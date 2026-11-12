using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace InternetPriority
{
    internal enum Preference
    {
        WiFi,
        Ethernet,
        Connection,
        WindowsDefaults
    }

    internal sealed class DetectedConnection
    {
        public DetectedConnection(string kind, string name, string status, Guid? adapterId, string adapterAlias)
        {
            Kind = kind;
            Name = name;
            Status = status;
            AdapterId = adapterId;
            AdapterAlias = adapterAlias;
        }

        public string Kind { get; private set; }
        public string Name { get; private set; }
        public string Status { get; private set; }
        public Guid? AdapterId { get; private set; }
        public string AdapterAlias { get; private set; }
    }

    internal sealed class NetworkStatus
    {
        public NetworkStatus(string priorityLabel, string wifiLabel, string ethernetLabel, DetectedConnection[] connections)
        {
            PriorityLabel = priorityLabel;
            WiFiLabel = wifiLabel;
            EthernetLabel = ethernetLabel;
            Connections = connections;
        }

        public string PriorityLabel { get; private set; }
        public string WiFiLabel { get; private set; }
        public string EthernetLabel { get; private set; }
        public DetectedConnection[] Connections { get; private set; }
    }

    internal static class NetworkService
    {
        private const int CancelledByUser = 1223;
        private const int StatusTimeoutMs = 40000;
        private const string StatusPrefix = "InternetPriority:";
        private const string ConnectionPrefix = "InternetConnection:";
        private const string IpHelperLibrary = "iphlpapi.dll";
        private const uint ProtocolNotSupported = 50;
        private const uint NetworkUnreachable = 1231;
        private const uint HostUnreachable = 1232;

        [DllImport(IpHelperLibrary, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetBestInterfaceEx([In] byte[] destinationAddress, out uint interfaceIndex);

        [DllImport(IpHelperLibrary, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);

        [DllImport(IpHelperLibrary, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint ConvertInterfaceLuidToAlias(ref ulong interfaceLuid, [Out] StringBuilder alias, UIntPtr length);

        public static NetworkStatus ReadStatus()
        {
            string output;
            try
            {
                if (RunPowerShell(ReadScript, true, out output) != 0)
                {
                    throw new InvalidOperationException("Windows could not read the network settings. Try Refresh.");
                }
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException("Windows could not open its network settings.", error);
            }

            var connections = new List<DetectedConnection>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith(ConnectionPrefix, StringComparison.Ordinal))
                {
                    string[] fields = line.Substring(ConnectionPrefix.Length).Split('|');
                    if (fields.Length == 5)
                    {
                        Guid id;
                        Guid? adapterId = Guid.TryParseExact(fields[3], "D", out id) && id != Guid.Empty
                            ? (Guid?)id : null;
                        connections.Add(new DetectedConnection(
                            fields[0], DecodeLabel(fields[2]), fields[1], adapterId, DecodeLabel(fields[4])));
                    }
                }
                else if (line.StartsWith(StatusPrefix, StringComparison.Ordinal))
                {
                    string[] labels = line.Substring(StatusPrefix.Length).Split('|');
                    if (labels.Length == 3)
                    {
                        return new NetworkStatus(DecodeLabel(labels[0]), labels[1], labels[2], connections.ToArray());
                    }
                }
            }

            throw new InvalidOperationException("Windows returned incomplete network settings. Try Refresh.");
        }

        private static string DecodeLabel(string label)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(label));
        }

        public static string ReadActiveInternetLabel()
        {
            // Look up routes to two public addresses in each family. Windows sends no traffic.
            uint ipv4Primary = FindRouteInterface("1.1.1.1");
            uint ipv4Secondary = FindRouteInterface("8.8.8.8");
            uint ipv6Primary = FindRouteInterface("2606:4700:4700::1111");
            uint ipv6Secondary = FindRouteInterface("2001:4860:4860::8888");

            return DescribeInternetRoutes(ipv4Primary, ipv4Secondary, ipv6Primary, ipv6Secondary, GetRouteInterfaceLabel);
        }

        private static uint FindRouteInterface(string address)
        {
            SocketAddress socketAddress = new IPEndPoint(IPAddress.Parse(address), 0).Serialize();
            var destination = new byte[socketAddress.Size];
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = socketAddress[i];
            }

            uint index;
            uint result = GetBestInterfaceEx(destination, out index);
            if (result == 0) { return index; }
            if (result == ProtocolNotSupported || result == NetworkUnreachable || result == HostUnreachable) { return 0; }
            throw new Win32Exception(unchecked((int)result));
        }

        private static string DescribeInternetRoutes(uint ipv4Primary, uint ipv4Secondary,
            uint ipv6Primary, uint ipv6Secondary, Func<uint, string> getLabel)
        {
            if (ipv4Primary != ipv4Secondary || ipv6Primary != ipv6Secondary)
            {
                var indices = new HashSet<uint>();
                var labels = new List<string>();
                foreach (uint index in new[] { ipv4Primary, ipv4Secondary, ipv6Primary, ipv6Secondary })
                {
                    if (index != 0 && indices.Add(index))
                    {
                        labels.Add(getLabel(index));
                    }
                }
                return "Varies by destination (" + string.Join(" / ", labels) + ")";
            }

            if (ipv4Primary == 0 && ipv6Primary == 0) { return "No route found"; }
            if (ipv4Primary == ipv6Primary) { return getLabel(ipv4Primary); }
            if (ipv4Primary == 0) { return getLabel(ipv6Primary) + " (IPv6 only)"; }
            if (ipv6Primary == 0) { return getLabel(ipv4Primary) + " (IPv4 only)"; }
            return "IPv4: " + getLabel(ipv4Primary) + " / IPv6: " + getLabel(ipv6Primary);
        }

        private static string GetRouteInterfaceLabel(uint index)
        {
            ulong luid;
            if (ConvertInterfaceIndexToLuid(index, out luid) == 0)
            {
                var alias = new StringBuilder(257); // IF_MAX_STRING_SIZE plus the terminating null.
                if (ConvertInterfaceLuidToAlias(ref luid, alias, (UIntPtr)(uint)alias.Capacity) == 0 && alias.Length > 0)
                {
                    return alias.ToString();
                }
            }

            return "Interface " + index;
        }

        public static void ApplyWithElevation(Preference preference, Guid? adapterId = null)
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--apply " + PreferenceArgument(preference, adapterId),
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            int exitCode;
            try
            {
                using (Process worker = Process.Start(startInfo))
                {
                    if (worker == null)
                    {
                        throw new InvalidOperationException("Windows could not start the priority change.");
                    }

                    worker.WaitForExit();
                    exitCode = worker.ExitCode;
                }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == CancelledByUser)
                {
                    throw new OperationCanceledException("Administrator approval was cancelled.", error);
                }

                throw new InvalidOperationException("Windows could not start the change with administrator approval.", error);
            }

            switch (exitCode)
            {
                case 0:
                    return;
                case 2:
                    if (preference == Preference.WindowsDefaults)
                    {
                        throw new InvalidOperationException("Windows did not find any configurable Wi-Fi or Ethernet adapters.");
                    }
                    throw new InvalidOperationException("Connect or enable both a Wi-Fi adapter and an Ethernet adapter, then try again.");
                case 3:
                    throw new InvalidOperationException("Windows rejected the change. The previous priorities were restored.");
                case 4:
                    throw new InvalidOperationException("Windows could not restore every prior setting. Refresh to check the current priority.");
                case 6:
                    throw new InvalidOperationException("Administrator approval is needed to change network priority.");
                case 8:
                    throw new InvalidOperationException("Another priority change is running. Wait for it to finish and try again.");
                case 9:
                    throw new InvalidOperationException("The selected adapter is no longer available to configure. Refresh and choose an adapter again.");
                default:
                    throw new InvalidOperationException("Windows could not change the adapter priorities. Refresh and try again.");
            }
        }

        public static int RunElevated(Preference preference, Guid? adapterId = null)
        {
            string request = PreferenceArgument(preference, adapterId);
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    return 6;
                }
            }

            try
            {
                string output;
                string script = "$priorityRequest = '" + request + "'\r\n" + ApplyScript;
                return RunPowerShell(script, false, out output);
            }
            catch (Win32Exception)
            {
                return 5;
            }
            catch (InvalidOperationException)
            {
                return 5;
            }
        }

        private static string PreferenceArgument(Preference preference, Guid? adapterId)
        {
            switch (preference)
            {
                case Preference.WiFi:
                    return "wifi";
                case Preference.Ethernet:
                    return "ethernet";
                case Preference.WindowsDefaults:
                    return "defaults";
                case Preference.Connection:
                    if (!adapterId.HasValue || adapterId.Value == Guid.Empty)
                    {
                        throw new ArgumentException("Choose an adapter to prefer.", "adapterId");
                    }
                    return "adapter " + adapterId.Value.ToString("D");
                default:
                    throw new ArgumentOutOfRangeException("preference");
            }
        }

        private static int RunPowerShell(string script, bool readOnly, out string output)
        {
            string systemDirectory = Environment.SystemDirectory;
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                systemDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                WorkingDirectory = systemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Windows could not open its network settings.");
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (readOnly && !process.WaitForExit(StatusTimeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // The query exited during the timeout check.
                    }

                    throw new InvalidOperationException("Windows took too long to read the network settings. Try Refresh.");
                }

                // A write is allowed to finish or roll back even if Windows is slow.
                process.WaitForExit();
                Task.WaitAll(stdout, stderr);
                output = stdout.Result;
                return process.ExitCode;
            }
        }

        private const string AdapterScript = @"
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:PSModulePath = Join-Path $PSHOME 'Modules'

