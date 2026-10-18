using System.Text;
using WslRemoteSetup.Core.Execution;

namespace WslRemoteSetup.UnitTests;

public class OutputDecoderTests
{
    [Theory]
    [InlineData("WSL 버전: 3.0.1.0\r\n커널 버전: 6.18.40.1-1")]
    [InlineData("기본 버전: 2")]
    [InlineData("Default Version: 2")]
    public void Utf16le_without_bom_is_detected(string text) =>
        Assert.Equal(text, OutputDecoder.Decode(Encoding.Unicode.GetBytes(text)));

    [Theory]
    [InlineData("linux_admin uid=1000")]
    [InlineData("한글 UTF-8 출력")]
    public void Utf8_is_decoded(string text) =>
        Assert.Equal(text, OutputDecoder.Decode(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Boms_are_honoured()
    {
        Assert.Equal("가", OutputDecoder.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("가")]));
        Assert.Equal("가", OutputDecoder.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("가")]));
    }

    [Fact]
    public void Empty_is_empty() => Assert.Equal("", OutputDecoder.Decode([]));
}

public class SecretMaskerTests
{
    [Fact]
    public void Masks_all_occurrences_longest_first()
    {
        var text = "pw=Secr3t! and Secr3t again, part Secr";
        var masked = SecretMasker.Apply(text, ["Secr", "Secr3t!"]);
        Assert.DoesNotContain("Secr3t!", masked);
        Assert.DoesNotContain("Secr", masked);
    }

    [Fact]
    public void Empty_secrets_are_ignored() =>
        Assert.Equal("abc", SecretMasker.Apply("abc", ["", null!]));
}

public class CommandRunnerTests
{
    private static readonly string Sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

    [Fact]
    public void Relative_path_is_rejected()
    {
        var runner = new CommandRunner([Path.Combine(Sys, "wsl.exe")]);
        Assert.Throws<CommandNotAllowedException>(() => runner.EnsureAllowed(new CommandSpec("wsl.exe", [])));
    }

    [Fact]
    public void Executable_outside_allowlist_is_rejected()
    {
        var runner = new CommandRunner([Path.Combine(Sys, "wsl.exe")]);
        Assert.Throws<CommandNotAllowedException>(() =>
            runner.EnsureAllowed(new CommandSpec(Path.Combine(Sys, "cmd.exe"), ["/c", "echo"])));
    }

    [Fact]
    public void Nul_in_argument_is_rejected()
    {
        var wsl = Path.Combine(Sys, "wsl.exe");
        var runner = new CommandRunner([wsl]);
        Assert.Throws<CommandNotAllowedException>(() => runner.EnsureAllowed(new CommandSpec(wsl, ["a\0b"])));
    }

    [Fact]
    public void Allowlisted_spec_passes_validation_without_running()
    {
        var wsl = Path.Combine(Sys, "wsl.exe");
        var runner = new CommandRunner([wsl]);
        runner.EnsureAllowed(new CommandSpec(wsl.ToUpperInvariant(), ["--version"]));
    }

    [Fact]
    public void Non_positive_timeout_is_rejected()
    {
        var wsl = Path.Combine(Sys, "wsl.exe");
        var runner = new CommandRunner([wsl]);
        Assert.Throws<ArgumentException>(() => runner.EnsureAllowed(new CommandSpec(wsl, []) { Timeout = TimeSpan.Zero }));
    }
}
