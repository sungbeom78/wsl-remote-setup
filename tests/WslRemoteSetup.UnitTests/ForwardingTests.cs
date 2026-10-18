using System.Net;
using Microsoft.Extensions.Time.Testing;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Ipc;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Policy;

namespace WslRemoteSetup.UnitTests;

internal static class ForwardingFixtures
{
    public static readonly Guid Id = Guid.Parse("6f1c2a0e-1111-4c4c-9a9a-0123456789ab");
    public const string Owner = "S-1-5-21-1-2-3-1002";
    public const string Other = "S-1-5-21-1-2-3-1003";
    public static readonly Ipv4Cidr WslHost = Ipv4Cidr.FromAddress(IPAddress.Parse("172.24.208.1"), 20);

    public static ForwardingPolicy Policy() => new()
    {
        InstallationId = Id,
        OwnerSid = Owner,
        ListenAddress = "192.168.1.222",
        ListenPort = 1004,
        ConnectPort = 22,
    };

    public static RepairRequest Request(string ip = "172.24.220.111") => new(Id, IPAddress.Parse(ip));
}

public class ForwardingPolicyTests
{
    [Fact]
    public void Valid_policy_has_no_errors() => Assert.Empty(ForwardingFixtures.Policy().Validate());

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("192.168.1")]
    public void Wildcard_or_loopback_listen_address_is_rejected(string address) =>
        Assert.NotEmpty((ForwardingFixtures.Policy() with { ListenAddress = address }).Validate());

    [Fact]
    public void Owner_must_be_user_sid() =>
        Assert.NotEmpty((ForwardingFixtures.Policy() with { OwnerSid = "S-1-5-18" }).Validate());

    [Fact]
    public void Parse_rejects_invalid_policy() =>
        Assert.Throws<InvalidDataException>(() => ForwardingPolicy.Parse("""{"InstallationId":"00000000-0000-0000-0000-000000000000","OwnerSid":"x","ListenAddress":"0.0.0.0","ListenPort":0,"ConnectPort":22}"""));
}

public class RepairRequestValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    private static RepairDecision Eval(RepairRequest? req = null, string? caller = ForwardingFixtures.Owner, Ipv4Cidr? cidr = null, DateTimeOffset? last = null, bool noCidr = false) =>
        RepairRequestValidator.Evaluate(ForwardingFixtures.Policy(), req ?? ForwardingFixtures.Request(), caller,
            noCidr ? null : cidr ?? ForwardingFixtures.WslHost, Now, last);

    [Fact]
    public void Owner_with_valid_wsl_address_is_allowed() => Assert.True(Eval().Allowed);

    [Fact]
    public void Other_local_user_is_denied() // T10
    {
        Assert.Equal(RepairCode.Denied, Eval(caller: ForwardingFixtures.Other).Code);
        Assert.Equal(RepairCode.Denied, Eval(caller: null).Code);
        Assert.Equal(RepairCode.Denied, Eval(caller: "S-1-5-18").Code); // SYSTEM도 소유자가 아니면 거부
    }

    [Fact]
    public void Wrong_installation_is_denied() =>
        Assert.Equal(RepairCode.Denied, Eval(new RepairRequest(Guid.NewGuid(), IPAddress.Parse("172.24.220.111"))).Code);

    [Theory]
    [InlineData("8.8.8.8")]          // 공인 주소
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.222")]   // 수신 주소 자신
    [InlineData("192.168.1.10")]    // LAN의 다른 PC로 포워딩 시도
    [InlineData("172.24.224.5")]     // WSL 대역 밖
    public void Arbitrary_target_addresses_are_denied(string ip) =>
        Assert.Equal(RepairCode.Denied, Eval(ForwardingFixtures.Request(ip)).Code);

    [Fact]
    public void Missing_host_adapter_is_denied() => Assert.Equal(RepairCode.Denied, Eval(noCidr: true).Code);

    [Fact]
    public void Rate_limit_applies_after_identity_checks()
    {
        Assert.Equal(RepairCode.RateLimited, Eval(last: Now.AddSeconds(-5)).Code);
        Assert.Equal(RepairCode.Denied, Eval(caller: ForwardingFixtures.Other, last: Now.AddSeconds(-5)).Code);
        Assert.True(Eval(last: Now.AddMinutes(-5)).Allowed);
    }
}

public class PortProxyReconcilerTests
{
    private static readonly IPAddress Target = IPAddress.Parse("172.24.220.111");

    private static ReconcileAction Decide(IReadOnlyList<PortProxyRule> current, string? recorded = null) =>
        PortProxyReconciler.Decide(ForwardingFixtures.Policy(), current, recorded, Target);

    [Fact]
    public void Adds_when_absent() => Assert.Equal(ReconcileKind.Add, Decide([]).Kind);

    [Fact]
    public void No_change_when_same() // T17
    {
        var a = Decide([new("192.168.1.222", 1004, "172.24.220.111", 22)], "172.24.220.111");
        Assert.Equal(ReconcileKind.NoChange, a.Kind);
    }