function Get-PhysicalLinks {
    foreach ($adapter in @(NetAdapter\Get-NetAdapter -Physical)) {
        $kind = $null
        if ($adapter.InterfaceType -eq 71 -or $adapter.NdisPhysicalMedium -in 1, 9) {
            $kind = 'Wi-Fi'
        } elseif ($adapter.InterfaceType -eq 6 -and $adapter.NdisPhysicalMedium -in 0, 14) {
            $kind = 'Ethernet'
        }

        if ($null -ne $kind) {
            $guid = [Guid]::Empty
            $adapterId = ''
            if ([Guid]::TryParse([string]$adapter.InterfaceGuid, [ref]$guid) -and $guid -ne [Guid]::Empty) {
                $adapterId = $guid.ToString('D')
            }
            $name = [string]$adapter.Name
            if ([string]::IsNullOrWhiteSpace($name)) { $name = '{0} {1}' -f $kind, $adapter.InterfaceIndex }
            [pscustomobject]@{
                Index = [uint32]$adapter.InterfaceIndex
                Kind = $kind
                Name = $name
                AdapterId = $adapterId
                Connected = $adapter.InterfaceOperationalStatus -eq 1
                Enabled = $adapter.InterfaceAdminStatus -ne 2
            }
        }
    }
}

function Get-PhysicalIpRows($links) {
    $interfaces = @(NetTCPIP\Get-NetIPInterface -PolicyStore ActiveStore)
    foreach ($link in $links) {
        foreach ($interface in @($interfaces | Where-Object InterfaceIndex -eq $link.Index)) {
            if ($interface.AddressFamily -in 'IPv4', 'IPv6') {
                [pscustomobject]@{
                    Index = $link.Index
                    Kind = $link.Kind
                    Name = $link.Name
                    AdapterId = $link.AdapterId
                    Family = [string]$interface.AddressFamily
                    Metric = [uint32]$interface.InterfaceMetric
                    AutomaticMetric = [string]$interface.AutomaticMetric
                }
            }
        }
    }
}

