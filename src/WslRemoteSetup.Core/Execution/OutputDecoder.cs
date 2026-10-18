using System.Text;

namespace WslRemoteSetup.Core.Execution;

/// <summary>
/// 외부 명령 출력 디코딩. inbox wsl.exe는 WSL_UTF8을 무시하고 UTF-16LE로 출력하는 경우가 있어
/// BOM 또는 NUL 바이트 분포로 UTF-16LE를 판별하고, 그 외에는 UTF-8로 해석한다.
/// </summary>
public static class OutputDecoder
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "";
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        return LooksLikeUtf16Le(bytes) ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    internal static bool LooksLikeUtf16Le(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2) return false;
        int pairs = bytes.Length / 2, oddZero = 0, evenZero = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0) evenZero++;
            if (bytes[i + 1] == 0) oddZero++;
        }
        // ASCII 위주 UTF-16LE는 홀수 위치가 대부분 0이다. 한글 UTF-16LE는 0이 적으므로
        // 짝수 위치에 0이 거의 없고 홀수 위치에 0이 일정 비율 이상이면 UTF-16LE로 본다.
        return oddZero * 4 >= pairs && evenZero * 10 < pairs;
    }
}
