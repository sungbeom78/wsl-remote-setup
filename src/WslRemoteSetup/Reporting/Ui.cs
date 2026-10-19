using System.Runtime.Versioning;
using System.Security.Principal;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Reporting;

/// <summary>
/// 화면 표시 도우미(ADR 0006). 화면에는 쉬운 말만, 기술 값은 작업 기록에 남긴다.
/// 관리자 작업 기록은 보호 경로, 일반 작업 기록은 사용자 폴더에 둔다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Ui
{
    public static OperationHistory OpenHistory(string operation)
    {
        var path = WindowsForwarding.IsElevated()
            ? Path.Combine(ProtectedPaths.LogDirectory, "history.log")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslRemoteSetup", "logs", "history.log");
        var history = new OperationHistory(path, operation, CurrentUserName());
        history.Detail("프로그램 빌드", App.BuildId);
        return history;
    }

    public static string CurrentUserName()
    {
        using var id = WindowsIdentity.GetCurrent();
        var i = id.Name.IndexOf('\\');
        return i >= 0 ? id.Name[(i + 1)..] : id.Name;
    }

    public static void Title(string text)
    {
        Console.WriteLine();
        Console.WriteLine(text);
        Console.WriteLine(new string('-', Math.Min(60, text.Length * 2)));
    }

    public static void Line(string text = "") => Console.WriteLine(string.IsNullOrEmpty(text) ? "" : "  " + text);

    /// <summary>"  1. 제목 ........ [상태]" + 설명 줄.</summary>
    public static void Item(int n, string title, string state, string? explain = null)
    {
        Console.Write($"  {n}. {title} ");
        Write($"[{state}]", state switch
        {
            "변경 없음" or "정상" or "일치" => ConsoleColor.Green,
            "확인 필요" or "주의" => ConsoleColor.Yellow,
            "진행 불가" or "불일치" or "실패" => ConsoleColor.Red,
            _ => ConsoleColor.Cyan,
        });
        Console.WriteLine();
        if (explain is not null) Console.WriteLine($"     {explain}");
    }

    public static void Note(string text)
    {
        Console.Write("  ");
        Write("※", ConsoleColor.Yellow);
        Console.WriteLine($" {text}");
    }

    public static void Blocked(string text)
    {
        Console.Write("  ");
        Write("진행할 수 없음:", ConsoleColor.Red);
        Console.WriteLine($" {text}");
    }

    public static void Progress(int n, int total, string title, Action action, OperationHistory history)
    {
        Console.Write($"  [{n}/{total}] {title} ... ");
        history.Begin(title);
        try
        {
            action();
            Write("완료", ConsoleColor.Green);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Write("실패", ConsoleColor.Red);
            Console.WriteLine();
            history.Fail($"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public static void HistoryHint(OperationHistory h)
    {
        Console.WriteLine();
        Console.WriteLine($"  자세한 진행 기록: {h.FilePath}");
    }

    private static void Write(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }
}