function Get-DefaultRoutes {
    NetTCPIP\Get-NetRoute -PolicyStore ActiveStore |
        Where-Object { $_.DestinationPrefix -in '0.0.0.0/0', '::/0' }
}
";

        private const string ReadScript = AdapterScript + @"
function Get-LinkLabel($links, $kind) {
    $matchesForKind = @($links | Where-Object Kind -eq $kind)
    if ($matchesForKind.Count -eq 0) { return 'Not detected' }
    if (@($matchesForKind | Where-Object Connected).Count -gt 0) { return 'Connected' }
    if (@($matchesForKind | Where-Object Enabled).Count -eq 0) { return 'Disabled' }
    return 'Disconnected'
}

function Get-RouteCosts($rows, $routes) {
    foreach ($row in $rows) {
        foreach ($route in @($routes | Where-Object InterfaceIndex -eq $row.Index)) {
            [long]$route.RouteMetric + $row.Metric
        }
    }
}

function Get-Priority($rows, $routes, $family) {
    $familyRows = @($rows | Where-Object Family -eq $family)
    if ($familyRows.Count -eq 0) { return }
    $manual = @($familyRows | Where-Object AutomaticMetric -eq 'Disabled')
    if ($manual.Count -eq 0) { return 'Windows automatic' }

    if ($manual.Count -eq $familyRows.Count) {
        $first = @($manual | Where-Object Metric -eq 10)
        $fallback = @($manual | Where-Object Metric -ge 500)
        if ($first.Count -eq 1 -and $fallback.Count -eq $manual.Count - 1) {
            return ('{0} first' -f $first[0].Name)
        }
    }

    $wireless = @($familyRows | Where-Object Kind -eq 'Wi-Fi')
    $wired = @($familyRows | Where-Object Kind -eq 'Ethernet')
    if ($wireless.Count -eq 0 -or $wired.Count -eq 0) { return }

    $familyRoutes = @($routes | Where-Object AddressFamily -eq $family)
    $wirelessCosts = @(Get-RouteCosts $wireless $familyRoutes)
    $wiredCosts = @(Get-RouteCosts $wired $familyRoutes)

    # Compare live gateway costs when both choices have a gateway.
    # Otherwise compare their saved interface priorities.
    if ($wirelessCosts.Count -gt 0 -and $wiredCosts.Count -gt 0) {
        $wirelessMetric = ($wirelessCosts | Measure-Object -Minimum).Minimum
        $wiredMetric = ($wiredCosts | Measure-Object -Minimum).Minimum
    } else {
        $wirelessMetric = ($wireless | Measure-Object -Property Metric -Minimum).Minimum
        $wiredMetric = ($wired | Measure-Object -Property Metric -Minimum).Minimum
    }

    if ($wirelessMetric -lt $wiredMetric) { return 'Wi-Fi first' }
    if ($wiredMetric -lt $wirelessMetric) { return 'Ethernet first' }
    return 'Equal priority'
}

