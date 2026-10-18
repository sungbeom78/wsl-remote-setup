using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Ipc;

/// <summary>
/// 소유자 에이전트 → 네트워크 관리부 요청. 관찰한 WSL IPv4만 담는다.
/// 포트·수신 주소·명령·경로·SID는 담지 않으며, 호출자 신원은 파이프에서 확인한다.
/// </summary>
public sealed record RepairRequest(Guid InstallationId, IPAddress ObservedWslIPv4)
{
    public const int ProtocolVersion = 1;
    public const int MaxBytes = 1024;

    private const string PropVersion = "v";
    private const string PropInstallation = "installationId";
    private const string PropAddress = "observedWslIPv4";

    public byte[] ToUtf8()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber(PropVersion, ProtocolVersion);
            w.WriteString(PropInstallation, InstallationId.ToString("D"));
            w.WriteString(PropAddress, ObservedWslIPv4.ToString());
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>엄격한 파서: 크기 제한, 정확히 세 필드, 중복·미지 필드 거부, 타입 고정.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out RepairRequest? request, out string error)
    {
        request = null;
        if (utf8.Length == 0) { error = "빈 요청"; return false; }
        if (utf8.Length > MaxBytes) { error = "요청 크기 초과"; return false; }

        int? version = null;
        Guid? installation = null;
        IPAddress? address = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 2, CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { error = "객체가 아님"; return false; }
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) { error = "형식 오류"; return false; }
                var name = reader.GetString()!;
                if (!seen.Add(name)) { error = $"중복 필드 {name}"; return false; }
                if (!reader.Read()) { error = "형식 오류"; return false; }
                switch (name)
                {
                    case PropVersion when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v):
                        version = v;
                        break;
                    case PropInstallation when reader.TokenType == JsonTokenType.String
                        && Guid.TryParseExact(reader.GetString(), "D", out var g):
                        installation = g;
                        break;
                    case PropAddress when reader.TokenType == JsonTokenType.String
                        && InputValidator.TryParseIPv4(reader.GetString(), out var ip):
                        address = ip;
                        break;
                    case PropVersion or PropInstallation or PropAddress:
                        error = $"필드 값 오류 {name}";
                        return false;
                    default:
                        error = $"허용되지 않은 필드 {name}";
                        return false;
                }
            }
            if (reader.Read()) { error = "객체 뒤 추가 데이터"; return false; }
        }
        catch (JsonException)
        {
            error = "JSON 오류";
            return false;
        }

        if (version != ProtocolVersion) { error = "지원하지 않는 프로토콜 버전"; return false; }
        if (installation is null || address is null) { error = "필수 필드 누락"; return false; }
        request = new RepairRequest(installation.Value, address);
        error = "";
        return true;
    }
}

public enum RepairCode
{
    Updated,
    NoChange,
    Denied,
    Conflict,
    RateLimited,
    EndpointUnreachable,
    Paused,
    Error,
}

/// <summary>관리부 응답. 내부 경로·명령·정책 세부값을 노출하지 않는 짧은 메시지만 담는다.</summary>
public sealed record RepairResponse(RepairCode Code, string Message)
{
    public const int MaxBytes = 1024;

    public byte[] ToUtf8()
    {
        var message = Message.Length > 200 ? Message[..200] : Message;
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["code"] = Code.ToString(),
            ["message"] = message,
        }));
    }

    public static RepairResponse Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxBytes) return new(RepairCode.Error, "응답 크기 초과");
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(utf8);
            if (d is not null && d.TryGetValue("code", out var c) && Enum.TryParse<RepairCode>(c, out var code))
                return new(code, d.GetValueOrDefault("message", ""));
        }
        catch (JsonException) { }
        return new(RepairCode.Error, "응답 형식 오류");
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Code}: {Message}");
}
