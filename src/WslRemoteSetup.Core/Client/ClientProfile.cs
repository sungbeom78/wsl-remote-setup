using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Client;

/// <summary>
/// 연결 정보 파일(ADR 0009). 서버에서 `export-client`로 만들고 노트북의 `client` 모드가 읽는다.
/// 공개 정보만 담는다: 비밀번호·개인키·설치 ID·SID는 넣지 않는다.
/// </summary>
public sealed partial record ClientProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    /// <summary>접속 별칭(예: G2). ssh config의 Host 이름으로 쓴다.</summary>
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int SshPort { get; init; }
    public required string User { get; init; }
    public int? RdpPort { get; init; }
    public string? RdpUser { get; init; }
    /// <summary>서버 SSH 공개키 한 줄(예: "ssh-ed25519 AAAA…"). known_hosts에 미리 등록한다.</summary>
    public required string HostKey { get; init; }
    /// <summary>서버 키 지문(SHA256:…). HostKey에서 다시 계산해 일치해야 한다.</summary>
    public required string HostKeyFingerprint { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostnameRegex();

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (Version != CurrentVersion) e.Add("지원하지 않는 연결 정보 파일 버전입니다.");
        if (!NameRegex().IsMatch(Name)) e.Add("이름은 영문·숫자·.·_·-만 쓸 수 있습니다(32자 이하).");
        if (!InputValidator.TryParseIPv4(Host, out _) && !HostnameRegex().IsMatch(Host)) e.Add("서버 주소 형식이 올바르지 않습니다.");
        if (!InputValidator.Port(SshPort).IsValid) e.Add("SSH 포트가 올바르지 않습니다.");
        if (!InputValidator.LinuxUserName(User).IsValid) e.Add("Linux 사용자 이름이 올바르지 않습니다.");
        if (RdpPort is { } rp && !InputValidator.Port(rp).IsValid) e.Add("원격 데스크톱 포트가 올바르지 않습니다.");
        if (RdpUser is not null && !InputValidator.WindowsAccountName(RdpUser).IsValid) e.Add("원격 데스크톱 사용자 이름이 올바르지 않습니다.");
        var keys = Ssh.HostKeyFingerprint.ParseKeyscan("host " + HostKey);
        if (keys.Count != 1) e.Add("서버 공개키 형식이 올바르지 않습니다.");
        else if (keys[0].Fingerprint != HostKeyFingerprint) e.Add("서버 공개키와 지문이 일치하지 않습니다(파일이 바뀌었을 수 있음).");
        return e;
    }

    public string ToJson() => JsonSerializer.Serialize(this, StateStore.JsonOptions);

    /// <summary>엄격하게 읽는다: 크기 제한, 비밀값으로 보이는 필드 거부, 검증 실패 시 예외.</summary>
    public static ClientProfile Parse(string json)
    {
        if (json.Length > 16 * 1024) throw new InvalidDataException("연결 정보 파일이 너무 큽니다.");
        if (json.Contains("PRIVATE KEY", StringComparison.Ordinal) || json.Contains("\"Password", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("연결 정보 파일에 비밀값으로 보이는 내용이 있습니다.");
        var p = JsonSerializer.Deserialize<ClientProfile>(json, StateStore.JsonOptions) ?? throw new InvalidDataException("빈 파일입니다.");
        var errors = p.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        return p;
    }

    /// <summary>known_hosts 항목. 포트가 22가 아니면 [host]:port 형식.</summary>
    public string KnownHostsLine()
    {
        var parts = HostKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hostPart = SshPort == 22 ? Host : string.Create(CultureInfo.InvariantCulture, $"[{Host}]:{SshPort}");
        return $"{hostPart} {parts[0]} {parts[1]}";
    }

    /// <summary>RDP 바로가기(.rdp) 내용. 비밀번호는 넣지 않는다(접속 때 Windows가 묻는다). NLA 유지.</summary>
    public string? RdpFile()
    {
        if (RdpPort is not { } port) return null;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"full address:s:{Host}:{port}\r\n");
        if (RdpUser is not null) sb.Append(CultureInfo.InvariantCulture, $"username:s:{RdpUser}\r\n");
        sb.Append("prompt for credentials:i:1\r\n");
        sb.Append("authentication level:i:2\r\n");
        sb.Append("enablecredsspsupport:i:1\r\n");
        sb.Append("screen mode id:i:2\r\n");
        sb.Append("smart sizing:i:1\r\n");
        sb.Append("dynamic resolution:i:1\r\n");
        return sb.ToString();
    }
}
