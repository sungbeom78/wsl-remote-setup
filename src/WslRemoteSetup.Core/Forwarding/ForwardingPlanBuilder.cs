using System.Globalization;
using System.Net;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Core.Ssh;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Forwarding;

/// <summary>Windows → WSL SSH 포워딩 계획 입력. 관리자 적용 시에는 관리부가 직접 관찰한 값으로 채운다.</summary>
public sealed record ForwardingFacts
{
    public required Guid InstallationId { get; init; }
    public required string OwnerSid { get; init; }
    public required string ListenAddress { get; init; }
    public int ListenPort { get; init; } = DesiredConfiguration.DefaultListenPort;
    public int ConnectPort { get; init; } = DesiredConfiguration.DefaultWslSshPort;
    public required IPAddress TargetAddress { get; init; }
    public required string ExpectedHostKey { get; init; }

    public required IReadOnlyList<NetworkInterfaceInfo> Interfaces { get; init; }
    public required IReadOnlyList<PortProxyRule> PortProxies { get; init; }
    public required IReadOnlyList<int> TcpListenPorts { get; init; }
    public required IReadOnlyList<FirewallRuleInfo> FirewallRules { get; init; }
    public Ipv4Cidr? WslHostCidr { get; init; }
    public string? EndpointBanner { get; init; }
    public IReadOnlyList<HostKey> ObservedHostKeys { get; init; } = [];
    public IReadOnlyList<OwnedResource> Owned { get; init; } = [];
}

public enum FirewallActionKind { Create, NoChange, Conflict }

public sealed record FirewallRuleSpec(string Name, string Description, string LocalAddress, int Port, string RemoteCidr, FirewallProfiles Profile);

public sealed record ForwardingPlan(
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    ReconcileAction? PortProxy,
    FirewallActionKind Firewall,
    FirewallRuleSpec? Rule,
    string Summary)
{
    public bool CanApply => Blockers.Count == 0;
    public bool HasChanges => PortProxy?.Kind is ReconcileKind.Add or ReconcileKind.Update || Firewall == FirewallActionKind.Create;
}

/// <summary>
/// 포워딩·방화벽 계획을 만든다(FR-05, FR-06). 승인된 수신 주소·포트만 사용하고,
/// 대상이 WSL NAT 대역 안의 실제 SSH endpoint이며 host key가 등록값과 같을 때만 적용 가능으로 판단한다.
/// </summary>
public static class ForwardingPlanBuilder
{
    public static string RuleName(Guid installationId, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"WslRemoteSetup SSH {port} ({installationId.ToString("N")[..8]})");

    public static ForwardingPlan Build(ForwardingFacts f)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        var policy = new ForwardingPolicy
        {
            InstallationId = f.InstallationId,
            OwnerSid = f.OwnerSid,
            ListenAddress = f.ListenAddress,
            ListenPort = f.ListenPort,
            ConnectPort = f.ConnectPort,
        };
        blockers.AddRange(policy.Validate());
        if (!HostKeyFingerprint.IsWellFormed(f.ExpectedHostKey)) blockers.Add("기대 host key 지문 형식이 올바르지 않습니다.");

        // 수신 어댑터: 기존 주소를 그대로 쓰며 변경하지 않는다.
        var adapter = f.Interfaces.FirstOrDefault(i => i.IsUp && i.Ipv4.Any(a => a.Address == f.ListenAddress));
        string? lanCidr = null;
        if (adapter is null)
        {
            blockers.Add($"{f.ListenAddress}가 활성 어댑터에 없습니다. 다른 어댑터로 자동 확대하지 않습니다.");
        }
        else
        {
            var a = adapter.Ipv4.First(x => x.Address == f.ListenAddress);
            if (InputValidator.TryParseIPv4(a.Address, out var ip)) lanCidr = Ipv4Cidr.FromAddress(ip, a.PrefixLength).ToString();
            if (adapter.Category != NetworkCategory.Private)
                blockers.Add($"수신 어댑터 프로필이 {adapter.Category}입니다. Private로 신뢰 확인 전에는 규칙을 만들지 않습니다.");
            if (adapter.DhcpEnabled != false)
                warnings.Add("수신 주소가 고정이 아닐 수 있습니다(Windows 주소 고정: 미완료).");
        }

