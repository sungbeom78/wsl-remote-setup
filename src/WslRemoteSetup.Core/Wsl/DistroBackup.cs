using System.Globalization;

namespace WslRemoteSetup.Core.Wsl;

/// <summary>
/// 배포판 삭제 전 백업(`wsl --export`) 경로 결정·검사(순수 함수). 기존 파일은 덮어쓰지 않고,
/// 남은 공간이 예상 크기보다 넉넉할 때만 진행한다. 백업이 확인되지 않으면 삭제하지 않는다.
/// </summary>
public static class DistroBackup
{
    /// <summary>예상 크기에 더하는 여유(tar 머리말·작업 중 임시 공간).</summary>
    public const double Margin = 1.1;

    public static string DefaultPath(string documentsDir, string distro, DateTimeOffset now) =>
        Path.Combine(documentsDir, "WSL 백업", $"{distro}-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.tar");

    /// <summary>입력 경로를 정리한다: 빈 입력은 기본값, 따옴표 제거, 폴더만 주면 기본 파일 이름을 붙이고, 확장자가 없으면 .tar.</summary>
    public static string Resolve(string? input, string defaultPath, Func<string, bool> isExistingDirectory)
    {
        var s = (input ?? "").Trim().Trim('"').Trim();
        if (s.Length == 0) return defaultPath;
        if (isExistingDirectory(s) || s.EndsWith('\\') || s.EndsWith('/'))
            return Path.Combine(s, Path.GetFileName(defaultPath));
        return Path.HasExtension(s) ? s : s + ".tar";
    }

    /// <summary>문제가 있으면 쉬운 말의 이유, 없으면 null.</summary>
    public static string? Check(string path, bool fileExists, long? freeBytes, long? estimatedBytes)
    {
        if (!Path.IsPathFullyQualified(path)) return "C:\\폴더\\파일.tar 처럼 드라이브부터 전체 경로를 입력해 주세요.";
        if (path.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase)) return "WSL 안의 경로에는 백업할 수 없습니다(삭제와 함께 사라짐). Windows 폴더를 입력해 주세요.";
        if (!path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return "백업 파일 이름은 .tar로 끝나야 합니다.";
        if (fileExists) return "같은 이름의 파일이 이미 있습니다. 덮어쓰지 않으니 다른 이름을 입력해 주세요.";
        if (freeBytes is { } free && estimatedBytes is { } need && free < need * Margin)
            return $"남은 공간이 부족합니다(필요 약 {Size(need)}, 남은 공간 {Size(free)}). 다른 드라이브를 입력해 주세요.";
        return null;
    }

    /// <summary>`df -B1 --output=used /` 출력에서 사용 바이트를 읽는다.</summary>
    public static long? ParseDfUsed(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0}GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0}MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0}KB"),
    };

    public static string RestoreCommand(string distro, string backupPath) =>
        $"wsl --import {distro} \"%LOCALAPPDATA%\\WSL\\{distro}\" \"{backupPath}\"";
}
