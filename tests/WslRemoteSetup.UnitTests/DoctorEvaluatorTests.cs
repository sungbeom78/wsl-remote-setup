using WslRemoteSetup.Core;
using WslRemoteSetup.Core.Diagnostics;
using WslRemoteSetup.Core.Model;

namespace WslRemoteSetup.UnitTests;

public class DoctorEvaluatorTests
{
    private static CheckResult Find(IReadOnlyList<CheckResult> r, string id) => Assert.Single(r, x => x.Id == id);

    [Fact]
    public void G2_after_bootstrap_reports_expected_statuses()
    {
        var r = DoctorEvaluator.Evaluate(Fixtures.G2AfterBootstrap(), new DesiredConfiguration());
        Assert.Equal(CheckStatus.Pass, Find(r, "OS_EDITION").Status);
        Assert.Equal(CheckStatus.Pass, Find(r, "VIRTUALIZATION").Status);
        Assert.Equal(CheckStatus.Pass, Find(r, "WSL_RUNTIME").Status);
        Assert.Equal(CheckStatus.Pass, Find(r, "WSL_DISTRO").Status);
        Assert.Equal(CheckStatus.Pass, Find(r, "WSL_LINUX_USER").Status);
        Assert.Equal(CheckStatus.Pass, Find(r, "OWNER_ACCOUNT").Status);
        Assert.Equal(CheckStatus.Pass, Find(r, "NET_STATIC_IP").Status);
        Assert.Equal(CheckStatus.Pass, Find(r, "NET_PROFILE").Status);
        Assert.Contains("192.168.1.0/24", Find(r, "NET_ADAPTER").Detail);
        Assert.Contains("7389", Find(r, "RDP").Detail);
        Assert.Equal(CheckStatus.Pass, Find(r, "RDP_FIREWALL").Status);
        Assert.Equal(CheckStatus.Warn, Find(r, "RDP_FIREWALL_DEFAULT").Status); // T26
        Assert.Equal(CheckStatus.Pass, Find(r, "LISTEN_PORT").Status);
        Assert.Equal(CheckStatus.NotTested, Find(r, "SSH_LAN_AUTH").Status);   // T20
        Assert.Equal(CheckStatus.NotTested, Find(r, "RDP_LAN_AUTH").Status);
        Assert.DoesNotContain(r, x => x.Id == "OS_ARCH");
    }

    [Fact]
    public void Arm64_server_is_warned_not_blocked()
    {
        var s = Fixtures.G2AfterBootstrap();
        s = s with { Os = s.Os! with { IsArm64 = true } };
        Assert.Equal(CheckStatus.Warn, Find(DoctorEvaluator.Evaluate(s, new DesiredConfiguration()), "OS_ARCH").Status);
    }

    [Fact]
    public void Fresh_windows_marks_wsl_steps_pending()
    {
        var r = DoctorEvaluator.Evaluate(Fixtures.FreshWindows(), new DesiredConfiguration());
        Assert.Equal(CheckStatus.Pending, Find(r, "WSL_RUNTIME").Status);
        Assert.Equal(CheckStatus.Pending, Find(r, "VIRTUALIZATION").Status);
        Assert.Equal(CheckStatus.Pending, Find(r, "WSL_DISTRO").Status);
        Assert.DoesNotContain(r, x => x.Id == "WSL_LINUX_USER");
    }

    [Fact]
    public void Home_edition_is_ssh_only_without_rdp_workaround()
    {
        var s = Fixtures.G2AfterBootstrap();
        s = s with { Os = s.Os! with { EditionId = "Core" } };
        var r = DoctorEvaluator.Evaluate(s, new DesiredConfiguration());
        Assert.Equal(CheckStatus.Warn, Find(r, "OS_EDITION").Status);
        Assert.Equal(CheckStatus.NotApplicable, Find(r, "RDP").Status); // T25
    }

    [Fact]
    public void Listener_on_1004_is_port_conflict()
    {
        var s = Fixtures.G2AfterBootstrap() with { TcpListenPorts = [1004, 7389] };
        var c = Find(DoctorEvaluator.Evaluate(s, new DesiredConfiguration()), "LISTEN_PORT");
        Assert.Equal(CheckStatus.Fail, c.Status);
        Assert.Contains("PORT_CONFLICT", c.Detail);
    }

