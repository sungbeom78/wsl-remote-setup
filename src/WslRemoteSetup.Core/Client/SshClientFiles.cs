using System.Text;

namespace WslRemoteSetup.Core.Client;

public sealed record EditResult(string? NewText, bool Changed, string? Conflict);

/// <summary>
/// 노트북의 `~/.ssh/config`·`known_hosts` 편집(순수 함수). 이 도구 표식이 있는 블록만 바꾸고,
/// 사용자가 직접 만든 같은 이름의 Host나 다른 서버 키가 있으면 덮어쓰지 않고 충돌로 알린다.
/// </summary>
public static class SshClientFiles
{
    public static string BeginMarker(string name) => $"# >>> WslRemoteSetup {name} (자동 관리 — 직접 고치지 마세요)";
    public static string EndMarker(string name) => $"# <<< WslRemoteSetup {name}";

    public static string HostBlock(ClientProfile p, string identityFile) => string.Join('\n',
        BeginMarker(p.Name),
        $"Host {p.Name}",
        $"    HostName {p.Host}",
        $"    Port {p.SshPort}",
        $"    User {p.User}",
        $"    IdentityFile \"{identityFile}\"",
        "    IdentitiesOnly yes",
        "    StrictHostKeyChecking yes",
        "    ServerAliveInterval 15",
        "    ServerAliveCountMax 3",
        EndMarker(p.Name));

    public static EditResult UpsertConfig(string? existing, ClientProfile p, string identityFile)
    {
        var text = (existing ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var block = HostBlock(p, identityFile);
        var begin = BeginMarker(p.Name);
        var end = EndMarker(p.Name);
        var bi = text.IndexOf(begin, StringComparison.Ordinal);
        if (bi >= 0)
        {
            var ei = text.IndexOf(end, bi, StringComparison.Ordinal);
            if (ei < 0) return new(null, false, "이 도구가 만든 블록의 끝 표식이 없습니다. config 파일을 직접 확인해 주세요.");
            var current = text.Substring(bi, ei + end.Length - bi);
            if (current == block) return new(existing, false, null);
            return new(text[..bi] + block + text[(ei + end.Length)..], true, null);
        }

        // 표식 밖에 같은 이름의 Host가 있으면 사용자 설정이므로 건드리지 않는다.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Host ", StringComparison.OrdinalIgnoreCase)) continue;
            var names = line[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)))
                return new(null, false, $"config에 직접 만든 'Host {p.Name}'가 이미 있습니다. 다른 이름을 쓰거나 그 항목을 지운 뒤 다시 실행해 주세요.");
        }
        // 새 블록은 맨 앞에 둔다(ssh config는 먼저 나온 값이 우선이므로, 넓은 'Host *' 설정보다 앞서야 한다).
        var sep = text.Length == 0 ? "" : "\n\n";
        return new(block + sep + text, true, null);
    }

    private static readonly HashSet<string> KeyTypes = ["ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-rsa"];

    /// <summary>
    /// 공개키 등록용 원격 명령(고정 형식). 키는 공개 정보라 인수로 넘겨도 되며, 형식을 엄격히 검사해
    /// 따옴표·셸 메타문자가 들어갈 수 없게 한다. 같은 키가 있으면 추가하지 않는다.
    /// </summary>
    public static string AuthorizedKeyCommand(string publicKeyLine)
    {
        var parts = publicKeyLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !KeyTypes.Contains(parts[0]) || !Core.Ssh.HostKeyFingerprint.ParseKeyscan("h " + parts[0] + " " + parts[1]).Any())
            throw new FormatException("공개키 형식이 올바르지 않습니다.");
        var comment = parts.Length > 2 ? new string(string.Join('-', parts[2..]).Where(c => char.IsAsciiLetterOrDigit(c) || c is '@' or '.' or '_' or '-').ToArray()) : "";
        var key = comment.Length > 0 ? $"{parts[0]} {parts[1]} {comment}" : $"{parts[0]} {parts[1]}";
        var match = $"{parts[0]} {parts[1]}";
        return "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
               $"if grep -qF '{match}' ~/.ssh/authorized_keys; then echo WRS_KEY_PRESENT; " +
               $"else echo '{key}' >> ~/.ssh/authorized_keys && echo WRS_KEY_ADDED; fi";
    }

    /// <summary>known_hosts에 서버 키를 미리 등록. 같은 호스트에 다른 키가 있으면 충돌(중간자 또는 서버 재설치 가능성).</summary>
    public static EditResult UpsertKnownHosts(string? existing, ClientProfile p)
    {
        var text = (existing ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var line = p.KnownHostsLine();
        var parts = line.Split(' ');
        var hostPart = parts[0];
        foreach (var raw in text.Split('\n'))
        {
            var cols = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 3 || cols[0].StartsWith('#')) continue;
            if (!cols[0].Split(',').Contains(hostPart, StringComparer.Ordinal) || cols[1] != parts[1]) continue;
            return cols[2] == parts[2]
                ? new(existing, false, null)
                : new(null, false, "이 노트북에 이 서버의 다른 키가 등록되어 있습니다. 서버를 다시 설치한 경우가 아니라면 접속하지 마세요.");
        }
        var sb = new StringBuilder(text);
        if (text.Length > 0 && !text.EndsWith('\n')) sb.Append('\n');
        sb.Append(line).Append('\n');
        return new(sb.ToString(), true, null);
    }
}
