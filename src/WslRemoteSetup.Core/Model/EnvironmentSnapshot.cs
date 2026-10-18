namespace WslRemoteSetup.Core.Model;

public enum FeatureState { Unknown, Enabled, Disabled, EnablePending, Absent }

public enum NetworkCategory { Unknown, Public, Private, DomainAuthenticated }

[Flags]
public enum FirewallProfiles { None = 0, Domain = 1, Private = 2, Public = 4, All = 7 }

/// <summary>
/// 읽기 전용 진단 결과. 각 영역은 조회 실패 시 null이고 <see cref="Errors"/>에 원인을 남긴다.
/// "조회 실패"와 "없음"을 구분하기 위함이다.
/// </summary>
public sealed record EnvironmentSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset CollectedAt { get; init; }
    public OsInfo? Os { get; init; }
    public IdentityInfo? Identity { get; init; }
    public IReadOnlyList<LocalAccountInfo>? Accounts { get; init; }
    public IReadOnlyList<NetworkInterfaceInfo>? Interfaces { get; init; }
    public RdpInfo? Rdp { get; init; }
    public WslInfo? Wsl { get; init; }
    public IReadOnlyList<int>? TcpListenPorts { get; init; }
    public IReadOnlyList<PortProxyRule>? PortProxies { get; init; }
    public IReadOnlyList<FirewallRuleInfo>? FirewallRules { get; init; }
    public IReadOnlyList<ProbeError> Errors { get; init; } = [];
}

public sealed record ProbeError(string Area, string Message);

public sealed record OsInfo(
    string Caption,
    string EditionId,
    string DisplayVersion,
    int Build,
    int Ubr,
    bool Is64Bit,
    bool? PendingReboot,
    bool? HypervisorPresent,
    FeatureState VirtualMachinePlatform,
    FeatureState WslOptionalComponent)
{
    /// <summary>RDP 호스트 기능이 없는 Home 계열.</summary>
    public bool IsHomeEdition => EditionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
}

public sealed record IdentityInfo(string UserName, string Sid, bool IsElevated, bool IsAdministratorsMember, bool IsRemoteSession);

public sealed record LocalAccountInfo(string Name, string Sid, bool Enabled, bool IsAdministrator, bool IsRemoteDesktopUser);

public sealed record Ipv4AddressInfo(string Address, int PrefixLength);

public sealed record NetworkInterfaceInfo(
    string Name,
    string Description,
    bool IsUp,
    bool IsWireless,
    bool? DhcpEnabled,
    IReadOnlyList<Ipv4AddressInfo> Ipv4,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    NetworkCategory Category);

public sealed record RdpInfo(bool Enabled, int Port, bool NlaRequired, bool PolicyOverride);

public sealed record WslDistroInfo(string Name, int Version, int DefaultUid, bool IsDefault);

public sealed record WslInfo(
    bool RuntimeInstalled,
    string? RuntimeVersion,
    IReadOnlyList<WslDistroInfo> DistrosForCurrentUser);

public sealed record PortProxyRule(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort);

public sealed record FirewallRuleInfo(
    string Name,
    string DisplayName,
    bool Enabled,
    bool Inbound,
    bool Allow,
    FirewallProfiles Profiles,
    string Protocol,
    string LocalPorts,
    string RemoteAddresses)
{
    /// <summary>프로그램·서비스·앱 패키지에 한정된 규칙(그 프로그램의 수신에만 적용).</summary>
    public bool ScopedToApplication { get; init; }

    /// <summary>포트 목록에 해당 TCP 포트가 명시(번호·범위)된 규칙인지.</summary>
    public bool ExplicitlyAllowsTcpPort(int port) =>
        Protocol is "TCP" or "Any" && !IsAllPorts(LocalPorts) && PortListContains(LocalPorts, port);

    /// <summary>프로그램 제한 없이 모든 포트를 여는 규칙. 어떤 서비스 포트에도 적용되므로 따로 경고한다.</summary>
    public bool IsUnscopedAllPorts =>
        !ScopedToApplication && Protocol is "TCP" or "Any" && IsAllPorts(LocalPorts);

    public bool AppliesToTcpPort(int port) => ExplicitlyAllowsTcpPort(port) || IsUnscopedAllPorts;

    private static bool IsAllPorts(string ports) => ports is "*" or "Any" or "";

    public bool IsRemoteUnrestricted => RemoteAddresses is "*" or "Any" or "";

    internal static bool PortListContains(string ports, int port)
    {
        if (ports is "*" or "Any" or "") return true;
        foreach (var part in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0
                && int.TryParse(part[..dash], out var lo)
                && int.TryParse(part[(dash + 1)..], out var hi))
            {
                if (port >= lo && port <= hi) return true;
            }
            else if (int.TryParse(part, out var single) && single == port)
            {
                return true;
            }
        }
        return false;
    }
}
