using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Wsl;

/// <summary>현지화 문구에 의존하지 않는 wsl.exe 출력 해석.</summary>
public static class WslOutputParser
{
    /// <summary>
    /// `wsl --list --running --quiet` 출력에서 배포판 이름만 추린다(한 줄에 이름 하나).
    /// 이름 규칙에 맞지 않는 줄(안내 문구 등)은 버린다. 종료코드 판정은 호출자가 한다.
    /// </summary>
    public static IReadOnlyList<string> ParseQuietList(string output) =>
        output.Replace("\0", "", StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('﻿'))
            .Where(l => InputValidator.DistroName(l).IsValid)
            .ToList();
}
