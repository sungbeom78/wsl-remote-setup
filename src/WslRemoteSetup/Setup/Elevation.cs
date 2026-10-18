using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace WslRemoteSetup.Setup;

/// <summary>
/// 관리자 단계 실행: 같은 EXE를 UAC로 새 창에서 실행한다. 새 창에서도 계획을 보여 주고 [Y/n]을 다시 확인한다.
/// 인수는 고정된 명령만 넘기며 사용자 입력 문자열을 그대로 이어 붙이지 않는다(검증된 값만).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Elevation
{
    public sealed record Result(bool Started, int ExitCode, string? Error);

    public static Result RunSelfElevated(params string[] args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        foreach (var a in args)
            if (a.Any(c => c is '"' or '\0' or '\r' or '\n')) throw new ArgumentException("허용되지 않는 인수입니다.", nameof(args));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)),
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return new Result(true, p.ExitCode, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new Result(false, -1, "관리자 권한 요청(UAC)이 취소되었습니다.");
        }
        catch (Win32Exception ex)
        {
            return new Result(false, -1, ex.Message);
        }
    }

    /// <summary>관리자 창 끝에서 결과를 읽을 수 있게 Enter를 기다린다(새 창이 바로 닫히지 않게).</summary>
    public static void PauseIfOwnWindow()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.Write("  Enter를 누르면 이 창을 닫고 원래 창으로 돌아갑니다...");
        Console.ReadLine();
    }
}
