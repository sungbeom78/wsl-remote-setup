using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WslRemoteSetup.Core.Validation;

public sealed record ValidationResult(bool IsValid, string? Error)
{
    public static readonly ValidationResult Ok = new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}

/// <summary>사용자 입력과 외부 조회값을 셸/명령 인수로 쓰기 전에 검증한다.</summary>
public static partial class InputValidator
{
    // Ubuntu adduser 기본 NAME_REGEX와 같은 계열: 소문자/숫자/underscore/hyphen, 32자 이하.
    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxUserRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DistroRegex();

    private static readonly SearchValues<char> WindowsAccountForbidden = SearchValues.Create("\"/\\[]:;|=,+*?<>@");

    private static readonly HashSet<string> ReservedLinuxUsers = new(StringComparer.Ordinal)
    {
        "root", "daemon", "bin", "sys", "sync", "nobody", "sshd",
    };

    public static ValidationResult LinuxUserName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ValidationResult.Fail("Linux 사용자 이름이 비어 있습니다.");
        if (!LinuxUserRegex().IsMatch(value))
            return ValidationResult.Fail("Linux 사용자 이름은 소문자·숫자·_·- 만 사용하고 소문자나 _로 시작해야 합니다(최대 32자).");
        if (ReservedLinuxUsers.Contains(value))
            return ValidationResult.Fail($"'{value}'은(는) 예약된 시스템 계정입니다.");
        return ValidationResult.Ok;
    }

    public static ValidationResult WindowsAccountName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ValidationResult.Fail("Windows 계정 이름이 비어 있습니다.");
        if (value.Length > 20) return ValidationResult.Fail("Windows 로컬 계정 이름은 20자 이하여야 합니다.");
        if (value.AsSpan().ContainsAny(WindowsAccountForbidden) || value.Any(char.IsControl))
            return ValidationResult.Fail("Windows 계정 이름에 사용할 수 없는 문자가 있습니다.");
        if (value.Trim(' ', '.').Length == 0 || value.EndsWith('.'))
            return ValidationResult.Fail("Windows 계정 이름은 공백·마침표로만 이루어지거나 마침표로 끝날 수 없습니다.");
        return ValidationResult.Ok;
    }

    public static ValidationResult DistroName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ValidationResult.Fail("배포판 이름이 비어 있습니다.");
        return DistroRegex().IsMatch(value)
            ? ValidationResult.Ok
            : ValidationResult.Fail("배포판 이름은 영문·숫자·.·_·- 만 사용할 수 있습니다.");
    }

    public static ValidationResult Port(int value) =>
        value is >= 1 and <= 65535 ? ValidationResult.Ok : ValidationResult.Fail("포트는 1~65535 범위여야 합니다.");

    public static ValidationResult Port(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? Port(port)
            : ValidationResult.Fail("포트는 숫자여야 합니다.");

    /// <summary>점 4개로 구분된 정규 IPv4만 허용한다. IPAddress.TryParse가 받는 "10.1" 같은 축약형은 거부한다.</summary>
    public static bool TryParseIPv4(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split('.');
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)) return false;
            if (part.Length > 1 && part[0] == '0') return false; // 8진수 오해 방지
            if (int.Parse(part, CultureInfo.InvariantCulture) > 255) return false;
        }
        if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        address = parsed;
        return true;
    }

    public static ValidationResult IPv4(string? value) =>
        TryParseIPv4(value, out _) ? ValidationResult.Ok : ValidationResult.Fail("올바른 IPv4 주소가 아닙니다.");
}
