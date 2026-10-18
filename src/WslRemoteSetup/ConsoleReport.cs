using WslRemoteSetup.Core;
using WslRemoteSetup.Core.Planning;

namespace WslRemoteSetup;

internal static class ConsoleReport
{
    public static void PrintChecks(IEnumerable<CheckResult> results)
    {
        foreach (var r in results)
        {
            var color = r.Status switch
            {
                CheckStatus.Pass => ConsoleColor.Green,
                CheckStatus.Fail => ConsoleColor.Red,
                CheckStatus.Warn => ConsoleColor.Yellow,
                CheckStatus.Pending => ConsoleColor.Cyan,
                _ => ConsoleColor.Gray,
            };
            Console.Write($"  {PadDisplay(r.Title, 30)} ");
            WriteColored(PadDisplay(CheckResult.Label(r.Status), 15), color);
            Console.WriteLine(r.Detail);
        }
    }

    public static void PrintPlan(InstallationPlan plan)
    {
        var n = 0;
        foreach (var s in plan.Steps)
        {
            n++;
            var (label, color) = s.Change switch
            {
                ChangeNeed.NotRequired => ("변경 없음", ConsoleColor.Green),
                ChangeNeed.Required => ("변경 필요", ConsoleColor.Cyan),
                _ => ("확인 필요", ConsoleColor.Yellow),
            };
            Console.Write($"  {n,2}. {PadDisplay(s.Title, 40)} ");
            WriteColored($"[{label}]", color);
            Console.WriteLine();
            Console.WriteLine($"      현재: {s.Current}");
            Console.WriteLine($"      목표: {s.Target}");
            var flags = new List<string> { $"실행 주체 {ExecutorLabel(s.Executor)}" };
            if (s.RequiresApproval && s.Change != ChangeNeed.NotRequired) flags.Add("승인 필요");
            if (s.MayRequireReboot) flags.Add("재부팅 가능");
            if (s.MayDisconnect) flags.Add("접속 단절 가능");
            flags.Add($"되돌리기 {RevertLabel(s.Revert)}");
            if (s.DependsOn.Count > 0) flags.Add($"선행 {string.Join(",", s.DependsOn)}");
            Console.WriteLine($"      {string.Join(" · ", flags)}");
            if (s.NotImplementedNote is not null) Console.WriteLine($"      비고: {s.NotImplementedNote}");
        }
        Console.WriteLine();
        Console.WriteLine($"  계획 해시: {plan.ComputeHash()[..16]}…  (승인 후 계획이 바뀌면 다시 승인 필요)");
    }

    private static string ExecutorLabel(StepExecutor e) => e switch
    {
        StepExecutor.CurrentUser => "현재 사용자",
        StepExecutor.Administrator => "관리자(UAC)",
        StepExecutor.OwnerSession => "소유자 일반 세션",
        StepExecutor.Person => "사용자 직접 입력",
        _ => e.ToString(),
    };

    private static string RevertLabel(Reversibility r) => r switch
    {
        Reversibility.ToolCanRevert => "도구가 제거 가능",
        Reversibility.ManualOnly => "수동",
        Reversibility.NotReversible => "자동 제거 안 함(데이터 보존)",
        _ => "해당 없음",
    };

    /// <summary>한글 등 전각 문자를 2칸으로 계산해 콘솔 열을 맞춘다.</summary>
    private static string PadDisplay(string text, int width)
    {
        var w = text.Sum(c => IsWide(c) ? 2 : 1);
        return w >= width ? text : text + new string(' ', width - w);
    }

    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or >= '豈' and <= '﫿' or >= '＀' and <= '｠';

    private static void WriteColored(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }
}
