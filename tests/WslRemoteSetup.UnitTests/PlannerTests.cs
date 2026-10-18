using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Planning;

namespace WslRemoteSetup.UnitTests;

public class PlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(9));

    private static PlanStep Step(InstallationPlan p, string id) => Assert.Single(p.Steps, s => s.Id == id);

    [Fact]
    public void G2_after_bootstrap_needs_no_runtime_distro_or_user_changes()
    {
        var plan = Planner.Create(Fixtures.G2AfterBootstrap(), new DesiredConfiguration(), Now);
        Assert.Equal(ChangeNeed.NotRequired, Step(plan, "wsl-runtime").Change);
        Assert.Equal(ChangeNeed.NotRequired, Step(plan, "owner-account").Change);
        Assert.Equal(ChangeNeed.NotRequired, Step(plan, "distro-register").Change);
        Assert.Equal(ChangeNeed.NotRequired, Step(plan, "linux-user").Change);
        Assert.Equal(ChangeNeed.NotRequired, Step(plan, "rdp").Change); // 기존 7389 유지
        Assert.Contains("7389", Step(plan, "rdp").Target);
        Assert.Equal(ChangeNeed.Required, Step(plan, "portproxy").Change);
    }

    [Fact]
    public void Fresh_windows_requires_runtime_with_reboot_and_admin()
    {
        var plan = Planner.Create(Fixtures.FreshWindows(), new DesiredConfiguration(), Now);
        var rt = Step(plan, "wsl-runtime");
        Assert.Equal(ChangeNeed.Required, rt.Change);
        Assert.True(rt.MayRequireReboot);
        Assert.Equal(StepExecutor.Administrator, rt.Executor);
        Assert.Equal(StepExecutor.OwnerSession, Step(plan, "distro-register").Executor);
        Assert.Equal(StepExecutor.Person, Step(plan, "linux-user").Executor);
    }

    [Fact]
    public void Different_owner_than_current_user_does_not_claim_distro_state()
    {
        // T02: 현재 관리자와 소유자가 다르면 현재 계정의 배포판 목록으로 판단하지 않는다.
        var plan = Planner.Create(Fixtures.G2AfterBootstrap(), new DesiredConfiguration { OwnerAccountName = "consoleuser" }, Now);
        Assert.Equal(ChangeNeed.Unknown, Step(plan, "distro-register").Change);
    }

    [Fact]
    public void Missing_owner_account_requires_creation()
    {
        var plan = Planner.Create(Fixtures.G2AfterBootstrap(), new DesiredConfiguration { OwnerAccountName = "remoteControl" }, Now);
        Assert.Equal(ChangeNeed.Required, Step(plan, "owner-account").Change);
    }

    [Fact]
    public void Port_conflict_is_not_planned_as_overwrite()
    {
        var s = Fixtures.G2AfterBootstrap() with { TcpListenPorts = [1004] };
        var step = Step(Planner.Create(s, new DesiredConfiguration(), Now), "portproxy");
        Assert.Equal(ChangeNeed.Unknown, step.Change);
        Assert.Contains("PORT_CONFLICT", step.NotImplementedNote);
    }

    [Fact]
    public void Distro_is_never_planned_as_tool_removable()
    {
        var plan = Planner.Create(Fixtures.FreshWindows(), new DesiredConfiguration(), Now);
        Assert.Equal(Reversibility.NotReversible, Step(plan, "distro-register").Revert);
    }

    [Fact]
    public void Dependencies_reference_earlier_steps_only()
    {
        var plan = Planner.Create(Fixtures.FreshWindows(), new DesiredConfiguration(), Now);
        var seen = new HashSet<string>();
        foreach (var s in plan.Steps)
        {
            Assert.All(s.DependsOn, d => Assert.Contains(d, seen));
            seen.Add(s.Id);
        }
    }

    [Fact]
    public void Hash_ignores_creation_time_but_changes_with_content()
    {
        var a = Planner.Create(Fixtures.G2AfterBootstrap(), new DesiredConfiguration(), Now);
        var b = Planner.Create(Fixtures.G2AfterBootstrap(), new DesiredConfiguration(), Now.AddHours(1));
        var c = Planner.Create(Fixtures.G2AfterBootstrap(), new DesiredConfiguration { ListenPort = 2222 }, Now);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), c.ComputeHash());
    }

    [Fact]
    public void Invalid_desired_configuration_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Planner.Create(Fixtures.G2AfterBootstrap(), new DesiredConfiguration { LinuxUser = "root" }, Now));
    }
}
