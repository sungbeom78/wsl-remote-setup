using System.Net;
using System.Security.Cryptography;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Ssh;
using WslRemoteSetup.Core.State;

namespace WslRemoteSetup.UnitTests;

public class HostKeyFingerprintTests
{
    private static string KeyLine(string type, byte[] body, out string fingerprint)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        byte[] blob = [0, 0, 0, (byte)name.Length, .. name, .. body];
        fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        return $"172.24.220.111 {type} {Convert.ToBase64String(blob)}";
    }

    [Fact]
    public void Computes_openssh_style_fingerprint()
    {
        var line = KeyLine("ssh-ed25519", new byte[36], out var fp);
        var keys = HostKeyFingerprint.ParseKeyscan("# 172.24.220.111:22 SSH-2.0-OpenSSH_9.6p1\n" + line + "\n");
        var k = Assert.Single(keys);
        Assert.Equal("ssh-ed25519", k.KeyType);
        Assert.Equal(fp, k.Fingerprint);
        Assert.True(HostKeyFingerprint.IsWellFormed(fp));
    }

    [Fact]
    public void Blob_type_mismatch_and_garbage_are_ignored()
    {
        var line = KeyLine("ssh-rsa", new byte[10], out _).Replace(" ssh-rsa ", " ssh-ed25519 ", StringComparison.Ordinal);
        Assert.Empty(HostKeyFingerprint.ParseKeyscan(line + "\nhost ssh-ed25519 !!!notbase64\nshort"));
    }

    [Fact]
    public void Parses_ssh_verbose_server_host_key()
    {
        const string output = """
            debug1: Connection established.
            debug1: Server host key: ssh-ed25519 SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR8
            debug1: Authentications that can continue: publickey,password
            wrs-hostkey-probe@172.24.220.111: Permission denied (publickey,password).
            """;
        var k = Assert.Single(HostKeyFingerprint.ParseSshVerbose(output));
        Assert.Equal("ssh-ed25519", k.KeyType);
        Assert.Equal("SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR8", k.Fingerprint);
        Assert.Empty(HostKeyFingerprint.ParseSshVerbose("debug1: Server host key: ssh-ed25519 SHA256:short"));
    }

    [Theory]
    [InlineData("SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR8", true)]
    [InlineData("SHA256:qjDdY", false)]
    [InlineData("MD5:aa:bb", false)]
    [InlineData("SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR;", false)]
    public void IsWellFormed(string value, bool ok) => Assert.Equal(ok, HostKeyFingerprint.IsWellFormed(value));
}

public class ForwardingPlanBuilderTests
{
    private const string Key = "SHA256:qjDdYL7ZZ9YgGsiu6CQ9etjth6qZm4SDvHVUPU3MsR8";
    private static readonly Guid Id = Guid.Parse("5f3c9a10-7b2e-4d6a-9c1e-2a8b4d6f0e13");

    private static ForwardingFacts G2() => new()
    {
        InstallationId = Id,
        OwnerSid = Fixtures.OwnerSid,
        ListenAddress = "192.168.1.222",
        TargetAddress = IPAddress.Parse("172.24.220.111"),
        ExpectedHostKey = Key,
        Interfaces = Fixtures.G2AfterBootstrap().Interfaces!,
        PortProxies = [],
        TcpListenPorts = [7389, 22],
        FirewallRules = Fixtures.G2AfterBootstrap().FirewallRules!,
        WslHostCidr = Ipv4Cidr.FromAddress(IPAddress.Parse("172.24.208.1"), 20),
        EndpointBanner = "SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.20",
        ObservedHostKeys = [new HostKey("ssh-ed25519", Key)],
    };

    [Fact]
    public void G2_first_apply_adds_proxy_and_lan_only_rule()
    {
        var plan = ForwardingPlanBuilder.Build(G2());
        Assert.True(plan.CanApply, string.Join(" / ", plan.Blockers));
        Assert.Equal(ReconcileKind.Add, plan.PortProxy!.Kind);
        Assert.Equal(FirewallActionKind.Create, plan.Firewall);
        Assert.Equal("192.168.1.0/24", plan.Rule!.RemoteCidr);
        Assert.Equal("192.168.1.222", plan.Rule.LocalAddress);
        Assert.Equal(FirewallProfiles.Private, plan.Rule.Profile);
        Assert.Equal(1004, plan.Rule.Port);
        Assert.Contains("5f3c9a10", plan.Rule.Name);
    }