        // 대상 검증
        if (f.WslHostCidr is not { } wslCidr)
            blockers.Add("WSL 호스트 가상 어댑터를 찾지 못했습니다.");
        else if (!wslCidr.Contains(f.TargetAddress))
            blockers.Add($"대상 {f.TargetAddress}가 WSL NAT 대역 {wslCidr} 밖입니다.");
        if (f.EndpointBanner?.StartsWith("SSH-2.0-", StringComparison.Ordinal) != true)
            blockers.Add($"대상 {f.TargetAddress}:{f.ConnectPort}에서 SSH 배너를 확인하지 못했습니다(배포판이 유휴 종료되었을 수 있음 — 소유자 세션에서 유지 중인지 확인).");
        if (!f.ObservedHostKeys.Any(k => k.Fingerprint == f.ExpectedHostKey))
            blockers.Add("대상 SSH host key가 등록된 지문과 다릅니다.");

        // portproxy
        var owned = f.Owned.FirstOrDefault(o => o.Kind == ResourceKinds.PortProxy
            && o.Key == string.Create(CultureInfo.InvariantCulture, $"{f.ListenAddress}:{f.ListenPort}"));
        var recordedConnect = owned?.Value?.Split(':')[0];
        ReconcileAction? proxy = null;
        if (policy.Validate().Count == 0)
        {
            proxy = PortProxyReconciler.Decide(policy, f.PortProxies, recordedConnect, f.TargetAddress);
            if (proxy.Kind == ReconcileKind.Conflict) blockers.Add($"[PORTPROXY_CONFLICT] {proxy.Reason}");
        }
        var proxiesOnPort = f.PortProxies.Any(p => p.ListenPort == f.ListenPort);
        if (f.TcpListenPorts.Contains(f.ListenPort) && !proxiesOnPort)
            blockers.Add($"[PORT_CONFLICT] TCP {f.ListenPort}를 다른 프로세스가 사용 중입니다. 기존 서비스는 변경하지 않습니다.");

        // 방화벽
        var name = RuleName(f.InstallationId, f.ListenPort);
        FirewallRuleSpec? rule = lanCidr is null ? null : new FirewallRuleSpec(
            name,
            $"WslRemoteSetup installationId {f.InstallationId:D}: LAN SSH → WSL",
            f.ListenAddress, f.ListenPort, lanCidr, FirewallProfiles.Private);
        var existing = f.FirewallRules.Where(r => r.Name == name || r.DisplayName == name).ToList();
        var fw = FirewallActionKind.Create;
        if (existing.Count > 1)
        {
            fw = FirewallActionKind.Conflict;
            blockers.Add($"같은 이름의 방화벽 규칙이 여러 개입니다: {name}");
        }
        else if (existing.Count == 1 && rule is not null)
        {
            var e = existing[0];
            var same = e is { Enabled: true, Inbound: true, Allow: true, Protocol: "TCP" }
                && e.LocalPorts == rule.Port.ToString(CultureInfo.InvariantCulture)
                && e.Profiles == rule.Profile
                && SameRemote(e.RemoteAddresses, rule.RemoteCidr);
            fw = same ? FirewallActionKind.NoChange : FirewallActionKind.Conflict;
            if (!same) blockers.Add($"같은 이름의 방화벽 규칙이 다른 설정으로 존재합니다: {name}. 덮어쓰지 않습니다.");
        }

        var broad = f.FirewallRules.Where(r => r is { Enabled: true, Inbound: true, Allow: true }
            && r.Name != name && r.ExplicitlyAllowsTcpPort(f.ListenPort)).ToList();
        if (broad.Count > 0)
            warnings.Add($"TCP {f.ListenPort}를 허용하는 다른 규칙이 있습니다: {string.Join(", ", broad.Select(b => b.DisplayName).Distinct())}. 좁은 규칙 추가로 취소되지 않습니다.");

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{f.ListenAddress}:{f.ListenPort} → {f.TargetAddress}:{f.ConnectPort}, 방화벽 Private·원격 {lanCidr ?? "?"}");
        return new ForwardingPlan(blockers, warnings, proxy, fw, rule, summary);
    }

    /// <summary>방화벽 COM은 CIDR을 "a.b.c.d/m.m.m.m" 형식으로 돌려줄 수 있어 두 형식을 같은 값으로 본다.</summary>
    internal static bool SameRemote(string actual, string cidr)
    {
        if (string.Equals(actual, cidr, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Ipv4Cidr.TryParse(cidr, out var expected)) return false;
        var parts = actual.Split('/');
        if (parts.Length != 2 || !InputValidator.TryParseIPv4(parts[0], out var net)) return false;
        if (InputValidator.TryParseIPv4(parts[1], out var mask))
        {
            var bits = System.Numerics.BitOperations.PopCount(Ipv4Cidr.ToUInt32(mask));
            return Ipv4Cidr.FromAddress(net, bits) == expected;
        }
        return Ipv4Cidr.TryParse(actual, out var parsed) && parsed == expected;
    }
}
