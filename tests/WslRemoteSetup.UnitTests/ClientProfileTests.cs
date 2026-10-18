using WslRemoteSetup.Core.Client;
using WslRemoteSetup.Core.Ssh;

namespace WslRemoteSetup.UnitTests;

public class ClientProfileTests
{
    // 공개키(비밀 아님). 지문은 실제 계산값을 쓴다.
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMw5fXLO/lS5rBnKoaGqOSEzM3Wzm0b9dFhZClMCdyMG";
    private static readonly string Fp = HostKeyFingerprint.ParseKeyscan("h " + Key)[0].Fingerprint;

    private static ClientProfile Sample(Func<ClientProfile, ClientProfile>? edit = null)
    {
        var p = new ClientProfile
        {
            Name = "G2", Host = "192.168.1.222", SshPort = 1004, User = "linux_admin",
            RdpPort = 7389, RdpUser = "ownerAdmin", HostKey = Key, HostKeyFingerprint = Fp,
            CreatedAt = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
        };
        return edit is null ? p : edit(p);
    }

    [Fact]
    public void Sample_is_valid_and_roundtrips()
    {
        var p = Sample();
        Assert.Empty(p.Validate());
        Assert.Equal(p, ClientProfile.Parse(p.ToJson()));
    }

    [Fact]
    public void Json_has_no_secret_or_internal_fields()
    {
        var json = Sample().ToJson();
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallationId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_mismatch_is_rejected()
    {
        var p = Sample(p => p with { HostKeyFingerprint = "SHA256:" + new string('A', 43) });
        Assert.Contains(p.Validate(), e => e.Contains("일치하지 않습니다", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ClientProfile.Parse(p.ToJson()));
    }

    [Theory]
    [InlineData("G 2")]
    [InlineData("-x")]
    [InlineData("")]
    public void Bad_name_rejected(string name) => Assert.NotEmpty(Sample(p => p with { Name = name }).Validate());

    [Theory]
    [InlineData("bad host")]
    [InlineData("-oProxyCommand=x")]
    public void Bad_host_rejected(string host) => Assert.NotEmpty(Sample(p => p with { Host = host }).Validate());

    [Fact]
    public void Hostname_is_allowed() => Assert.Empty(Sample(p => p with { Host = "g2.local" }).Validate());

    [Fact]
    public void Bad_ports_rejected()
    {
        Assert.NotEmpty(Sample(p => p with { SshPort = 0 }).Validate());
        Assert.NotEmpty(Sample(p => p with { RdpPort = 70000 }).Validate());
    }

    [Fact]
    public void Secret_like_content_rejected()
    {
        var json = Sample().ToJson().Replace("\"name\"", "\"password\": \"x\", \"name\"", StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => ClientProfile.Parse(json));
        Assert.Throws<InvalidDataException>(() => ClientProfile.Parse("{\"k\":\"-----BEGIN OPENSSH PRIVATE KEY-----\"}"));
    }

    [Fact]
    public void Known_hosts_line_uses_bracket_form_for_non_22()
    {
        Assert.StartsWith("[192.168.1.222]:1004 ssh-ed25519 AAAA", Sample().KnownHostsLine(), StringComparison.Ordinal);
        Assert.StartsWith("192.168.1.222 ssh-ed25519 ", Sample(p => p with { SshPort = 22 }).KnownHostsLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rdp_file_has_address_and_no_password()
    {
        var rdp = Sample().RdpFile()!;
        Assert.Contains("full address:s:192.168.1.222:7389", rdp, StringComparison.Ordinal);
        Assert.Contains("username:s:ownerAdmin", rdp, StringComparison.Ordinal);
        Assert.DoesNotContain("password", rdp, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Sample(p => p with { RdpPort = null }).RdpFile());
    }
}

public class SshClientFilesTests
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMw5fXLO/lS5rBnKoaGqOSEzM3Wzm0b9dFhZClMCdyMG";
    private const string Id = @"C:\Users\u\.ssh\id_ed25519";

    private static ClientProfile P(int port = 1004) => new()
    {
        Name = "G2", Host = "192.168.1.222", SshPort = port, User = "linux_admin",
        HostKey = Key, HostKeyFingerprint = HostKeyFingerprint.ParseKeyscan("h " + Key)[0].Fingerprint,
    };

    [Fact]
    public void Config_block_added_to_empty_file()
    {
        var r = SshClientFiles.UpsertConfig(null, P(), Id);
        Assert.True(r.Changed);
        Assert.Null(r.Conflict);
        Assert.Contains("Host G2\n    HostName 192.168.1.222\n    Port 1004", r.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_block_goes_first_and_preserves_others()
    {
        const string existing = "Host *\r\n    ServerAliveInterval 60\r\n";
        var r = SshClientFiles.UpsertConfig(existing, P(), Id);
        Assert.StartsWith(SshClientFiles.BeginMarker("G2"), r.NewText, StringComparison.Ordinal);
        Assert.EndsWith("Host *\n    ServerAliveInterval 60\n", r.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_rerun_is_noop_and_update_replaces_only_block()
    {
        var first = SshClientFiles.UpsertConfig("Host other\n    Port 1\n", P(), Id).NewText;
        var again = SshClientFiles.UpsertConfig(first, P(), Id);
        Assert.False(again.Changed);
        var moved = SshClientFiles.UpsertConfig(first, P(2222), Id);
        Assert.True(moved.Changed);
        Assert.Contains("Port 2222", moved.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("Port 1004", moved.NewText, StringComparison.Ordinal);
        Assert.Contains("Host other\n    Port 1\n", moved.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_user_owned_same_host_is_conflict()
    {
        var r = SshClientFiles.UpsertConfig("Host g2 laptop\n    Port 7389\n", P(), Id);
        Assert.NotNull(r.Conflict);
        Assert.Null(r.NewText);
    }

    [Fact]
    public void Config_missing_end_marker_is_conflict()
    {
        var r = SshClientFiles.UpsertConfig(SshClientFiles.BeginMarker("G2") + "\nHost G2\n", P(), Id);
        Assert.NotNull(r.Conflict);
    }

    [Fact]
    public void Known_hosts_added_once()
    {
        var r = SshClientFiles.UpsertKnownHosts("other ssh-rsa AAAA\n", P());
        Assert.True(r.Changed);
        Assert.EndsWith(P().KnownHostsLine() + "\n", r.NewText, StringComparison.Ordinal);
        Assert.False(SshClientFiles.UpsertKnownHosts(r.NewText, P()).Changed);
    }

    [Fact]
    public void Authorized_key_command_is_fixed_and_sanitized()
    {
        var cmd = SshClientFiles.AuthorizedKeyCommand(Key + " laptop-1");
        Assert.Contains($"grep -qF '{Key}'", cmd, StringComparison.Ordinal);
        Assert.Contains($"echo '{Key} laptop-1' >>", cmd, StringComparison.Ordinal);
        var evil = SshClientFiles.AuthorizedKeyCommand(Key + " x';rm -rf ~;'");
        Assert.DoesNotContain(";rm", evil, StringComparison.Ordinal);
        Assert.Equal(4, evil.Count(c => c == '\''));
    }

    [Theory]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP")]
    [InlineData("ssh-ed25519 AAAA'$(id)'")]
    public void Authorized_key_command_rejects_bad_keys(string line) =>
        Assert.Throws<FormatException>(() => SshClientFiles.AuthorizedKeyCommand(line));

    [Fact]
    public void Known_hosts_different_key_is_conflict()
    {
        var r = SshClientFiles.UpsertKnownHosts("[192.168.1.222]:1004 ssh-ed25519 AAAAOTHER\n", P());
        Assert.NotNull(r.Conflict);
    }
}
