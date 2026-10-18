using WslRemoteSetup.Core.Status;

namespace WslRemoteSetup.UnitTests;

public class StatusEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 17, 0, 0, TimeSpan.FromHours(9));

    private static StatusFacts Healthy() => new()
    {
        Installed = true,
        OwnerName = "ownerAdmin",
        CurrentUser = "ownerAdmin",
        Distro = "Ubuntu-24.04",
        ListenAddress = "192.168.1.222",
        ListenPort = 1004,
        Service = ServiceState.Running,
        TaskRegistered = true,
        AgentRunning = true,
        Paused = false,
        AgentLastCheck = Now.AddSeconds(-30),
        AgentLastCode = "NoChange",
        WslRunning = true,
        ForwardRuleExists = true,
        FirewallRuleExists = true,
        Listening = true,
        SshAnswers = true,
        Now = Now,
    };

    private static StatusLine Line(StatusReport r, string title) => Assert.Single(r.Lines, l => l.Title == title);

    [Fact]
    public void Healthy_is_good_with_plain_words_and_no_technical_ids()
    {
        var r = StatusEvaluator.Evaluate(Healthy());
        Assert.Equal(Health.Good, r.Overall);
        Assert.Contains("SSH로 접속할 수 있습니다", r.Headline);
        Assert.All(r.Lines, l => Assert.Equal(Health.Good, l.Health));
        var all = string.Join(" ", r.Lines.Select(l => l.Text));
        Assert.DoesNotContain("S-1-5", all);
        Assert.DoesNotContain("portproxy", all, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(r.NextSteps);
    }

    [Fact]
    public void Paused_is_off_not_failure()
    {
        var r = StatusEvaluator.Evaluate(Healthy() with { Paused = true, WslRunning = false, SshAnswers = false, AgentRunning = true });
        Assert.Equal(Health.Off, r.Overall);
        Assert.Contains("resume-monitoring", r.Headline);
        Assert.Equal(Health.Off, Line(r, "WSL").Health);
    }

    [Fact]
    public void Configured_but_not_listening_is_explained() // T15 실측 상황
    {
        var r = StatusEvaluator.Evaluate(Healthy() with { Listening = false, SshAnswers = false });
        Assert.Equal(Health.Bad, r.Overall);
        Assert.Contains("연결 통로", Line(r, "SSH 접속").Text);
        Assert.Contains("자동 복구", Line(r, "연결 통로").Text);
    }

    [Fact]
    public void Agent_not_running_suggests_relogin()
    {
        var r = StatusEvaluator.Evaluate(Healthy() with { AgentRunning = false, WslRunning = false, SshAnswers = false });
        Assert.Equal(Health.Bad, Line(r, "자동 연결 관리").Health);
        Assert.Contains(r.NextSteps, s => s.Contains("다시 로그인"));
    }

    [Fact]
    public void Other_account_cannot_see_agent_state()
    {
        var r = StatusEvaluator.Evaluate(Healthy() with { CurrentUser = "consoleuser", AgentRunning = null });
        Assert.Equal(Health.Unknown, Line(r, "자동 연결 관리").Health);
    }

    [Fact]
    public void Stale_or_failing_agent_is_warning_while_ssh_still_works()
    {
        var stale = StatusEvaluator.Evaluate(Healthy() with { AgentLastCheck = Now.AddMinutes(-20) });
        Assert.Equal(Health.Warn, stale.Overall);
        Assert.Contains("20분 전", Line(stale, "자동 연결 관리").Text);

        var failing = StatusEvaluator.Evaluate(Healthy() with { AgentFailures = 3, AgentLastCode = "Error" });
        Assert.Equal(Health.Warn, Line(failing, "자동 연결 관리").Health);
    }

    [Fact]
    public void Stopped_service_and_missing_rules_give_next_steps()
    {
        var r = StatusEvaluator.Evaluate(Healthy() with { Service = ServiceState.Stopped, ForwardRuleExists = false, Listening = false, SshAnswers = false });
        Assert.Contains(r.NextSteps, s => s.Contains("autostart install"));
        Assert.Contains(r.NextSteps, s => s.Contains("forward apply"));
    }

    [Fact]
    public void Not_installed_is_off()
    {
        var r = StatusEvaluator.Evaluate(new StatusFacts { Installed = false, Now = Now });
        Assert.Equal(Health.Off, r.Overall);
    }
}