function ConvertTo-ProtocolLabel($label) {
    # Adapter names can contain delimiters, non-ASCII characters or newlines.
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$label))
}

function Write-Connection($kind, $name, $status, $adapterId = '', $adapterAlias = '') {
    $encodedName = ConvertTo-ProtocolLabel $name
    $encodedAlias = ConvertTo-ProtocolLabel $adapterAlias
    [Console]::WriteLine(('InternetConnection:{0}|{1}|{2}|{3}|{4}' -f $kind, $status, $encodedName, $adapterId, $encodedAlias))
}

function Write-DetectedConnections($links) {
    $profiles = @{}
    if (@($links | Where-Object { $_.Kind -eq 'Wi-Fi' -and $_.Connected }).Count -gt 0) {
        try {
            foreach ($profile in @(NetConnection\Get-NetConnectionProfile)) {
                if (-not [string]::IsNullOrWhiteSpace($profile.Name)) {
                    $profiles[[uint32]$profile.InterfaceIndex] = $profile.Name
                }
            }
        } catch {
            # Connection profiles are optional. Adapter aliases are still usable.
        }
    }

    foreach ($link in @($links | Sort-Object @{Expression='Connected'; Descending=$true}, Kind, Index)) {
        $name = $link.Name
        if ($link.Kind -eq 'Wi-Fi' -and $link.Connected -and $profiles.ContainsKey($link.Index)) {
            $name = $profiles[$link.Index]
        }

        $status = 'Disconnected'
        if (-not $link.Enabled) { $status = 'Disabled' }
        elseif ($link.Connected) { $status = 'Connected' }
        Write-Connection $link.Kind $name $status $link.AdapterId $link.Name
    }

    $vpnNames = @{}
    try {
        $physicalNames = @($links | ForEach-Object Name)
        $vpnMarker = '\b(?:VPN|WireGuard|Wintun|OpenVPN|TAP-Windows|ProTUN|ProtonVPN|NordLynx|Tailscale|ZeroTier)\b|\bWAN Miniport \((?:IKEv2|SSTP|L2TP|PPTP)\)'
        foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($adapter.OperationalStatus -ne 'Up' -or $adapter.Name -in $physicalNames -or
                $adapter.NetworkInterfaceType -in 'Loopback', 'Wireless80211' -or
                ($adapter.Name -notmatch $vpnMarker -and $adapter.Description -notmatch $vpnMarker)) {
                continue
            }

            # Ignore packet filters and idle virtual adapters without an IP address.
            if ($adapter.GetIPProperties().UnicastAddresses.Count -gt 0) {
                $vpnNames[$adapter.Name] = $true
            }
        }
    } catch {
        # Third-party tunnel discovery is optional.
    }

    foreach ($name in @($vpnNames.Keys | Sort-Object)) {
        Write-Connection 'VPN' $name 'Connected'
    }
}

