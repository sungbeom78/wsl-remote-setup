namespace WslRemoteSetup.Core.Execution;

/// <summary>로그·오류 메시지에 비밀값이 남지 않도록 가린다.</summary>
public static class SecretMasker
{
    public const string Mask = "****";

    public static string Apply(string text, IEnumerable<string>? secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets is null) return text;
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        return text;
    }
}
