using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using WslRemoteSetup.Agent;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Core.Ssh;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Probing;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Setup;

/// <summary>
/// `install` / `resume`: EXE 하나로 처음부터 끝까지 단계별로 진행한다(AI 없이).
/// 단계마다 실제 상태를 다시 확인해 이미 된 단계는 건너뛰므로, 중간에 멈추거나 재부팅한 뒤 다시 실행하면 이어서 진행된다.
/// 시스템을 바꾸는 단계는 영향을 설명하고 [Y/n]으로 승인받는다. 관리자 단계는 UAC 창에서 한 번 더 확인한다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallWizard
{
    private const int Total = 8;
    private static readonly string Wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static async Task<int> RunAsync()
    {
        if (WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("설치 마법사는 관리자 창이 아닌 일반 창에서 실행해 주세요(WSL은 사용자 계정별로 설치됩니다). 관리자 단계는 마법사가 따로 요청합니다.");
            return App.ExitUsage;
        }
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("질문에 답할 수 있는 창에서 실행해 주세요.");
            return App.ExitUserInputRequired;
        }

        var history = Ui.OpenHistory("설치 마법사");
        var runner = new CommandRunner([Wsl]);
        Ui.Title("WSL Remote Setup — 설치");
        Ui.Line("단계마다 무엇을 바꾸는지 보여 드리고, 확인을 받은 뒤 진행합니다. 중간에 멈춰도 다시 실행하면 이어서 진행합니다.");

        // 1. 질문
        Step(1, "설치 질문");
        var snapshot = await new WindowsProbe(runner).CollectAsync();
        var choices = SetupWizard.LoadChoices();
        if (choices is not null)
        {
            Ui.Line($"저장된 선택값: 포트 {choices.ListenPort}번 · 계정 {choices.OwnerAccount} · Linux 사용자 {choices.LinuxUser} · {(choices.SshAuth == SshAuthentication.PublicKeyOnly ? "키로만 로그인" : "비밀번호 또는 키")}");
            Console.Write($"  이 값으로 계속할까요? (n = 다시 묻기) {Confirmation.Prompt} ");
            if (Confirmation.Parse(Console.ReadLine()) == ConfirmAnswer.No) choices = null;
        }
        choices ??= await SetupWizard.AskAsync(snapshot);
        if (choices is null) return Stop(history, "질문 단계에서 중단");
        history.Detail("선택값", $"port={choices.ListenPort} owner={choices.OwnerAccount} linux={choices.LinuxUser} auth={choices.SshAuth} distro={choices.Distro}");
        Program_DhcpNotice(snapshot);

        // 2. WSL 기능
        Step(2, "WSL 설치 확인");
        var wslOk = snapshot.Wsl?.RuntimeInstalled == true && snapshot.Os is { VirtualMachinePlatform: FeatureState.Enabled, HypervisorPresent: true };
        if (wslOk) Done("WSL이 이미 설치되어 있습니다.");
        else
        {
            if (snapshot.Os?.PendingReboot == true && snapshot.Wsl?.RuntimeInstalled != true)
                Ui.Note("이 PC는 이미 재부팅을 기다리는 변경이 있습니다. 재부팅 후 다시 실행하는 것이 좋습니다.");
            Impact("WSL(Windows에서 Linux를 실행하는 기능)을 설치합니다.",
                ["Windows 기능 '가상 머신 플랫폼'과 WSL 프로그램이 설치됩니다(인터넷 필요).",
                 "설치 후 재부팅이 필요할 수 있습니다. 재부팅하면 원격 데스크톱 연결이 잠시 끊깁니다.",
                 "이 PC의 IP·원격 데스크톱 설정은 바꾸지 않습니다."]);
            if (!Confirm()) return Stop(history, "WSL 설치를 취소함");
            history.Begin("WSL 설치(관리자)");
            var r = Elevation.RunSelfElevated("internal-install-wsl");
            if (!r.Started) return Stop(history, r.Error!);
            history.Detail("종료코드", r.ExitCode.ToString(CultureInfo.InvariantCulture));
            snapshot = await new WindowsProbe(runner).CollectAsync();
            if (snapshot.Wsl?.RuntimeInstalled == true && snapshot.Os is { HypervisorPresent: true }) { history.Done("설치됨"); Done("WSL을 설치했습니다."); }
            else
            {
                history.Done("재부팅 필요");
                SaveProgress("재부팅 후 WSL 설치 확인");
                Console.WriteLine();
                Ui.Line("WSL 설치를 마치려면 재부팅이 필요합니다.");
                Ui.Line($"재부팅 후 {choices.OwnerAccount} 계정으로 로그인해서 이 프로그램을 다시 실행하면 이어서 진행합니다.");
                Console.Write("  지금 재부팅할까요? 저장하지 않은 작업이 있으면 n을 누르세요. [y/N] ");
                if (Confirmation.ParseDefaultNo(Console.ReadLine()) == ConfirmAnswer.Yes)
                {
                    history.Note("사용자가 재부팅을 선택함");
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), "/r /t 30 /c \"WslRemoteSetup: WSL 설치 마무리\"") { UseShellExecute = false })?.Dispose();
                    Ui.Line("30초 뒤 재부팅합니다.");
                }
                history.Finish("재부팅 대기");
                return App.ExitOk;
            }
        }

        // 3. 소유자 계정
        Step(3, "WSL을 쓸 계정 확인");
        if (!string.Equals(choices.OwnerAccount, Ui.CurrentUserName(), StringComparison.OrdinalIgnoreCase))
        {
            SaveProgress("소유자 계정 로그인 대기");
            Ui.Line($"WSL은 계정마다 따로 설치됩니다. {choices.OwnerAccount} 계정으로 로그인한 뒤 이 프로그램을 다시 실행해 주세요.");
            history.Finish($"소유자({choices.OwnerAccount}) 로그인 대기");
            return App.ExitOk;
        }
        Done($"{choices.OwnerAccount} 계정에서 진행합니다.");

        // 4. Ubuntu 등록
        Step(4, $"{choices.Distro} 설치");
        var distro = (snapshot.Wsl?.DistrosForCurrentUser ?? []).FirstOrDefault(d => string.Equals(d.Name, choices.Distro, StringComparison.OrdinalIgnoreCase));
        if (distro is { Version: 2 }) Done("이미 설치되어 있습니다.");
        else if (distro is not null) return Stop(history, $"{choices.Distro}가 WSL 1로 설치되어 있습니다. 변환은 데이터에 영향을 줄 수 있어 자동으로 하지 않습니다.");
        else
        {
            Impact($"{choices.Distro}(Linux)를 이 계정에 설치합니다.", ["인터넷에서 약 수백 MB를 내려받습니다.", "이 계정의 사용자 폴더 안에 설치됩니다."]);
            if (!Confirm()) return Stop(history, "Ubuntu 설치를 취소함");
            history.Begin("배포판 설치");
            var psi = new ProcessStartInfo(Wsl) { UseShellExecute = false };
            foreach (var a in new[] { "--install", "-d", choices.Distro, "--no-launch" }) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { p.WaitForExit(); history.Detail("종료코드", p.ExitCode.ToString(CultureInfo.InvariantCulture)); }
            snapshot = await new WindowsProbe(runner).CollectAsync();
            if ((snapshot.Wsl?.DistrosForCurrentUser ?? []).All(d => !string.Equals(d.Name, choices.Distro, StringComparison.OrdinalIgnoreCase)))
                return Stop(history, $"{choices.Distro} 설치를 확인하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 실행해 주세요.");
            history.Done("설치됨");
            Done("설치했습니다.");
        }

        // 5. Linux 사용자
        Step(5, $"Linux 사용자 {choices.LinuxUser}");
        history.Begin("Linux 사용자 준비");
        var (initExit, initOut) = LinuxScripts.RunCaptured(choices.Distro, "root", "init-user.sh", "--user", choices.LinuxUser);
        history.Detail("결과", initOut.Replace('\n', ' ').Trim());
        if (initExit != 0) return Stop(history, "Linux 사용자를 준비하지 못했습니다.");
        if (initOut.Contains("USER_CREATED", StringComparison.Ordinal)) Ui.Line($"{choices.LinuxUser} 사용자를 만들었습니다(관리자 명령 sudo 사용 가능).");
        if (initOut.Contains("PASSWORD_MISSING", StringComparison.Ordinal))
        {
            Console.WriteLine();
            Ui.Line($"{choices.LinuxUser}의 Linux 비밀번호를 정해 주세요. Windows 비밀번호와 따로입니다.");
            Ui.Line("입력하는 글자는 화면에 보이지 않습니다. 같은 비밀번호를 두 번 입력합니다.");
            var ok = false;
            for (var i = 0; i < 3 && !ok; i++)
                ok = LinuxScripts.RunInteractive(choices.Distro, "root", "/usr/bin/passwd", choices.LinuxUser) == 0;
            if (!ok) return Stop(history, "비밀번호를 설정하지 못했습니다.");
            history.Note("사용자가 Linux 비밀번호를 직접 설정함(기록하지 않음)");
        }
        history.Done("준비됨");
        Done("준비됐습니다.");

        // 6. SSH 서버 (+ 공개키)
        Step(6, "SSH 서버");
        var hasSsh = LinuxScripts.RunCaptured(choices.Distro, "root", "probe-ssh.sh").Output.Contains("ssh.socket enabled=enabled", StringComparison.Ordinal);
        string? keyLine = null;
        if (choices.SshAuth == SshAuthentication.PublicKeyOnly || AskOptional("접속하는 PC의 공개키(.pub 한 줄)를 지금 등록할까요?"))
        {
            keyLine = AskPublicKey();
            if (keyLine is null && choices.SshAuth == SshAuthentication.PublicKeyOnly)
                return Stop(history, "'키로만 로그인'은 공개키가 있어야 합니다. 키를 준비한 뒤 다시 실행하거나 로그인 방식을 바꿔 주세요.");
            if (keyLine is not null)
            {
                var (kExit, kOut) = LinuxScripts.RunWithInput(choices.Distro, choices.LinuxUser, "add-authorized-key.sh", keyLine, "--installation-id", InstallationId().ToString("D"));
                history.Detail("공개키 등록", kOut.Trim());
                Ui.Line(kExit == 0 ? "공개키를 등록했습니다." : "공개키를 등록하지 못했습니다.");
                if (kExit != 0 && choices.SshAuth == SshAuthentication.PublicKeyOnly) return Stop(history, "공개키 등록 실패");
            }
        }
        if (hasSsh) Done("SSH 서버가 이미 설치되어 있습니다.");
        else
        {
            Impact("WSL 안에 SSH 서버를 설치합니다.",
                ["Ubuntu 패키지 openssh-server를 설치합니다(인터넷 필요).",
                 $"root 로그인은 막고 {choices.LinuxUser}만 접속할 수 있게 합니다.",
                 choices.SshAuth == SshAuthentication.PublicKeyOnly ? "키로만 로그인할 수 있게 합니다." : "비밀번호 또는 키로 로그인할 수 있습니다.",
                 "아직 다른 PC에서는 접속할 수 없습니다(다음 단계에서 연결 통로를 엽니다)."]);
            if (!Confirm()) return Stop(history, "SSH 설치를 취소함");
            history.Begin("SSH 설치");
            var exit = LinuxScripts.RunVisible(choices.Distro, "root", "install-openssh.sh",
                "--user", choices.LinuxUser, "--installation-id", InstallationId().ToString("D"),
                "--password-auth", choices.SshAuth == SshAuthentication.PublicKeyOnly ? "no" : "yes");
            if (exit != 0) return Stop(history, "SSH 서버를 설치하지 못했습니다(위 화면의 [FAIL] 내용을 확인하세요).");
            history.Done("설치됨");
            Done("설치했습니다.");
        }

        // 7. 연결 통로·방화벽 (관리자)
        Step(7, $"외부 연결 통로 열기 ({choices.ListenPort}번)");
        var wslIp = await WindowsForwarding.ReadWslAddressAsync(runner, choices.Distro);
        if (wslIp is null) return Stop(history, "WSL 주소를 확인하지 못했습니다.");
        var hostKey = await ForwardCommand.ReadRecordedHostKeyAsync(runner, choices.Distro);
        if (!HostKeyFingerprint.IsWellFormed(hostKey)) return Stop(history, "WSL SSH 서버 확인 정보를 읽지 못했습니다.");
        history.Detail("WSL 주소·서버 키", $"{wslIp} {hostKey}");
        Impact("다른 PC가 SSH로 들어올 수 있게 연결 통로와 방화벽 허용 규칙을 만듭니다(관리자 권한).",
            [.. (choices.Services ?? []).Select(s => $"추가 서비스 {s.Name}: 외부 {s.ListenPort}번 → WSL {s.ConnectPort}번"),
             $"이 PC의 {choices.ListenPort}번으로 들어온 접속을 WSL SSH로 전달합니다.",
             "같은 공유기에 연결된 기기만 허용하고, '개인 네트워크'일 때만 동작합니다.",
             "관리자 창이 열리면 같은 내용을 한 번 더 확인합니다."]);
        if (!Confirm()) return Stop(history, "연결 통로 만들기를 취소함");
        history.Begin("연결 통로(관리자)");
        // 관리자 창에서 WSL이 꺼지지 않도록 잠시 유지한다.
        using (var hold = new Lifetime.WslLifetimeKeeper(choices.Distro))
        {
            hold.Start();
            string[] fargs = ["forward", "apply", "--wsl-ip", wslIp.ToString(), "--port", choices.ListenPort.ToString(CultureInfo.InvariantCulture), "--distro", choices.Distro, "--host-key", hostKey!, "--pause-at-end",
                .. (choices.Services ?? []).SelectMany(s => new[] { "--service", string.Create(CultureInfo.InvariantCulture, $"{s.Name}:{s.ListenPort}:{s.ConnectPort}") })];
            var fr = Elevation.RunSelfElevated(fargs);
            hold.Release();
            if (!fr.Started) return Stop(history, fr.Error!);
            history.Detail("종료코드", fr.ExitCode.ToString(CultureInfo.InvariantCulture));
            if (fr.ExitCode != App.ExitOk) return Stop(history, "연결 통로를 만들지 못했거나 취소되었습니다.");
        }
        history.Done("완료");
        Done("연결 통로를 열었습니다.");

        // 8. 자동 실행 (관리자)
        Step(8, "로그인하면 자동 실행");
        Impact("이 PC를 켜고 로그인하면 WSL과 연결을 자동으로 관리하게 합니다(관리자 권한).",
            ["백그라운드 연결 관리 프로그램을 등록합니다(컴퓨터를 켜면 자동 시작).",
             $"{choices.OwnerAccount}으로 로그인하면 WSL을 켜 두고 1분마다 연결을 점검합니다.",
             "프로그램을 C:\\Program Files\\WslRemoteSetup에 설치합니다."]);
        if (!Confirm()) return Stop(history, "자동 실행 설정을 취소함");
        history.Begin("자동 실행(관리자)");
        var ar = Elevation.RunSelfElevated("autostart", "install", "--distro", choices.Distro, "--pause-at-end");
        if (!ar.Started) return Stop(history, ar.Error!);
        if (ar.ExitCode != App.ExitOk) return Stop(history, "자동 실행을 설정하지 못했거나 취소되었습니다.");
        history.Done("완료");
        Done("설정했습니다.");

        SaveProgress("완료");
        history.Finish("설치 완료");
        Console.WriteLine();
        await Status.StatusCommand.RunAsync();
        Console.WriteLine();
        Ui.Line("다른 PC에서 접속하는 방법:");
        var routed = snapshot.Interfaces?.FirstOrDefault(i => i.IsUp && i.Gateways.Count > 0 && i.Ipv4.Count > 0);
        Ui.Line($"  ssh -p {choices.ListenPort} {choices.LinuxUser}@{(routed is null ? "<이 PC 주소>" : routed.Ipv4[0].Address)}");
        Contact.Show();
        return App.ExitOk;
    }

    private static void Program_DhcpNotice(EnvironmentSnapshot s) => App.DhcpNotice(s);

    private static void Step(int n, string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{n}/{Total}] {title}");
        Console.ResetColor();
        SaveProgress(title);
    }

    private static void Done(string text) => Ui.Line($"✔ {text}");

    private static void Impact(string what, IEnumerable<string> effects)
    {
        Ui.Line(what);
        foreach (var e in effects) Ui.Line($"  - {e}");
    }

    private static bool Confirm()
    {
        Console.Write($"  진행할까요? {Confirmation.Prompt} ");
        for (var i = 0; i < 3; i++)
        {
            switch (Confirmation.Parse(Console.ReadLine()))
            {
                case ConfirmAnswer.Yes: return true;
                case ConfirmAnswer.No: return false;
                default: Console.Write($"  Y 또는 n으로 입력해 주세요 {Confirmation.Prompt} "); break;
            }
        }
        return false;
    }

    private static bool AskOptional(string question)
    {
        Console.Write($"  {question} [y/N] ");
        return Confirmation.ParseDefaultNo(Console.ReadLine()) == ConfirmAnswer.Yes;
    }

    private static string? AskPublicKey()
    {
        Ui.Line("접속하는 PC에서 공개키 파일(예: id_ed25519.pub) 내용을 한 줄 그대로 붙여 넣으세요. (개인키는 절대 붙여 넣지 마세요)");
        for (var i = 0; i < 3; i++)
        {
            Console.Write("  공개키: ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line)) return null;
            if (line.Contains("PRIVATE KEY", StringComparison.Ordinal)) { Ui.Line("→ 개인키로 보입니다. .pub 파일의 내용을 붙여 넣어 주세요."); continue; }
            if (HostKeyFingerprint.ParseKeyscan("x " + line).Count == 1) return line;
            Ui.Line("→ 공개키 형식이 아닙니다(ssh-ed25519 AAAA… 형태).");
        }
        return null;
    }

    private static Guid InstallationId()
    {
        var state = new StateStore(ProtectedPaths.StateFile).Load().State;
        if (state is not null) return state.InstallationId;
        var path = Path.Combine(UserPaths.Root, "user-state", "installation-id.txt");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var g)) return g;
        g = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, g.ToString("D"));
        return g;
    }

    private static void SaveProgress(string stage) =>
        UserPaths.WriteJsonAtomic(Path.Combine(UserPaths.Root, "user-state", "install-progress.json"), new { stage, updatedAt = DateTimeOffset.Now });

    private static int Stop(OperationHistory history, string reason)
    {
        Console.WriteLine();
        Ui.Blocked(reason);
        Ui.Line("다시 실행하면 이미 끝난 단계는 건너뛰고 이어서 진행합니다.");
        history.Finish($"중단: {reason}");
        Ui.HistoryHint(history);
        Contact.Show();
        return App.ExitChecksFailed;
    }
}
