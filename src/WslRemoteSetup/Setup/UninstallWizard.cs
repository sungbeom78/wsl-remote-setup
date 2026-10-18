using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using WslRemoteSetup.Agent;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Lifetime;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Setup;

/// <summary>
/// `uninstall`: 설정한 것을 단계별로 되돌린다. 단계마다 영향을 알리고 승인을 받으며, 건너뛸 수 있다.
/// 1) 외부 연결 닫기(핵심) 2) 자동 실행 중지 3) WSL 안 SSH 설정 되돌리기 4) (선택) WSL 배포판 삭제 5) 설치 이력 보고.
/// 설치된 프로그램(WSL 런타임, openssh-server, 이 도구 실행 파일)은 지우지 않고 목록과 지우는 방법만 알려 준다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UninstallWizard
{
    private const int Total = 5;
    private static readonly string Wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static async Task<int> RunAsync()
    {
        if (WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("되돌리기 마법사는 일반 창에서 실행해 주세요(WSL은 계정별로 관리됩니다). 관리자 단계는 마법사가 따로 요청합니다.");
            return App.ExitUsage;
        }
        if (Console.IsInputRedirected) { Console.Error.WriteLine("질문에 답할 수 있는 창에서 실행해 주세요."); return App.ExitUserInputRequired; }

        var history = Ui.OpenHistory("되돌리기 마법사");
        var state = new StateStore(ProtectedPaths.StateFile).Load().State;
        var choices = SetupWizard.LoadChoices();
        var distro = state?.DistroName ?? choices?.Distro ?? "Ubuntu-24.04";
        var linuxUser = choices?.LinuxUser ?? "linux_admin";
        var done = new List<string>();
        var skipped = new List<string>();

        Ui.Title("WSL Remote Setup — 되돌리기");
        Ui.Line("단계마다 무엇이 바뀌는지 알려 드리고 확인을 받습니다. 원하지 않는 단계는 n으로 건너뛸 수 있습니다.");
        Ui.Line("설치된 프로그램은 지우지 않고, 마지막에 무엇이 설치되어 있는지 알려 드립니다.");

        // 1. 외부 연결 닫기
        Step(1, "외부 연결 닫기");
        var hasForward = state?.ResourceOwnership.Any(r => r.Kind is ResourceKinds.PortProxy or ResourceKinds.FirewallRule) == true;
        if (!hasForward) Ui.Line("✔ 이 도구가 연 외부 연결이 없습니다.");
        else if (Ask("다른 PC에서 들어오는 연결 통로와 방화벽 허용 규칙을 지웁니다.",
                     ["지우는 즉시 다른 PC에서 SSH로 접속할 수 없습니다(지금 연결된 세션도 끊길 수 있습니다).",
                      "이 PC 안에서 WSL을 쓰는 데는 영향이 없습니다.",
                      "원격 데스크톱(RDP)과 다른 방화벽 규칙은 그대로 둡니다.",
                      "관리자 창에서 한 번 더 확인합니다."], defaultYes: true))
        {
            var r = Elevation.RunSelfElevated("forward", "remove", "--pause-at-end");
            Record(history, "외부 연결 닫기", r, done, skipped);
        }
        else skipped.Add("외부 연결 닫기");

        // 2. 자동 실행 중지
        Step(2, "자동 실행 중지");
        state = new StateStore(ProtectedPaths.StateFile).Load().State;
        var hasAuto = state?.ResourceOwnership.Any(r => r.Kind is "scheduled-task" or "service" or "policy") == true;
        if (!hasAuto) Ui.Line("✔ 이 도구가 등록한 자동 실행이 없습니다.");
        else if (Ask("로그인 시 자동 실행과 백그라운드 연결 관리 프로그램을 지웁니다.",
                     ["WSL이 더 이상 자동으로 켜지지 않고, 연결 점검도 멈춥니다.",
                      "설치된 프로그램 파일은 지우지 않습니다.",
                      "관리자 창에서 한 번 더 확인합니다."], defaultYes: true))
        {
            var r = Elevation.RunSelfElevated("autostart", "remove", "--pause-at-end");
            Record(history, "자동 실행 중지", r, done, skipped);
        }
        else skipped.Add("자동 실행 중지");

        // 3. WSL 안 SSH 설정
        Step(3, "WSL 안 SSH 설정 되돌리기");
        var runner = new CommandRunner([Wsl]);
        var distroExists = await DistroExistsAsync(runner, distro);
        if (!distroExists) Ui.Line($"✔ {distro}가 없어 되돌릴 SSH 설정이 없습니다.");
        else if (Ask($"{distro} 안의 SSH 설정을 되돌립니다.",
                     ["이 도구가 만든 SSH 설정 파일과, 이 도구로 등록한 공개키만 지웁니다(직접 넣은 키는 남김).",
                      "SSH 서버를 끕니다. 이 PC 안에서도 SSH로 접속할 수 없게 됩니다.",
                      "SSH 프로그램(openssh-server)은 지우지 않습니다."], defaultYes: true))
        {
            var id = state?.InstallationId ?? Guid.Empty;
            if (id == Guid.Empty) { Ui.Line("설치 기록을 찾지 못해 건너뜁니다."); skipped.Add("SSH 설정 되돌리기"); }
            else
            {
                history.Begin("SSH 설정 되돌리기");
                var (exit, output) = LinuxScripts.RunCaptured(distro, "root", "revert-ssh.sh", "--user", linuxUser, "--installation-id", id.ToString("D"));
                history.Detail("결과", output.Replace('\n', ' ').Trim());
                if (exit == 0) { history.Done("완료"); done.Add("WSL 안 SSH 설정 되돌리기"); Ui.Line("✔ 되돌렸습니다."); }
                else { history.Fail("실패"); skipped.Add("SSH 설정 되돌리기(실패)"); Ui.Blocked("SSH 설정을 되돌리지 못했습니다. 기록 파일을 확인해 주세요."); }
            }
        }
        else skipped.Add("SSH 설정 되돌리기");

        // 4. (선택) WSL 배포판 삭제
        Step(4, $"(선택) {distro} 삭제");
        if (!await DistroExistsAsync(runner, distro)) Ui.Line($"✔ {distro}가 이미 없습니다.");
        else
        {
            Ui.Line($"{distro}를 이 계정에서 삭제할 수 있습니다.");
            Ui.Line("  - 안에 있는 모든 파일·프로그램·설정이 영구히 지워지며 되살릴 수 없습니다.");
            Ui.Line("  - 필요한 파일은 먼저 다른 곳에 백업하세요.");
            Ui.Line("  - WSL 프로그램 자체는 지우지 않습니다.");
            Console.Write("  삭제할까요? [y/N] ");
            if (Confirmation.ParseDefaultNo(Console.ReadLine()) == ConfirmAnswer.Yes)
            {
                Console.Write($"  정말 삭제하려면 배포판 이름({distro})을 그대로 입력하세요: ");
                var typed = Console.ReadLine()?.Trim();
                if (string.Equals(typed, distro, StringComparison.Ordinal))
                {
                    history.Begin($"{distro} 삭제");
                    // 자동 실행이 남아 있으면 다시 켜질 수 있어, 자동 실행 중지 단계를 건너뛴 경우 먼저 멈춤 상태로 둔다.
                    if (skipped.Contains("자동 실행 중지")) FilePauseSource.Set(true);
                    var psi = new ProcessStartInfo(Wsl) { UseShellExecute = false };
                    foreach (var a in new[] { "--unregister", distro }) psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi)!;
                    p.WaitForExit();
                    history.Detail("종료코드", p.ExitCode.ToString(CultureInfo.InvariantCulture));
                    if (p.ExitCode == 0) { history.Done("삭제함"); done.Add($"{distro} 삭제"); Ui.Line("✔ 삭제했습니다."); }
                    else { history.Fail("실패"); Ui.Blocked("삭제하지 못했습니다."); skipped.Add($"{distro} 삭제(실패)"); }
                }
                else { Ui.Line("이름이 달라 삭제하지 않았습니다."); skipped.Add($"{distro} 삭제"); }
            }
            else { Ui.Line("삭제하지 않습니다."); skipped.Add($"{distro} 삭제"); }
        }

        // (선택) 이 도구가 고정한 주소 해제
        if (new StateStore(ProtectedPaths.StateFile).Load().State?.ResourceOwnership.Any(r => r.Kind == "static-ip") == true)
        {
            Console.WriteLine();
            Ui.Line("이 도구로 이 PC 주소를 고정해 두었습니다. 공유기 자동 할당으로 되돌릴 수 있습니다.");
            Ui.Line("  - 주소가 바뀌면 원격 연결이 끊기고, 다른 PC의 접속 주소를 바꿔야 할 수 있습니다.");
            Console.Write("  자동 할당으로 되돌릴까요? [y/N] ");
            if (Confirmation.ParseDefaultNo(Console.ReadLine()) == ConfirmAnswer.Yes)
                Record(history, "주소 고정 해제", Elevation.RunSelfElevated("network", "revert", "--pause-at-end"), done, skipped);
            else skipped.Add("주소 고정 해제");
        }

        // 5. 설치 이력
        Step(5, "설치 이력");
        Ui.Line("이 도구가 설치했거나 사용한 프로그램(지우지 않음)과 직접 지우는 방법:");
        Ui.Line("  - WSL 프로그램: 설정 > 앱 > 설치된 앱 > 'Windows Subsystem for Linux' 제거");
        Ui.Line($"  - {distro}: 남아 있다면 'wsl --unregister {distro}'(파일 모두 삭제) 또는 이 마법사 4단계");
        Ui.Line("  - SSH 프로그램(openssh-server): WSL 안에서 'sudo apt remove openssh-server'");
        Ui.Line($"  - 이 도구: {ProtectedPaths.InstallDirectory} 폴더 (관리자 권한으로 삭제)");
        Ui.Line($"  - 작업 기록: {ProtectedPaths.DataRoot}, {UserPaths.Root}");
        Console.WriteLine();
        Ui.Line($"이번에 되돌린 것: {(done.Count == 0 ? "없음" : string.Join(", ", done))}");
        if (skipped.Count > 0) Ui.Line($"건너뛴 것: {string.Join(", ", skipped)}");
        history.Note($"완료: {string.Join(", ", done)} / 건너뜀: {string.Join(", ", skipped)}");
        history.Finish("완료");
        Ui.HistoryHint(history);
        Contact.Show();
        return App.ExitOk;
    }

    private static void Step(int n, string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{n}/{Total}] {title}");
        Console.ResetColor();
    }

    private static bool Ask(string what, IEnumerable<string> effects, bool defaultYes)
    {
        Ui.Line(what);
        foreach (var e in effects) Ui.Line($"  - {e}");
        Console.Write($"  진행할까요? {(defaultYes ? Confirmation.Prompt : "[y/N]")} (n = 이 단계 건너뛰기) ");
        var input = Console.ReadLine();
        return defaultYes ? Confirmation.Parse(input) == ConfirmAnswer.Yes : Confirmation.ParseDefaultNo(input) == ConfirmAnswer.Yes;
    }

    private static void Record(OperationHistory history, string title, Elevation.Result r, List<string> done, List<string> skipped)
    {
        history.Begin(title);
        if (!r.Started) { history.Fail(r.Error!); Ui.Blocked(r.Error!); skipped.Add($"{title}(취소)"); return; }
        history.Detail("종료코드", r.ExitCode.ToString(CultureInfo.InvariantCulture));
        if (r.ExitCode == App.ExitOk) { history.Done("완료"); done.Add(title); Ui.Line("✔ 완료했습니다."); }
        else { history.Fail("완료하지 못함"); skipped.Add($"{title}(미완료)"); Ui.Blocked("완료하지 못했거나 취소되었습니다. 다시 실행하면 이어서 진행할 수 있습니다."); }
    }

    private static async Task<bool> DistroExistsAsync(CommandRunner runner, string distro)
    {
        var r = await runner.RunAsync(new CommandSpec(Wsl, ["--list", "--quiet"]) { Timeout = TimeSpan.FromSeconds(30) });
        return r.ExitedZero && Core.Wsl.WslOutputParser.ParseQuietList(r.StandardOutput).Contains(distro, StringComparer.OrdinalIgnoreCase);
    }
}
