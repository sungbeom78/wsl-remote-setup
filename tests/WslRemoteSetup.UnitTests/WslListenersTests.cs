using WslRemoteSetup.Core.Forwarding;

namespace WslRemoteSetup.UnitTests;

public class WslListenersTests
{
    private const string Sample = """
        LISTEN 0      4096   127.0.0.53%lo:53        0.0.0.0:*    users:(("systemd-resolve",pid=150,fd=15))
        LISTEN 0      128          0.0.0.0:22        0.0.0.0:*    users:(("sshd",pid=200,fd=3))
        LISTEN 0      128             [::]:22           [::]:*    users:(("sshd",pid=200,fd=4))
        LISTEN 0      511                *:3000              *:*    users:(("node",pid=812,fd=21))
        LISTEN 0      244        127.0.0.1:5432        0.0.0.0:*    users:(("postgres",pid=90,fd=6))
        LISTEN 0      244            [::1]:5432           [::]:*    users:(("postgres",pid=90,fd=7))
        LISTEN 0      5          127.0.0.1:8888        0.0.0.0:*
        garbage line
        """;

    [Fact]
    public void Parses_ports_processes_and_loopback()
    {
        var l = WslListeners.Parse(Sample.Replace("\r", "", StringComparison.Ordinal));
        Assert.Equal([22, 53, 3000, 5432, 8888], l.Select(x => x.Port));
        Assert.False(l.Single(x => x.Port == 22).LoopbackOnly);
        Assert.Equal("sshd", l.Single(x => x.Port == 22).Process);
        Assert.False(l.Single(x => x.Port == 3000).LoopbackOnly);
        Assert.True(l.Single(x => x.Port == 5432).LoopbackOnly);
        Assert.True(l.Single(x => x.Port == 53).LoopbackOnly);
        Assert.Null(l.Single(x => x.Port == 8888).Process);
    }

    [Fact]
    public void Public_address_on_any_line_makes_port_reachable()
    {
        var l = WslListeners.Parse("LISTEN 0 1 127.0.0.1:8080 0.0.0.0:*\nLISTEN 0 1 0.0.0.0:8080 0.0.0.0:*\n");
        Assert.False(Assert.Single(l).LoopbackOnly);
    }

    [Theory]
    [InlineData("node", 3000, "node")]
    [InlineData("python3", 8000, "python3")]
    [InlineData(null, 8888, "port8888")]
    [InlineData("sshd", 2222, "ssh2")]
    [InlineData("Very_Long-Process.Name123", 1, "verylong-proc")]
    public void Suggests_valid_names(string? process, int port, string expected) =>
        Assert.Equal(expected, WslListeners.SuggestName(new WslListener(port, false, process), []));

    [Fact]
    public void Suggested_name_avoids_taken() =>
        Assert.Equal("node2", WslListeners.SuggestName(new WslListener(3001, false, "node"), ["node"]));

    [Fact]
    public void Database_ports_are_sensitive() => Assert.True(WslListeners.Sensitive.ContainsKey(5432));
}

public class WslListenersRealOutputTests
{
    // G2 실측 출력(2026-10-20)
    private const string G2 = """
        LISTEN 0      4096    127.0.0.53%lo:53   0.0.0.0:* users:(("systemd-resolve",pid=110,fd=15))
        LISTEN 0      1000   10.255.255.254:53   0.0.0.0:*
        LISTEN 0      4096          0.0.0.0:22   0.0.0.0:* users:(("sshd",pid=245,fd=3),("systemd",pid=1,fd=150))
        LISTEN 0      4096        127.0.0.1:9001 0.0.0.0:* users:(("docker-proxy",pid=422148,fd=8))
        LISTEN 0      4096       127.0.0.54:53   0.0.0.0:* users:(("systemd-resolve",pid=110,fd=17))
        LISTEN 0      4096             [::]:22      [::]:* users:(("sshd",pid=245,fd=4),("systemd",pid=1,fd=151))
        """;

    [Fact]
    public void Wsl_internal_dns_and_docker_localhost_are_loopback_only()
    {
        var l = WslListeners.Parse(G2.Replace("\r", "", StringComparison.Ordinal));
        Assert.True(l.Single(x => x.Port == 53).LoopbackOnly);
        var docker = l.Single(x => x.Port == 9001);
        Assert.True(docker.LoopbackOnly);
        Assert.Equal("docker-proxy", docker.Process);
        Assert.False(l.Single(x => x.Port == 22).LoopbackOnly);
    }
}
