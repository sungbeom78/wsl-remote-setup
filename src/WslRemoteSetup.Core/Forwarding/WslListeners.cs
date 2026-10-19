using System.Globalization;
using System.Text.RegularExpressions;

namespace WslRemoteSetup.Core.Forwarding;

/// <summary>WSL 안에서 연결을 받고 있는 TCP 포트 하나(ADR 0011).</summary>
/// <param name="LoopbackOnly">127.0.0.1·::1에만 열려 있어 Windows·다른 기기에서 닿을 수 없음.</param>
public sealed record WslListener(int Port, bool LoopbackOnly, string? Process);

/// <summary>`ss -ltnpH` 출력 해석과 추가 포트 추천(순수 함수).</summary>
public static partial class WslListeners
{
    /// <summary>외부에 열 때 한 번 더 경고할 포트(데이터베이스·캐시 등).</summary>
    public static readonly IReadOnlyDictionary<int, string> Sensitive = new Dictionary<int, string>
    {
        [3306] = "MySQL", [5432] = "PostgreSQL", [6379] = "Redis", [27017] = "MongoDB",
        [1433] = "SQL Server", [9200] = "Elasticsearch", [11211] = "Memcached", [5984] = "CouchDB",
    };

    [GeneratedRegex("users:\\(\\(\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessRegex();

    public static IReadOnlyList<WslListener> Parse(string ssOutput)
    {
        var found = new Dictionary<int, (bool AnyPublic, string? Process)>();
        foreach (var raw in ssOutput.Split('\n'))
        {
            var cols = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 5 || cols[0] != "LISTEN") continue;
            var local = cols[3];
            var sep = local.LastIndexOf(':');
            if (sep <= 0 || !int.TryParse(local[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)) continue;
            var addr = local[..sep].Trim('[', ']');
            var pct = addr.IndexOf('%', StringComparison.Ordinal);
            if (pct >= 0) addr = addr[..pct];
            // 10.255.255.254: WSL이 lo에 만드는 내부 DNS 주소(밖에서 닿지 않음, G2 실측)
            var isLoopback = addr.StartsWith("127.", StringComparison.Ordinal) || addr is "::1" or "localhost" or "10.255.255.254";
            var proc = ProcessRegex().Match(raw) is { Success: true } m ? m.Groups[1].Value : null;
            found[port] = found.TryGetValue(port, out var prev)
                ? (prev.AnyPublic || !isLoopback, prev.Process ?? proc)
                : (!isLoopback, proc);
        }
        return [.. found.OrderBy(kv => kv.Key).Select(kv => new WslListener(kv.Key, !kv.Value.AnyPublic, kv.Value.Process))];
    }

    /// <summary>프로세스 이름으로 서비스 이름 추천(영문 소문자·숫자·-, 16자 이하). 겹치면 뒤에 숫자를 붙인다.</summary>
    public static string SuggestName(WslListener l, IReadOnlyCollection<string> taken)
    {
        var baseName = new string((l.Process ?? "").ToLowerInvariant().Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-').ToArray());
        if (baseName.Length == 0 || !char.IsAsciiLetterLower(baseName[0])) baseName = "port" + l.Port.ToString(CultureInfo.InvariantCulture);
        if (baseName == "ssh" || baseName == "sshd") baseName = "ssh2";
        baseName = baseName[..Math.Min(13, baseName.Length)];
        var name = baseName;
        for (var i = 2; taken.Contains(name); i++) name = baseName + i.ToString(CultureInfo.InvariantCulture);
        return name;
    }
}
