using System.Xml.Linq;
using WslRemoteSetup.Core.Autostart;

namespace WslRemoteSetup.UnitTests;

public class LogonTaskXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Sid = "S-1-5-21-1000000001-2000000002-3000000003-1002";

    private static XDocument Build(string args = "agent --installation-id x") =>
        XDocument.Parse(LogonTaskXml.Build(Sid, @"C:\Windows\System32\conhost.exe", args, "WslRemoteSetup agent <5f3c9a10>"));

    [Fact]
    public void Runs_as_owner_interactive_token_least_privilege_without_password()
    {
        var doc = Build();
        var p = doc.Descendants(Ns + "Principal").Single();
        Assert.Equal(Sid, p.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", p.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", p.Element(Ns + "RunLevel")!.Value);
        Assert.DoesNotContain("Password", doc.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HighestAvailable", doc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Triggered_only_by_owner_logon_single_instance_no_time_limit()
    {
        var doc = Build();
        var trig = doc.Descendants(Ns + "Triggers").Single().Elements().Single();
        Assert.Equal("LogonTrigger", trig.Name.LocalName);
        Assert.Equal(Sid, trig.Element(Ns + "UserId")!.Value);
        Assert.Equal("IgnoreNew", doc.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("PT0S", doc.Descendants(Ns + "ExecutionTimeLimit").Single().Value);
    }

    [Fact]
    public void Special_characters_are_escaped()
    {
        var doc = Build("agent --x \"a<b>&c\"");
        Assert.Equal("agent --x \"a<b>&c\"", doc.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal("WslRemoteSetup agent <5f3c9a10>", doc.Descendants(Ns + "Description").Single().Value);
    }

    [Theory]
    [InlineData("S-1-5-18")]       // SYSTEM
    [InlineData("S-1-5-32-544")]   // Administrators 그룹
    [InlineData("ownerAdmin")]
    public void Non_user_principals_are_rejected(string sid) =>
        Assert.Throws<ArgumentException>(() => LogonTaskXml.Build(sid, @"C:\x.exe", "", ""));

    [Fact]
    public void Boot_task_uses_startup_trigger_and_s4u_without_password()
    {
        var doc = XDocument.Parse(LogonTaskXml.BuildBoot(Sid, @"C:\Windows\System32\conhost.exe", "agent", "boot poc"));
        var trig = doc.Descendants(Ns + "Triggers").Single().Elements().Single();
        Assert.Equal("BootTrigger", trig.Name.LocalName);
        var p = doc.Descendants(Ns + "Principal").Single();
        Assert.Equal(Sid, p.Element(Ns + "UserId")!.Value);
        Assert.Equal("S4U", p.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", p.Element(Ns + "RunLevel")!.Value);
        Assert.DoesNotContain("Password", doc.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Relative_command_is_rejected() =>
        Assert.Throws<ArgumentException>(() => LogonTaskXml.Build(Sid, "WslRemoteSetup.exe", "", ""));
}
