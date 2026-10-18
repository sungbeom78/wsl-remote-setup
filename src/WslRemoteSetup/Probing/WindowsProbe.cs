using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Model;

namespace WslRemoteSetup.Probing;

/// <summary>
/// 읽기 전용 Windows 진단. 레지스트리·WMI·.NET 네트워크 API를 사용하고,
/// 현지화된 명령 출력 텍스트를 파싱하지 않는다. 배포판을 실행(기동)하지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProbe(ICommandRunner runner)
{
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string RemoteDesktopUsersSid = "S-1-5-32-555";

    private readonly List<ProbeError> _errors = [];

    public async Task<EnvironmentSnapshot> CollectAsync(CancellationToken ct = default)
    {
        _errors.Clear();
        var snapshot = new EnvironmentSnapshot
        {
            CollectedAt = DateTimeOffset.Now,
            Os = Try("os", ProbeOs),
            Identity = Try("identity", ProbeIdentity),
            Accounts = Try("accounts", ProbeAccounts),
            Interfaces = Try("network", ProbeInterfaces),
            Rdp = Try("rdp", ProbeRdp),
            Wsl = await TryAsync("wsl", () => ProbeWslAsync(ct)).ConfigureAwait(false),
            TcpListenPorts = Try("listeners", ProbeListeners),
            PortProxies = Try("portproxy", ProbePortProxy),
            FirewallRules = Try("firewall", ProbeFirewall),
        };
        return snapshot with { Errors = [.. _errors] };
    }

    private T? Try<T>(string area, Func<T> probe) where T : class
    {
        try { return probe(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _errors.Add(new ProbeError(area, $"{ex.GetType().Name}: {ex.Message}"));
            return null;
        }
    }

    private async Task<T?> TryAsync<T>(string area, Func<Task<T>> probe) where T : class
    {
        try { return await probe().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _errors.Add(new ProbeError(area, $"{ex.GetType().Name}: {ex.Message}"));
            return null;
        }
    }

    private static OsInfo ProbeOs()
    {
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
            ?? throw new InvalidOperationException("CurrentVersion 키를 열 수 없습니다.");
        string caption = "Windows";
        bool? hypervisor = null;
        using (var os = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
            foreach (ManagementObject o in os.Get()) caption = (string?)o["Caption"] ?? caption;
        using (var cs = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in cs.Get()) hypervisor = o["HypervisorPresent"] as bool?;

        return new OsInfo(
            Caption: caption,
            EditionId: cv.GetValue("EditionID") as string ?? "",
            DisplayVersion: cv.GetValue("DisplayVersion") as string ?? "",
            Build: int.TryParse(cv.GetValue("CurrentBuildNumber") as string, out var b) ? b : 0,
            Ubr: cv.GetValue("UBR") is int ubr ? ubr : 0,
            Is64Bit: Environment.Is64BitOperatingSystem,
            PendingReboot: ProbePendingReboot(),
            HypervisorPresent: hypervisor,
            VirtualMachinePlatform: OptionalFeature("VirtualMachinePlatform"),
            WslOptionalComponent: OptionalFeature("Microsoft-Windows-Subsystem-Linux"))
        {
            IsArm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64,
        };
    }

    private static bool? ProbePendingReboot()
    {
        using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return cbs is not null || wu is not null;
    }

    private static FeatureState OptionalFeature(string name)
    {
        // Win32_OptionalFeature.InstallState: 1 Enabled, 2 Disabled, 3 Absent, 4 Unknown
        using var searcher = new ManagementObjectSearcher(
            new SelectQuery("Win32_OptionalFeature", $"Name='{name}'", ["InstallState"]));
        foreach (ManagementObject o in searcher.Get())
        {
            return Convert.ToUInt32(o["InstallState"], CultureInfo.InvariantCulture) switch
            {
                1 => FeatureState.Enabled,
                2 => FeatureState.Disabled,
                3 => FeatureState.Absent,
                _ => FeatureState.Unknown,
            };
        }
        return FeatureState.Absent;
    }

    private static IdentityInfo ProbeIdentity()
    {
        using var id = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(id);
        var adminSid = new SecurityIdentifier(AdministratorsSid);
        var isMember = id.Groups?.Any(g => g.Equals(adminSid)) == true; // deny-only 포함
        return new IdentityInfo(
            id.Name,
            id.User?.Value ?? "",
            principal.IsInRole(WindowsBuiltInRole.Administrator),
            isMember,
            GetSystemMetrics(SM_REMOTESESSION) != 0);
    }

    private const int SM_REMOTESESSION = 0x1000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static IReadOnlyList<LocalAccountInfo> ProbeAccounts()
    {
        var admins = GroupMemberSids(AdministratorsSid);
        var rdpUsers = GroupMemberSids(RemoteDesktopUsersSid);
        var list = new List<LocalAccountInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, SID, Disabled FROM Win32_UserAccount WHERE LocalAccount=TRUE");
        foreach (ManagementObject o in searcher.Get())
        {
            var sid = (string)o["SID"];
            list.Add(new LocalAccountInfo((string)o["Name"], sid, !(bool)o["Disabled"], admins.Contains(sid), rdpUsers.Contains(sid)));
        }
        return list;
    }

    private static HashSet<string> GroupMemberSids(string groupSid)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? groupName = null, domain = null;
        using (var g = new ManagementObjectSearcher($"SELECT Name, Domain FROM Win32_Group WHERE LocalAccount=TRUE AND SID='{groupSid}'"))
            foreach (ManagementObject o in g.Get()) { groupName = (string)o["Name"]; domain = (string)o["Domain"]; }
        if (groupName is null) return result;

        // 현지화된 그룹 이름은 SID로 찾은 값을 그대로 사용한다.
        var groupPath = $"Win32_Group.Domain='{Escape(domain!)}',Name='{Escape(groupName)}'";
        using var members = new ManagementObjectSearcher($"ASSOCIATORS OF {{{groupPath}}} WHERE AssocClass=Win32_GroupUser ResultRole=PartComponent");
        foreach (ManagementObject m in members.Get())
            if (m["SID"] is string sid) result.Add(sid);
        return result;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static IReadOnlyList<NetworkInterfaceInfo> ProbeInterfaces()
    {
        var categories = ProbeNetworkCategories();
        var list = new List<NetworkInterfaceInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            bool? dhcp = null;
            int? index = null;
            try
            {
                var v4 = props.GetIPv4Properties();
                dhcp = v4.IsDhcpEnabled;
                index = v4.Index;
            }
            catch (NetworkInformationException) { }

            var ipv4 = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                .Where(u => !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(u => new Ipv4AddressInfo(u.Address.ToString(), u.PrefixLength))
                .ToList();
            var gateways = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                .Select(g => g.Address.ToString()).ToList();
            var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();

            list.Add(new NetworkInterfaceInfo(
                ni.Name, ni.Description, ni.OperationalStatus == OperationalStatus.Up,
                ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                dhcp, ipv4, gateways, dns,
                index is int i && categories.TryGetValue(i, out var c) ? c : NetworkCategory.Unknown));
        }
        return list;
    }

    internal static Dictionary<int, NetworkCategory> ProbeNetworkCategories()
    {
        var map = new Dictionary<int, NetworkCategory>();
        try
        {
            var scope = new ManagementScope(@"root\StandardCimv2");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT InterfaceIndex, NetworkCategory FROM MSFT_NetConnectionProfile"));
            foreach (ManagementObject o in searcher.Get())
            {
                var idx = Convert.ToInt32(o["InterfaceIndex"], CultureInfo.InvariantCulture);
                map[idx] = Convert.ToUInt32(o["NetworkCategory"], CultureInfo.InvariantCulture) switch
                {
                    0 => NetworkCategory.Public,
                    1 => NetworkCategory.Private,
                    2 => NetworkCategory.DomainAuthenticated,
                    _ => NetworkCategory.Unknown,
                };
            }
        }
        catch (ManagementException) { }
        return map;
    }

    internal static RdpInfo ProbeRdp()
    {
        const string ts = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
        using var root = Registry.LocalMachine.OpenSubKey(ts) ?? throw new InvalidOperationException("Terminal Server 키 없음");
        using var tcp = Registry.LocalMachine.OpenSubKey(ts + @"\WinStations\RDP-Tcp") ?? throw new InvalidOperationException("RDP-Tcp 키 없음");
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services");

        var deny = policy?.GetValue("fDenyTSConnections") as int? ?? root.GetValue("fDenyTSConnections") as int? ?? 1;
        var nla = policy?.GetValue("UserAuthentication") as int? ?? tcp.GetValue("UserAuthentication") as int? ?? 0;
        var port = tcp.GetValue("PortNumber") as int? ?? 3389;
        var overridden = policy?.GetValue("fDenyTSConnections") is not null || policy?.GetValue("UserAuthentication") is not null;
        return new RdpInfo(deny == 0, port, nla == 1, overridden);
    }

    private async Task<WslInfo> ProbeWslAsync(CancellationToken ct)
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        string? version = null;
        var installed = false;
        if (File.Exists(wsl))
        {
            // --version은 배포판을 기동하지 않는다. 출력 문구는 현지화되므로 숫자 패턴만 사용한다.
            var r = await runner.RunAsync(new CommandSpec(wsl, ["--version"]) { Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
            installed = r.ExitedZero;
            if (installed)
            {
                var first = r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(first, @"\d+(\.\d+){2,3}");
                version = m.Success ? m.Value : null;
            }
        }

        var distros = new List<WslDistroInfo>();
        using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is not null)
        {
            var defaultId = lxss.GetValue("DefaultDistribution") as string;
            foreach (var sub in lxss.GetSubKeyNames())
            {
                using var k = lxss.OpenSubKey(sub);
                if (k?.GetValue("DistributionName") is not string name) continue;
                distros.Add(new WslDistroInfo(
                    name,
                    k.GetValue("Version") as int? ?? 0,
                    k.GetValue("DefaultUid") as int? ?? 0,
                    string.Equals(sub, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }
        return new WslInfo(installed, version, distros);
    }

    private static IReadOnlyList<int> ProbeListeners() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Select(e => e.Port).Distinct().Order().ToList();

    internal static IReadOnlyList<PortProxyRule> ProbePortProxy()
    {
        var list = new List<PortProxyRule>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PortProxy\v4tov4\tcp");
        if (key is null) return list;
        foreach (var name in key.GetValueNames())
        {
            // 값 이름 "listenaddress/listenport", 값 "connectaddress/connectport"
            var listen = name.Split('/');
            var connect = (key.GetValue(name) as string ?? "").Split('/');
            if (listen.Length == 2 && connect.Length == 2
                && int.TryParse(listen[1], out var lp) && int.TryParse(connect[1], out var cp))
                list.Add(new PortProxyRule(listen[0], lp, connect[0], cp));
        }
        return list;
    }

    private static IReadOnlyList<FirewallRuleInfo> ProbeFirewall()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("방화벽 COM을 찾을 수 없습니다.");
        dynamic policy = Activator.CreateInstance(type)!;
        var list = new List<FirewallRuleInfo>();
        foreach (dynamic rule in policy.Rules)
        {
            if ((int)rule.Direction != 1) continue; // NET_FW_RULE_DIR_IN
            int proto = rule.Protocol;
            list.Add(new FirewallRuleInfo(
                Name: (string)rule.Name,
                DisplayName: (string)rule.Name,
                Enabled: (bool)rule.Enabled,
                Inbound: true,
                Allow: (int)rule.Action == 1,
                Profiles: (FirewallProfiles)((int)rule.Profiles & 7),
                Protocol: proto switch { 6 => "TCP", 17 => "UDP", 256 => "Any", _ => proto.ToString(CultureInfo.InvariantCulture) },
                LocalPorts: proto is 6 or 17 ? (string?)rule.LocalPorts ?? "*" : "*",
                RemoteAddresses: NormalizeRemote((string?)rule.RemoteAddresses))
            {
                ScopedToApplication = IsApplicationScoped(rule),
            });
        }
        return list;
    }

    /// <summary>프로그램·서비스·앱 패키지에 묶인 규칙인지. 이런 규칙은 다른 프로그램의 포트를 열지 않는다.</summary>
    private static bool IsApplicationScoped(dynamic rule)
    {
        if (!string.IsNullOrEmpty((string?)rule.ApplicationName)) return true;
        if (!string.IsNullOrEmpty((string?)rule.ServiceName) && (string?)rule.ServiceName != "*") return true;
        try
        {
            if (!string.IsNullOrEmpty((string?)rule.LocalAppPackageId)) return true; // INetFwRule3
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return false;
    }

    private static string NormalizeRemote(string? remote) => remote is null or "*" ? "*" : remote;
}