    [Fact]
    public void Existing_portproxy_on_listen_port_is_warned_not_overwritten()
    {
        var s = Fixtures.G2AfterBootstrap() with { PortProxies = [new PortProxyRule("192.168.1.222", 1004, "172.20.0.2", 22)] };
        Assert.Equal(CheckStatus.Warn, Find(DoctorEvaluator.Evaluate(s, new DesiredConfiguration()), "LISTEN_PORT").Status);
    }

    [Fact]
    public void Broad_rule_on_actual_rdp_port_is_warned()
    {
        var s = Fixtures.G2AfterBootstrap();
        s = s with
        {
            FirewallRules = [.. s.FirewallRules!, new FirewallRuleInfo("open", "open 7389", true, true, true, FirewallProfiles.All, "TCP", "7000-8000", "*")],
        };
        Assert.Equal(CheckStatus.Warn, Find(DoctorEvaluator.Evaluate(s, new DesiredConfiguration()), "RDP_FIREWALL").Status);
    }

    [Fact]
    public void Application_scoped_wildcard_rules_do_not_count_as_rdp_rules()
    {
        // G2 실기 doctor에서 발견: UWP/프로그램 규칙(포트 *)이 RDP 허용 규칙으로 잘못 집계됨.
        var s = Fixtures.G2AfterBootstrap();
        s = s with
        {
            FirewallRules =
            [
                .. s.FirewallRules!,
                new FirewallRuleInfo("store", "Microsoft Store", true, true, true, FirewallProfiles.All, "Any", "*", "*") { ScopedToApplication = true },
                new FirewallRuleInfo("shadow", "Remote Desktop - Shadow (TCP-In)", true, true, true, FirewallProfiles.All, "TCP", "*", "*") { ScopedToApplication = true },
            ],
        };
        var r = DoctorEvaluator.Evaluate(s, new DesiredConfiguration());
        Assert.Equal(CheckStatus.Pass, Find(r, "RDP_FIREWALL").Status);
        Assert.DoesNotContain(r, x => x.Id == "FIREWALL_ALL_PORTS");
    }

    [Fact]
    public void Unscoped_all_ports_rule_is_warned_separately()
    {
        var s = Fixtures.G2AfterBootstrap();
        s = s with { FirewallRules = [.. s.FirewallRules!, new FirewallRuleInfo("any", "Allow everything", true, true, true, FirewallProfiles.Private, "TCP", "*", "*")] };
        var r = DoctorEvaluator.Evaluate(s, new DesiredConfiguration());
        Assert.Equal(CheckStatus.Warn, Find(r, "FIREWALL_ALL_PORTS").Status);
        Assert.Contains("Allow everything", Find(r, "FIREWALL_ALL_PORTS").Detail);
    }

    [Fact]
    public void Dhcp_and_public_profile_are_warned()
    {
        var s = Fixtures.G2AfterBootstrap();
        var wifi = s.Interfaces![0] with { DhcpEnabled = true, Category = NetworkCategory.Public };
        s = s with { Interfaces = [wifi] };
        var r = DoctorEvaluator.Evaluate(s, new DesiredConfiguration());
        Assert.Equal(CheckStatus.Warn, Find(r, "NET_STATIC_IP").Status);
        Assert.Equal(CheckStatus.Warn, Find(r, "NET_PROFILE").Status);
    }

    [Fact]
    public void Missing_owner_account_is_pending_and_distro_scope_is_current_user()
    {
        var r = DoctorEvaluator.Evaluate(Fixtures.G2AfterBootstrap(), new DesiredConfiguration { OwnerAccountName = "remoteControl" });
        Assert.Equal(CheckStatus.Pending, Find(r, "OWNER_ACCOUNT").Status);
        Assert.Contains("현재 실행 계정 기준", Find(r, "WSL_DISTRO").Detail);
    }

    [Fact]
    public void Probe_errors_are_not_tested_rather_than_absent()
    {
        var s = Fixtures.G2AfterBootstrap() with { Accounts = null, Errors = [new("accounts", "access denied")] };
        var r = DoctorEvaluator.Evaluate(s, new DesiredConfiguration());
        Assert.Equal(CheckStatus.NotTested, Find(r, "OWNER_ACCOUNT").Status);
        Assert.Equal(CheckStatus.NotTested, Find(r, "PROBE_ACCOUNTS").Status);
    }
}
