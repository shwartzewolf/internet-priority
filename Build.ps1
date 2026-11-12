#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory, ParameterSetName = 'VerifySigned')]
    [string] $VerifySignedInstaller
)

$ErrorActionPreference = 'Stop'

function Invoke-MsiMember {
    param($Object, [string] $Name, [string] $Kind, [object[]] $Arguments = @())
    $Object.GetType().InvokeMember($Name, $Kind, $null, $Object, $Arguments)
}

function Read-InstallerRecord {
    param($Database, [string] $Query, [scriptblock] $Read)
    $view = $null
    $record = $null
    try {
        $view = Invoke-MsiMember $Database 'OpenView' 'InvokeMethod' @($Query)
        [void] (Invoke-MsiMember $view 'Execute' 'InvokeMethod')
        $record = Invoke-MsiMember $view 'Fetch' 'InvokeMethod'
        if ($null -eq $record) { throw "Missing expected installer data: $Query" }
        & $Read $record
    } finally {
        if ($null -ne $record) { [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        if ($null -ne $view) { [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
    }
}

function Test-InstallerPayload {
    param([string] $InstallerPath, [string] $ExecutablePath)
    $installerFile = (Get-Item -LiteralPath $InstallerPath).FullName
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('InternetPriority-verify-' + [guid]::NewGuid().ToString('N'))
    $cabinet = Join-Path $scratch 'payload.cab'
    $extractDirectory = Join-Path $scratch 'extract'
    $member = Join-Path $extractDirectory 'AppExecutable'
    $embeddedApp = Join-Path $scratch 'Internet Priority.exe'
    $installer = $null
    $database = $null

    New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = Invoke-MsiMember $installer 'OpenDatabase' 'InvokeMethod' @($installerFile, 0)
        $packageVersion = Read-InstallerRecord $database 'SELECT `Value` FROM `Property` WHERE `Property`=''ProductVersion''' {
            param($record)
            Invoke-MsiMember $record 'StringData' 'GetProperty' @(1)
        }
        $media = Read-InstallerRecord $database 'SELECT `Cabinet` FROM `Media` WHERE `DiskId`=1' {
            param($record)
            Invoke-MsiMember $record 'StringData' 'GetProperty' @(1)
        }
        if ($media -notmatch '^#[A-Za-z0-9_.-]+\.cab$') {
            throw 'The installer must contain an embedded cabinet.'
        }

        # ReadStream format 1 maps each raw byte to the Unicode code point with the same value.
        $query = 'SELECT `Data` FROM `_Streams` WHERE `Name`=''{0}''' -f $media.Substring(1)
        $stream = Read-InstallerRecord $database $query {
            param($record)
            $length = Invoke-MsiMember $record 'DataSize' 'GetProperty' @(1)
            Invoke-MsiMember $record 'ReadStream' 'InvokeMethod' @(1, [int] $length, 1)
        }
        [IO.File]::WriteAllBytes($cabinet, [Text.Encoding]::GetEncoding(28591).GetBytes($stream))
        & "$env:WINDIR\System32\expand.exe" $cabinet '-F:AppExecutable' '-R' $extractDirectory | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $member -PathType Leaf)) {
            throw 'The installer did not contain the app executable.'
        }
        Move-Item -LiteralPath $member -Destination $embeddedApp

        $app = Get-Item -LiteralPath $embeddedApp
        $version = [version] $app.VersionInfo.ProductVersion
        if ($app.VersionInfo.ProductName -ne 'Internet Priority' -or $version.ToString(3) -ne $packageVersion) {
            throw 'The installer metadata does not match the embedded app.'
        }

        if ($ExecutablePath) {
            if ((Get-FileHash -LiteralPath $embeddedApp).Hash -ne (Get-FileHash -LiteralPath $ExecutablePath).Hash) {
                throw 'The executable in the installer does not match the compiled app.'
            }
        } else {
            foreach ($file in @($installerFile, $embeddedApp)) {
                $signature = Get-AuthenticodeSignature -FilePath $file
                if ($signature.Status -ne 'Valid' -or
                    $signature.SignerCertificate.GetNameInfo('SimpleName', $false) -ne 'SignPath Foundation' -or
                    $null -eq $signature.TimeStamperCertificate) {
                    throw "Expected a valid, timestamped SignPath Foundation signature on $file."
                }
            }
        }

        $version.ToString()
    } finally {
        if ($null -ne $database) { [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        if ($null -ne $installer) { [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
        Remove-Item -LiteralPath $member, $embeddedApp, $cabinet -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $extractDirectory, $scratch -ErrorAction SilentlyContinue
    }
}

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with Windows PowerShell (powershell.exe).'
}

if ($PSCmdlet.ParameterSetName -eq 'VerifySigned') {
    Test-InstallerPayload -InstallerPath $VerifySignedInstaller
    return
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The .NET Framework 4.x C# compiler is missing.'
}
$dist = Join-Path $PSScriptRoot 'dist'
$exePath = Join-Path $dist 'Internet Priority.exe'
$msiPath = Join-Path $dist 'Internet Priority Setup.msi'
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$sources = @('AssemblyInfo.cs', 'Program.cs', 'MainForm.cs', 'NetworkService.cs') | ForEach-Object {
    Join-Path $PSScriptRoot $_
}
$compilerArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/langversion:5', '/optimize+', '/warnaserror+', '/codepage:65001',
    "/win32manifest:$PSScriptRoot\app.manifest", "/win32icon:$PSScriptRoot\app.ico",
    "/resource:$PSScriptRoot\LICENSE,InternetPriority.License.txt", "/out:$exePath",
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll'
)
& $compiler @compilerArgs @sources
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }

& (Join-Path $PSScriptRoot 'BuildInstaller.ps1') -Executable $exePath -OutputPath $msiPath | Out-Null
$version = Test-InstallerPayload -InstallerPath $msiPath -ExecutablePath $exePath
[pscustomobject] @{ Version = $version; Executable = $exePath; Installer = $msiPath }