    [Fact]
    public void Updates_owned_rule_when_wsl_ip_changes() // T11
    {
        var a = Decide([new("192.168.1.222", 1004, "172.20.1.5", 22)], "172.20.1.5");
        Assert.Equal(ReconcileKind.Update, a.Kind);
        Assert.Equal("192.168.1.222", a.Desired!.ListenAddress);
        Assert.Equal(1004, a.Desired.ListenPort);
    }

    [Fact]
    public void Externally_changed_rule_is_conflict()
    {
        Assert.Equal(ReconcileKind.Conflict, Decide([new("192.168.1.222", 1004, "172.20.1.9", 22)], "172.20.1.5").Kind);
        Assert.Equal(ReconcileKind.Conflict, Decide([new("192.168.1.222", 1004, "172.20.1.9", 22)]).Kind); // 최초 적용 전 타인 규칙
    }

    [Fact]
    public void Wildcard_listener_on_same_port_is_conflict() =>
        Assert.Equal(ReconcileKind.Conflict, Decide([new("0.0.0.0", 1004, "172.20.1.9", 22)]).Kind);

    [Fact]
    public void Other_tuples_are_ignored()
    {
        var a = Decide([new("192.168.1.222", 2222, "10.0.0.5", 22), new("127.0.0.1", 1004, "10.0.0.6", 22)]);
        Assert.Equal(ReconcileKind.Add, a.Kind);
    }
}

public class RepairCoordinatorTests
{
    private sealed class FakeHost : IForwardingHost
    {
        public List<PortProxyRule> Rules { get; } = [];
        public bool EndpointOk { get; set; } = true;
        public Ipv4Cidr? Cidr { get; set; } = ForwardingFixtures.WslHost;
        public int Applied { get; private set; }

        public Ipv4Cidr? GetHostWslCidr() => Cidr;
        public IReadOnlyList<PortProxyRule> GetPortProxies() => [.. Rules];
        public bool CheckEndpoint(IPAddress address, int port) => EndpointOk;
        public void Apply(ReconcileAction action)
        {
            Applied++;
            Rules.RemoveAll(r => r.ListenAddress == action.Desired!.ListenAddress && r.ListenPort == action.Desired.ListenPort);
            Rules.Add(action.Desired!);
        }
    }

    private static (RepairCoordinator, FakeHost, FakeTimeProvider) Create()
    {
        var host = new FakeHost();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        return (new RepairCoordinator(ForwardingFixtures.Policy(), host, time), host, time);
    }

    [Fact]
    public void Ip_change_flow_updates_only_owned_tuple_and_is_idempotent()
    {
        var (c, host, time) = Create();
        host.Rules.Add(new("192.168.1.222", 3333, "10.9.9.9", 80)); // 남의 규칙

        Assert.Equal(RepairCode.Updated, c.Handle(ForwardingFixtures.Owner, ForwardingFixtures.Request("172.24.220.111"), "").Code);
        Assert.Equal(RepairCode.NoChange, c.Handle(ForwardingFixtures.Owner, ForwardingFixtures.Request("172.24.220.111"), "").Code);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(RepairCode.Updated, c.Handle(ForwardingFixtures.Owner, ForwardingFixtures.Request("172.24.220.200"), "").Code);
        Assert.Equal(2, host.Applied);
        Assert.Contains(host.Rules, r => r is { ListenPort: 3333, ConnectAddress: "10.9.9.9" });
        Assert.Contains(host.Rules, r => r is { ListenPort: 1004, ConnectAddress: "172.24.220.200" });
    }

    [Fact]
    public void Rate_limited_change_is_not_applied()
    {
        var (c, host, _) = Create();
        c.Handle(ForwardingFixtures.Owner, ForwardingFixtures.Request("172.24.220.111"), "");
        Assert.Equal(RepairCode.RateLimited, c.Handle(ForwardingFixtures.Owner, ForwardingFixtures.Request("172.24.220.200"), "").Code);
        Assert.Equal(1, host.Applied);
    }

    [Fact]
    public void Unreachable_endpoint_is_not_applied()
    {
        var (c, host, _) = Create();
        host.EndpointOk = false;
        Assert.Equal(RepairCode.EndpointUnreachable, c.Handle(ForwardingFixtures.Owner, ForwardingFixtures.Request(), "").Code);
        Assert.Equal(0, host.Applied);
    }

    [Fact]
    public void Denied_and_malformed_requests_change_nothing()
    {
        var (c, host, _) = Create();
        Assert.Equal(RepairCode.Denied, c.Handle(ForwardingFixtures.Other, ForwardingFixtures.Request(), "").Code);
        Assert.Equal(RepairCode.Denied, c.Handle(ForwardingFixtures.Owner, null, "JSON 오류").Code);
        Assert.Equal(0, host.Applied);
    }

    [Fact]
    public void External_conflict_stops()
    {
        var (c, host, _) = Create();
        host.Rules.Add(new("192.168.1.222", 1004, "172.24.210.9", 22));
        Assert.Equal(RepairCode.Conflict, c.Handle(ForwardingFixtures.Owner, ForwardingFixtures.Request(), "").Code);
        Assert.Equal(0, host.Applied);
    }
}
