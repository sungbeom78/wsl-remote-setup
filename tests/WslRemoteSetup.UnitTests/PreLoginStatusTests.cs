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
