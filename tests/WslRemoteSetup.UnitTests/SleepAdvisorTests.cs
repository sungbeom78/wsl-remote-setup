using WslRemoteSetup.Core.Power;
using WslRemoteSetup.Core.Status;

namespace WslRemoteSetup.UnitTests;

public class SleepAdvisorTests
{
    [Fact]
    public void Never_sleeps_on_ac_is_good()
    {
        Assert.Equal(Health.Good, SleepAdvisor.Line(new SleepInfo(0, 0, false)).Health);
        Assert.Equal(Health.Good, SleepAdvisor.Line(new SleepInfo(0, null, false)).Health);
    }

    [Fact]
    public void Standby_after_30_minutes_warns_in_plain_words()
    {
        var l = SleepAdvisor.Line(new SleepInfo(1800, 0, false));
        Assert.Equal(Health.Warn, l.Health);
        Assert.Contains("30분", l.Text, StringComparison.Ordinal);
        Assert.Contains("power keep-awake", l.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Earlier_hibernate_wins()
    {
        var l = SleepAdvisor.Line(new SleepInfo(0, 10800, false));
        Assert.Equal(Health.Warn, l.Health);
        Assert.Contains("3시간", l.Text, StringComparison.Ordinal);
        Assert.Contains("1시간", SleepAdvisor.Line(new SleepInfo(7200, 3600, true)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_when_not_read() => Assert.Equal(Health.Unknown, SleepAdvisor.Line(new SleepInfo(null, null, false)).Health);

    [Theory]
    [InlineData(30u, "30초")]
    [InlineData(600u, "10분")]
    [InlineData(5400u, "1시간 30분")]
    public void Duration_is_readable(uint s, string expected) => Assert.Equal(expected, SleepAdvisor.Duration(s));

    [Fact]
    public void Status_includes_sleep_warning_and_next_step()
    {
        var f = new StatusFacts
        {
            Installed = true, SshAnswers = true, WslRunning = true, AgentRunning = true, Service = ServiceState.Running,
            TaskRegistered = true, ForwardRuleExists = true, Listening = true, FirewallRuleExists = true,
            Sleep = new SleepInfo(1800, 0, false), Now = DateTimeOffset.Now, AgentLastCheck = DateTimeOffset.Now,
        };
        var r = StatusEvaluator.Evaluate(f);
        Assert.Contains(r.Lines, l => l.Title == "절전" && l.Health == Health.Warn);
        Assert.Contains(r.NextSteps, s => s.Contains("power keep-awake", StringComparison.Ordinal));
        Assert.Equal(Health.Warn, r.Overall);
        Assert.Equal(Health.Good, StatusEvaluator.Evaluate(f with { Sleep = new SleepInfo(0, 0, false) }).Overall);
    }
}