try {
    $links = @(Get-PhysicalLinks)
    $rows = @(Get-PhysicalIpRows $links)
    $routes = @(Get-DefaultRoutes)
    $priorities = @(
        Get-Priority $rows $routes 'IPv4'
        Get-Priority $rows $routes 'IPv6'
    ) | Select-Object -Unique
    $priorities = @($priorities)

    $priority = 'One connection type available'
    if ($rows.Count -eq 0) { $priority = 'No configurable adapters' }
    if ($priorities.Count -eq 1) { $priority = $priorities[0] }
    if ($priorities.Count -gt 1) { $priority = 'Priority differs by IP version' }
    $wifiLabel = Get-LinkLabel $links 'Wi-Fi'
    $ethernetLabel = Get-LinkLabel $links 'Ethernet'
    Write-DetectedConnections $links
    $encodedPriority = ConvertTo-ProtocolLabel $priority
    [Console]::WriteLine(('InternetPriority:{0}|{1}|{2}' -f $encodedPriority, $wifiLabel, $ethernetLabel))
    exit 0
} catch {
    exit 5
}
";

        private const string ApplyScript = AdapterScript + @"
function Set-PriorityMetric($row, $automaticMetric, $metric) {
    $settings = @{
        InterfaceIndex = $row.Index
        AddressFamily = $row.Family
        AutomaticMetric = $automaticMetric
    }
    if ($automaticMetric -eq 'Disabled') { $settings.InterfaceMetric = $metric }
    # Without PolicyStore, Windows saves the change in both policy stores.
    NetTCPIP\Set-NetIPInterface @settings
}

function Test-PriorityMetric($row, $automaticMetric, $metric) {
    $null -ne $row -and $row.AutomaticMetric -eq $automaticMetric -and
        ($automaticMetric -eq 'Enabled' -or $row.Metric -eq $metric)
}

function Find-PriorityRow($rows, $prior) {
    $matching = @($rows | Where-Object {
        $_.Family -eq $prior.Family -and
            (($_.AdapterId -ne '' -and $_.AdapterId -eq $prior.AdapterId) -or
                ($prior.AdapterId -eq '' -and $_.Index -eq $prior.Index -and $_.Name -eq $prior.Name))
    })
    if ($matching.Count -eq 1) { return $matching[0] }
}

