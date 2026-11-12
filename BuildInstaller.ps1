# Run with Windows PowerShell 5.1 after building Internet Priority.exe.
[CmdletBinding()]
param(
    [string] $Executable = "$PSScriptRoot\dist\Internet Priority.exe",
    [string] $OutputPath = "$PSScriptRoot\dist\Internet Priority Setup.msi"
)

$ErrorActionPreference = 'Stop'

function Invoke-InstallerMember {
    param($Object, [string] $Name, [string] $Kind, [object[]] $Arguments = @())
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        if ($null -ne $Arguments[$index]) {
            $Arguments[$index] = $Arguments[$index].PSObject.BaseObject
        }
    }
    try {
        $Object.GetType().InvokeMember($Name, $Kind, $null, $Object, $Arguments)
    } catch {
        throw "Windows Installer $Name failed. $($_.Exception.Message)"
    }
}

function Release-InstallerObject {
    param($Object)
    if ($null -ne $Object) {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($Object)
    }
}

function Invoke-InstallerSql {
    param([string] $Sql, $Record = $null)
    $view = $null
    try {
        $view = Invoke-InstallerMember $database 'OpenView' 'InvokeMethod' @($Sql)
        [void] (Invoke-InstallerMember $view 'Execute' 'InvokeMethod' @($Record))
        [void] (Invoke-InstallerMember $view 'Close' 'InvokeMethod')
    } catch {
        throw "Windows Installer SQL failed: $Sql. $($_.Exception.Message)"
    } finally {
        Release-InstallerObject $view
    }
}

function Add-InstallerStream {
    param([string] $Name, [string] $File)
    $record = Invoke-InstallerMember $installer 'CreateRecord' 'InvokeMethod' @(2)
    try {
        [void] (Invoke-InstallerMember $record 'StringData' 'SetProperty' @(1, $Name))
        [void] (Invoke-InstallerMember $record 'SetStream' 'InvokeMethod' @(2, $File))
        Invoke-InstallerSql 'INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)' $record
    } finally {
        Release-InstallerObject $record
    }
}

function Add-InstallerSequence {
    param([string] $Table, [System.Collections.IDictionary] $Actions)
    Invoke-InstallerSql ('CREATE TABLE `{0}` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)' -f $Table)
    foreach ($action in $Actions.GetEnumerator()) {
        Invoke-InstallerSql ('INSERT INTO `{0}` (`Action`, `Sequence`) VALUES (''{1}'', {2})' -f $Table, $action.Key, $action.Value)
    }
}

$source = Get-Item -LiteralPath $Executable
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ($source.Extension -ne '.exe' -or $source.VersionInfo.ProductName -ne 'Internet Priority') {
    throw 'Build Internet Priority.exe before packaging it.'
}
if ([IO.Path]::GetExtension($destination) -ne '.msi') {
    throw 'The output path must have an .msi extension.'
}

$fileVersion = '{0}.{1}.{2}.{3}' -f $source.VersionInfo.FileMajorPart, $source.VersionInfo.FileMinorPart, $source.VersionInfo.FileBuildPart, $source.VersionInfo.FilePrivatePart
$productVersion = '{0}.{1}.{2}' -f $source.VersionInfo.FileMajorPart, $source.VersionInfo.FileMinorPart, $source.VersionInfo.FileBuildPart
$upgradeCode = '{66E98BEC-D945-4E1C-A7B5-2C7B1F69590C}'
# Keep one product code per three-part release. Package codes change with each build.
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $productHash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("$upgradeCode/$productVersion"))
    $productCode = [Guid]::new([byte[]] ($productHash[0..15])).ToString('B').ToUpperInvariant()
} finally {
    $sha.Dispose()
}
$packageCode = [Guid]::NewGuid().ToString('B').ToUpperInvariant()

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('InternetPriority-' + [Guid]::NewGuid().ToString('N'))
$stagedFile = Join-Path $scratch 'AppExecutable'
$cabinet = Join-Path $scratch 'payload.cab'
$stagedMsi = Join-Path $scratch 'setup.msi'
$installer = $null
$database = $null
$summary = $null

