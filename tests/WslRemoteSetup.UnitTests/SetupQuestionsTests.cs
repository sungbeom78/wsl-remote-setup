using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Setup;

namespace WslRemoteSetup.UnitTests;

public class PortAdvisorTests
{
    // G2 실측(2026-10-18) 형태의 netsh 출력
    private const string Netsh = """
        Protocol tcp Port Exclusion Ranges

        Start Port    End Port
        ----------    --------
              5357        5357
             50000       50059     *
             58742       58841

        * - Administered port exclusions.
        """;

    private static readonly IReadOnlyList<(int, int)> Excluded = PortAdvisor.ParseExcludedRanges(Netsh);

    [Fact]
    public void Parses_ranges_ignoring_text() =>
        Assert.Equal([(5357, 5357), (50000, 50059), (58742, 58841)], Excluded);

    [Fact]
    public void Default_1004_is_available_on_g2() =>
        Assert.True(PortAdvisor.Check(1004, [135, 445, 7389], [], Excluded, 7389).Usable);

    [Theory]
    [InlineData(7389, PortVerdict.Reserved)]   // G2 RDP 포트
    [InlineData(3389, PortVerdict.Reserved)]
    [InlineData(445, PortVerdict.Reserved)]
    [InlineData(50010, PortVerdict.Excluded)]  // Windows 예약 범위
    [InlineData(2222, PortVerdict.InUse)]
    [InlineData(1005, PortVerdict.ProxyInUse)]
    [InlineData(0, PortVerdict.Invalid)]
    public void Conflicts_are_classified(int port, PortVerdict verdict) =>
        Assert.Equal(verdict, PortAdvisor.Check(port, [2222], [1005], Excluded, 7389).Verdict);

    [Fact]
    public void Own_port_is_reusable_on_rerun() => // T17: 재실행 시 자기 규칙을 충돌로 보지 않음
        Assert.True(PortAdvisor.Check(1004, [1004], [1004], Excluded, 7389, ownedPort: 1004).Usable);

    [Fact]
    public void Suggests_next_free_port() // T04
    {
        Assert.Equal(1006, PortAdvisor.Suggest(1004, [1004], [1005], Excluded, 7389));
        Assert.Equal(50060, PortAdvisor.Suggest(50000, [], [], Excluded, 7389));
    }
}

public class SetupQuestionsTests
{
    [Theory]
    [InlineData("", 1004)]
    [InlineData("  ", 1004)]
    [InlineData("2222", 2222)]
    public void Port_defaults_on_enter(string input, int expected) => Assert.Equal(expected, SetupQuestions.Port(input, 1004).Value);

    [Theory]
    [InlineData("abc")]
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("10 04")]
    public void Port_rejects_invalid(string input) => Assert.False(SetupQuestions.Port(input, 1004).Ok);

    [Fact]
    public void Linux_user_defaults_and_validates()
    {
        Assert.Equal("linux_admin", SetupQuestions.LinuxUser("", "linux_admin").Value);
        Assert.Equal("dev1", SetupQuestions.LinuxUser("dev1", "linux_admin").Value);
        Assert.False(SetupQuestions.LinuxUser("root", "linux_admin").Ok);
        Assert.False(SetupQuestions.LinuxUser("Bad User", "linux_admin").Ok);
    }

    [Fact]
    public void Windows_account_must_exist_and_keeps_real_casing()
    {
        string[] accounts = ["ownerAdmin", "consoleuser"];
        Assert.Equal("ownerAdmin", SetupQuestions.WindowsAccount("", "ownerAdmin", accounts).Value);
        Assert.Equal("ownerAdmin", SetupQuestions.WindowsAccount("OWNERADMIN", "x", accounts).Value);
        Assert.False(SetupQuestions.WindowsAccount("remoteControl", "x", accounts).Ok);
        Assert.False(SetupQuestions.WindowsAccount("a/b", "x", accounts).Ok);
    }

    [Theory]
    [InlineData("", SshAuthentication.PasswordAndPublicKey)]
    [InlineData("1", SshAuthentication.PasswordAndPublicKey)]
    [InlineData("2", SshAuthentication.PublicKeyOnly)]
    public void Ssh_auth_choice(string input, SshAuthentication expected) => Assert.Equal(expected, SetupQuestions.SshAuth(input).Value);

    [Fact]
    public void Ssh_auth_rejects_other() => Assert.False(SetupQuestions.SshAuth("3").Ok);
}
