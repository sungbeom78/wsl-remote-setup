using System.Diagnostics;
using System.Runtime.Versioning;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Reporting;

namespace WslRemoteSetup.Setup;

/// <summary>설치 마법사가 UAC 창으로 실행하는 내부 명령(도움말에 표시하지 않음).</summary>
[SupportedOSPlatform("windows")]
internal static class InternalCommands
{
    /// <summary>WSL 런타임 설치(관리자). 배포판은 설치하지 않는다(소유자 계정에서 따로 설치).</summary>
    public static int InstallWsl()
    {
        if (!WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("관리자 권한이 필요합니다.");
            return App.ExitUsage;
        }
        var history = Ui.OpenHistory("WSL 설치(관리자)");
        Ui.Title("WSL 설치");
        Ui.Line("Windows 기능과 WSL 프로그램을 설치합니다. 몇 분 걸릴 수 있습니다.");
        history.Begin("wsl --install --no-distribution");
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe")) { UseShellExecute = false };
        foreach (var a in new[] { "--install", "--no-distribution" }) psi.ArgumentList.Add(a);
        int exit;
        using (var p = Process.Start(psi)!) { p.WaitForExit(); exit = p.ExitCode; }
        // 종료코드 1이 "일부 적용 + 재부팅 필요"일 수 있어(G2 B3 실측) 판정은 마법사가 실제 상태를 다시 확인해 한다.
        history.Detail("종료코드", exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        history.Finish("완료(판정은 설치 마법사가 다시 확인)");
        Elevation.PauseIfOwnWindow();
        return exit;
    }
}