    [Fact]
    public void Rerun_after_apply_is_no_change() // T17
    {
        var f = G2() with
        {
            PortProxies = [new PortProxyRule("192.168.1.222", 1004, "172.24.220.111", 22)],
            TcpListenPorts = [7389, 1004],
            FirewallRules = [.. G2().FirewallRules, new FirewallRuleInfo(ForwardingPlanBuilder.RuleName(Id, 1004), ForwardingPlanBuilder.RuleName(Id, 1004), true, true, true, FirewallProfiles.Private, "TCP", "1004", "192.168.1.0/255.255.255.0")],
            Owned = [new OwnedResource(ResourceKinds.PortProxy, "192.168.1.222:1004", "", DateTimeOffset.UnixEpoch) { Value = "172.24.220.111:22" }],
        };
        var plan = ForwardingPlanBuilder.Build(f);
        Assert.True(plan.CanApply, string.Join(" / ", plan.Blockers));
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void Wsl_ip_change_updates_owned_proxy() // T11
    {
        var f = G2() with
        {
            TargetAddress = IPAddress.Parse("172.24.210.5"),
            PortProxies = [new PortProxyRule("192.168.1.222", 1004, "172.24.220.111", 22)],
            TcpListenPorts = [1004],
            Owned = [new OwnedResource(ResourceKinds.PortProxy, "192.168.1.222:1004", "", DateTimeOffset.UnixEpoch) { Value = "172.24.220.111:22" }],
        };
        Assert.Equal(ReconcileKind.Update, ForwardingPlanBuilder.Build(f).PortProxy!.Kind);
    }

    [Fact]
    public void Wrong_host_key_blocks()
    {
        var f = G2() with { ObservedHostKeys = [new HostKey("ssh-ed25519", "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] };
        Assert.Contains(ForwardingPlanBuilder.Build(f).Blockers, b => b.Contains("host key"));
    }

    [Fact]
    public void Target_outside_wsl_nat_blocks() =>
        Assert.False(ForwardingPlanBuilder.Build(G2() with { TargetAddress = IPAddress.Parse("192.168.1.10") }).CanApply);

    [Fact]
    public void Missing_banner_blocks() =>
        Assert.False(ForwardingPlanBuilder.Build(G2() with { EndpointBanner = null }).CanApply);

    [Fact]
    public void Foreign_listener_on_1004_blocks() // T04
    {
        var plan = ForwardingPlanBuilder.Build(G2() with { TcpListenPorts = [1004] });
        Assert.Contains(plan.Blockers, b => b.Contains("PORT_CONFLICT"));
    }

    [Fact]
    public void Foreign_proxy_on_tuple_blocks() =>
        Assert.Contains(ForwardingPlanBuilder.Build(G2() with { PortProxies = [new PortProxyRule("192.168.1.222", 1004, "10.0.0.9", 22)], TcpListenPorts = [1004] }).Blockers,
            b => b.Contains("PORTPROXY_CONFLICT"));

    [Fact]
    public void Listen_address_not_on_adapter_is_not_expanded() // T12
    {
        var plan = ForwardingPlanBuilder.Build(G2() with { ListenAddress = "192.168.0.50" });
        Assert.Contains(plan.Blockers, b => b.Contains("자동 확대하지 않습니다"));
        Assert.Null(plan.Rule);
    }

    [Fact]
    public void Public_profile_blocks()
    {
        var f = G2();
        f = f with { Interfaces = [f.Interfaces[0] with { Category = NetworkCategory.Public }] };
        Assert.Contains(ForwardingPlanBuilder.Build(f).Blockers, b => b.Contains("Public"));
    }

    [Fact]
    public void Wildcard_listen_is_rejected() =>
        Assert.False(ForwardingPlanBuilder.Build(G2() with { ListenAddress = "0.0.0.0" }).CanApply);

    [Fact]
    public void Same_name_rule_with_other_settings_is_conflict()
    {
        var name = ForwardingPlanBuilder.RuleName(Id, 1004);
        var f = G2() with { FirewallRules = [new FirewallRuleInfo(name, name, true, true, true, FirewallProfiles.All, "TCP", "1004", "*")] };
        var plan = ForwardingPlanBuilder.Build(f);
        Assert.Equal(FirewallActionKind.Conflict, plan.Firewall);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void Other_rule_on_port_warns() // T26
    {
        var f = G2() with { FirewallRules = [new FirewallRuleInfo("x", "Open 1004", true, true, true, FirewallProfiles.All, "TCP", "1000-1010", "*")] };
        Assert.Contains(ForwardingPlanBuilder.Build(f).Warnings, w => w.Contains("Open 1004"));
    }

    [Theory]
    [InlineData("192.168.1.0/255.255.255.0", true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("192.168.0.0/255.255.0.0", false)]
    [InlineData("*", false)]
    public void Remote_address_formats(string actual, bool same) =>
        Assert.Equal(same, ForwardingPlanBuilder.SameRemote(actual, "192.168.1.0/24"));
}
