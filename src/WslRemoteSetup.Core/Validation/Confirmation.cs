namespace WslRemoteSetup.Core.Validation;

public enum ConfirmAnswer { Yes, No, Invalid }

/// <summary>
/// 콘솔 확인 입력 해석. ASCII만 사용해 콘솔 코드페이지(CP949/UTF-8) 차이로 입력이 깨지지 않게 한다.
/// 기본값(빈 입력)은 Yes — 사용자 결정(ADR 0004).
/// </summary>
public static class Confirmation
{
    public const string Prompt = "[Y/n]";

    public static ConfirmAnswer Parse(string? input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return ConfirmAnswer.Yes;
        return t.ToUpperInvariant() switch
        {
            "Y" or "YES" => ConfirmAnswer.Yes,
            "N" or "NO" => ConfirmAnswer.No,
            _ => ConfirmAnswer.Invalid,
        };
    }
}