try {
    $links = @(Get-PhysicalLinks)
    $rows = @(Get-PhysicalIpRows $links)
    $mode, $selectedAdapter = $priorityRequest -split ' ', 2

    $plan = @(
        if ($mode -eq 'defaults') {
            foreach ($row in $rows) {
                [pscustomobject]@{ Prior = $row; AutomaticMetric = 'Enabled'; Metric = $null }
            }
        } else {
            if ($mode -eq 'adapter') {
                $preferredRows = @($rows | Where-Object AdapterId -eq $selectedAdapter)
                if ($preferredRows.Count -eq 0) { exit 9 }
                $fallbackRows = @($rows | Where-Object AdapterId -ne $selectedAdapter)
            } else {
                $kind = 'Ethernet'
                if ($mode -eq 'wifi') { $kind = 'Wi-Fi' }
                $preferredRows = @($rows | Where-Object Kind -eq $kind)
                $fallbackRows = @($rows | Where-Object Kind -ne $kind)
            }

            $routes = @(Get-DefaultRoutes)
            foreach ($family in 'IPv4', 'IPv6') {
                $preferred = @($preferredRows | Where-Object Family -eq $family | Sort-Object Metric, Index)
                $fallback = @($fallbackRows | Where-Object Family -eq $family | Sort-Object Metric, Index)
                if ($preferred.Count -eq 0 -or ($mode -ne 'adapter' -and $fallback.Count -eq 0)) { continue }

                $preferredIndexes = @($preferred | ForEach-Object Index)
                $preferredRouteMetric = ($routes |
                    Where-Object { $_.AddressFamily -eq $family -and $_.InterfaceIndex -in $preferredIndexes } |
                    Measure-Object -Property RouteMetric -Maximum).Maximum

                # Leave room for DHCP or IPv6 router-advertisement gateway metrics.
                $preferredMetric = 10
                $fallbackMetric = [Math]::Max(500, [int]$preferredRouteMetric + $preferred.Count + 100)
                foreach ($row in $preferred) {
                    [pscustomobject]@{ Prior = $row; AutomaticMetric = 'Disabled'; Metric = [uint32]$preferredMetric }
                    $preferredMetric++
                }
                foreach ($row in $fallback) {
                    [pscustomobject]@{ Prior = $row; AutomaticMetric = 'Disabled'; Metric = [uint32]$fallbackMetric }
                    $fallbackMetric++
                }
            }
        }
    )
    if ($plan.Count -eq 0) { exit 2 }
} catch {
    exit 5
}

$attempted = New-Object 'System.Collections.Generic.List[object]'
try {
    foreach ($item in $plan) {
        $row = $item.Prior
        if (Test-PriorityMetric $row $item.AutomaticMetric $item.Metric) { continue }
        $attempted.Add($row)
        Set-PriorityMetric $row $item.AutomaticMetric $item.Metric
    }

    $applied = @(Get-PhysicalIpRows @(Get-PhysicalLinks))
    foreach ($item in $plan) {
        $match = Find-PriorityRow $applied $item.Prior
        if (-not (Test-PriorityMetric $match $item.AutomaticMetric $item.Metric)) {
            throw 'Windows did not retain an adapter priority.'
        }
    }
    exit 0
} catch {
    $rollbackFailed = $false
    try {
        $current = @(Get-PhysicalIpRows @(Get-PhysicalLinks))
    } catch {
        exit 4
    }
    for ($index = $attempted.Count - 1; $index -ge 0; $index--) {
        $row = $attempted[$index]
        $match = Find-PriorityRow $current $row
        try {
            if ($null -eq $match) { throw 'The adapter is no longer available.' }
            Set-PriorityMetric $match $row.AutomaticMetric $row.Metric
        } catch {
            $rollbackFailed = $true
        }
    }
    if ($rollbackFailed) { exit 4 }
    try {
        $restored = @(Get-PhysicalIpRows @(Get-PhysicalLinks))
        foreach ($row in $attempted) {
            $match = Find-PriorityRow $restored $row
            if (-not (Test-PriorityMetric $match $row.AutomaticMetric $row.Metric)) { exit 4 }
        }
    } catch {
        exit 4
    }
    exit 3
}
";
    }
}
