using WslRemoteSetup.Core.Status;

namespace WslRemoteSetup.UnitTests;

public class PreLoginStatusTests
{
    private static StatusFacts Healthy(bool? preLogin) => new()
    {
        Installed = true, SshAnswers = true, WslRunning = true, AgentRunning = true, Service = ServiceState.Running,
        TaskRegistered = true, ForwardRuleExists = true, Listening = true, FirewallRuleExists = true,
        Now = DateTimeOffset.Now, AgentLastCheck = DateTimeOffset.Now, PreLogin = preLogin,
    };

    [Fact]
    public void On_is_shown_as_good()
    {
        var r = StatusEvaluator.Evaluate(Healthy(true));
        Assert.Contains(r.Lines, l => l.Title == "로그인 전 접속" && l.Health == Health.Good);
        Assert.Equal(Health.Good, r.Overall);
    }

    [Fact]
    public void Off_is_informational_and_does_not_degrade_overall()
    {
        var r = StatusEvaluator.Evaluate(Healthy(false));
        var line = Assert.Single(r.Lines, l => l.Title == "로그인 전 접속");
        Assert.Equal(Health.Off, line.Health);
        Assert.Contains("prelogin-on", line.Hint, StringComparison.Ordinal);
        Assert.Equal(Health.Good, r.Overall);
    }

    [Fact]
    public void Unknown_hides_line() =>
        Assert.DoesNotContain(StatusEvaluator.Evaluate(Healthy(null)).Lines, l => l.Title == "로그인 전 접속");
}

public class ServicePortStatusTests
{
    private static StatusFacts Facts(bool? active) => new()
    {
        Installed = true, SshAnswers = true, WslRunning = true, AgentRunning = true, Service = ServiceState.Running,
        TaskRegistered = true, ForwardRuleExists = true, Listening = true, FirewallRuleExists = true,
        Now = DateTimeOffset.Now, AgentLastCheck = DateTimeOffset.Now,
        Services = [("web", 8080, 80, true)],
        ServiceActive = active is { } a ? new Dictionary<string, bool> { ["web"] = a } : new Dictionary<string, bool>(),
    };

    [Fact]
    public void Inactive_wsl_service_keeps_path_good_and_says_it_connects_when_started()
    {
        var r = StatusEvaluator.Evaluate(Facts(false));
        var l = Assert.Single(r.Lines, x => x.Title == "추가 서비스 web");
        Assert.Equal(Health.Good, l.Health);
        Assert.Contains("WSL 서비스 꺼짐(켜면 바로 연결)", l.Text, StringComparison.Ordinal);
        Assert.Equal(Health.Good, r.Overall);
    }

    [Fact]
    public void Active_and_unknown_texts()
    {
        Assert.Contains("WSL 서비스 응답 있음", StatusEvaluator.Evaluate(Facts(true)).Lines.Single(x => x.Title == "추가 서비스 web").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("WSL 서비스", StatusEvaluator.Evaluate(Facts(null)).Lines.Single(x => x.Title == "추가 서비스 web").Text, StringComparison.Ordinal);
    }
}
