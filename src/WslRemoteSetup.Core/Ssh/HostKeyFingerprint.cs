using System.Security.Cryptography;

namespace WslRemoteSetup.Core.Ssh;

public sealed record HostKey(string KeyType, string Fingerprint);

/// <summary>ssh-keyscan 출력 줄에서 OpenSSH 형식 SHA256 지문(SHA256:base64, 패딩 없음)을 계산한다.</summary>
public static class HostKeyFingerprint
{
    public static IReadOnlyList<HostKey> ParseKeyscan(string output)
    {
        var keys = new List<HostKey>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('#')) continue;
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            byte[] blob;
            try { blob = Convert.FromBase64String(parts[2]); }
            catch (FormatException) { continue; }
            if (!BlobTypeMatches(blob, parts[1])) continue;
            keys.Add(new HostKey(parts[1], Compute(blob)));
        }
        return keys;
    }

    /// <summary>
    /// `ssh -v` 디버그 출력의 "Server host key: &lt;type&gt; SHA256:…" 줄을 해석한다.
    /// Windows 기본 ssh-keyscan(9.5p1)은 Ubuntu 24.04 기본 KEX(sntrup761)에서 실패하므로 이 경로를 사용한다.
    /// </summary>
    public static IReadOnlyList<HostKey> ParseSshVerbose(string output)
    {
        const string marker = "Server host key: ";
        var keys = new List<HostKey>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) continue;
            var parts = line[(i + marker.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && IsWellFormed(parts[1])) keys.Add(new HostKey(parts[0], parts[1]));
        }
        return keys;
    }

    public static string Compute(ReadOnlySpan<byte> keyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');

    public static bool IsWellFormed(string? fingerprint) =>
        fingerprint is not null
        && fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)
        && fingerprint.Length == "SHA256:".Length + 43
        && fingerprint.AsSpan(7).IndexOfAnyExcept(Base64Chars) < 0;

    private static readonly System.Buffers.SearchValues<char> Base64Chars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

    /// <summary>키 blob 앞부분의 string(type)이 줄의 키 형식과 같은지 확인한다(위조·잘림 방지).</summary>
    private static bool BlobTypeMatches(byte[] blob, string keyType)
    {
        if (blob.Length < 4) return false;
        var len = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        if (len <= 0 || len > blob.Length - 4) return false;
        return System.Text.Encoding.ASCII.GetString(blob, 4, len) == keyType;
    }
}