New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    # Cabinet member names must match the File table keys.
    Copy-Item -LiteralPath $source.FullName -Destination $stagedFile
    & "$env:SystemRoot\System32\makecab.exe" /D CompressionType=LZX $stagedFile $cabinet | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cabinet -PathType Leaf)) {
        throw 'MakeCab could not create the embedded payload.'
    }

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = Invoke-InstallerMember $installer 'OpenDatabase' 'InvokeMethod' @($stagedMsi, 3)

    $tables = @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL LOCALIZABLE PRIMARY KEY `Property`)'
        'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)'
        'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)'
        'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)'
        'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)'
        'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)'
        'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)'
        'CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)'
        'CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72), `DisplayResourceDLL` CHAR(255), `DisplayResourceId` SHORT, `DescriptionResourceDLL` CHAR(255), `DescriptionResourceId` SHORT PRIMARY KEY `Shortcut`)'
        'CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)'
        'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)'
    )
    foreach ($table in $tables) { Invoke-InstallerSql $table }

    $properties = [ordered] @{
        ProductName = 'Internet Priority'
        Manufacturer = 'Ryan Handler'
        ProductLanguage = '1033'
        ProductVersion = $productVersion
        ProductCode = $productCode
        UpgradeCode = $upgradeCode
        SecureCustomProperties = 'OLDERVERSIONS;NEWERVERSIONS'
        ARPNOMODIFY = '1'
    }
    foreach ($property in $properties.GetEnumerator()) {
        Invoke-InstallerSql ('INSERT INTO `Property` (`Property`, `Value`) VALUES (''{0}'', ''{1}'')' -f $property.Key, $property.Value)
    }

    $rows = @(
        'INSERT INTO `Directory` (`Directory`, `DefaultDir`) VALUES (''TARGETDIR'', ''SourceDir'')'
        'INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES (''LocalAppDataFolder'', ''TARGETDIR'', ''.'')'
        'INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES (''UserProgramsFolder'', ''LocalAppDataFolder'', ''Programs'')'
        'INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES (''INSTALLFOLDER'', ''UserProgramsFolder'', ''INTERN~1|Internet Priority'')'
        'INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES (''DesktopFolder'', ''TARGETDIR'', ''.'')'
        'INSERT INTO `Component` (`Component`, `ComponentId`, `Directory_`, `Attributes`, `KeyPath`) VALUES (''AppComponent'', ''{52E56C66-407E-4DFC-9679-FC13B0515E88}'', ''INSTALLFOLDER'', 4, ''AppKeyPath'')'
        'INSERT INTO `Component` (`Component`, `ComponentId`, `Directory_`, `Attributes`, `KeyPath`) VALUES (''DesktopComponent'', ''{8F06AB09-CB88-4D89-AD1E-19749F05CB1C}'', ''DesktopFolder'', 4, ''DesktopKeyPath'')'
        'INSERT INTO `Feature` (`Feature`, `Title`, `Display`, `Level`, `Attributes`) VALUES (''InternetPriority'', ''Internet Priority'', 1, 1, 0)'
        'INSERT INTO `FeatureComponents` (`Feature_`, `Component_`) VALUES (''InternetPriority'', ''AppComponent'')'
        'INSERT INTO `FeatureComponents` (`Feature_`, `Component_`) VALUES (''InternetPriority'', ''DesktopComponent'')'
        ('INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Version`, `Attributes`, `Sequence`) VALUES (''AppExecutable'', ''AppComponent'', ''INTERN~1.EXE|Internet Priority.exe'', {0}, ''{1}'', 16384, 1)' -f $source.Length, $fileVersion)
        'INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (1, 1, ''#payload.cab'')'
        'INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) VALUES (''AppKeyPath'', 1, ''Software\Ryan Handler\Internet Priority'', ''InstalledVersion'', ''[ProductVersion]'', ''AppComponent'')'
        'INSERT INTO `Registry` (`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) VALUES (''DesktopKeyPath'', 1, ''Software\Ryan Handler\Internet Priority'', ''DesktopShortcut'', ''#1'', ''DesktopComponent'')'
        'INSERT INTO `Shortcut` (`Shortcut`, `Directory_`, `Name`, `Component_`, `Target`, `Description`, `WkDir`) VALUES (''DesktopShortcut'', ''DesktopFolder'', ''INTERN~1|Internet Priority'', ''DesktopComponent'', ''[#AppExecutable]'', ''Switch Windows connection priority between Wi-Fi and Ethernet.'', ''INSTALLFOLDER'')'
        ('INSERT INTO `Upgrade` (`UpgradeCode`, `VersionMax`, `Attributes`, `ActionProperty`) VALUES (''{0}'', ''{1}'', 0, ''OLDERVERSIONS'')' -f $upgradeCode, $productVersion)
        ('INSERT INTO `Upgrade` (`UpgradeCode`, `VersionMin`, `Attributes`, `ActionProperty`) VALUES (''{0}'', ''{1}'', 2, ''NEWERVERSIONS'')' -f $upgradeCode, $productVersion)
        'INSERT INTO `LaunchCondition` (`Condition`, `Description`) VALUES (''Installed OR NOT NEWERVERSIONS'', ''A newer version of Internet Priority is already installed.'')'
    )
    foreach ($row in $rows) { Invoke-InstallerSql $row }

    Add-InstallerSequence 'InstallUISequence' ([ordered] @{
        FindRelatedProducts = 25
        LaunchConditions = 100
        CostInitialize = 800
        FileCost = 900
        CostFinalize = 1000
        ExecuteAction = 1300
    })
    Add-InstallerSequence 'InstallExecuteSequence' ([ordered] @{
        FindRelatedProducts = 25
        LaunchConditions = 100
        CostInitialize = 800
        FileCost = 900
        CostFinalize = 1000
        InstallValidate = 1400
        InstallInitialize = 1500
        RemoveExistingProducts = 1525
        ProcessComponents = 1600
        UnpublishFeatures = 1800
        RemoveRegistryValues = 2600
        RemoveShortcuts = 3200
        RemoveFiles = 3500
        InstallFiles = 4000
        CreateShortcuts = 4500
        WriteRegistryValues = 5000
        RegisterUser = 6000
        RegisterProduct = 6100
        PublishFeatures = 6300
        PublishProduct = 6400
        InstallFinalize = 6600
    })
    Add-InstallerSequence 'AdminExecuteSequence' ([ordered] @{
        CostInitialize = 800
        FileCost = 900
        CostFinalize = 1000
        InstallValidate = 1400
        InstallInitialize = 1500
        InstallAdminPackage = 3900
        InstallFiles = 4000
        InstallFinalize = 6600
    })

    Add-InstallerStream 'payload.cab' $cabinet
    $summary = Invoke-InstallerMember $database 'SummaryInformation' 'GetProperty' @(20)
    # Summary word count 10 specifies compressed media and a non-elevated package.
    $summaryProperties = [ordered] @{
        1 = 1252
        2 = 'Installation Database'
        3 = 'Internet Priority'
        4 = 'Ryan Handler'
        5 = 'Installer, MSI'
        7 = 'Intel;1033'
        9 = $packageCode
        14 = 405
        15 = 10
        19 = 2
    }
    foreach ($entry in $summaryProperties.GetEnumerator()) {
        [void] (Invoke-InstallerMember $summary 'Property' 'SetProperty' @([int] $entry.Key, $entry.Value))
    }
    [void] (Invoke-InstallerMember $summary 'Persist' 'InvokeMethod')
    [void] (Invoke-InstallerMember $database 'Commit' 'InvokeMethod')
    Release-InstallerObject $summary
    $summary = $null
    Release-InstallerObject $database
    $database = $null

    Copy-Item -LiteralPath $stagedMsi -Destination $destination -Force
    Get-Item -LiteralPath $destination
} finally {
    Release-InstallerObject $summary
    Release-InstallerObject $database
    Release-InstallerObject $installer
    Remove-Item -LiteralPath $stagedFile, $cabinet, $stagedMsi -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $scratch -ErrorAction SilentlyContinue
}
