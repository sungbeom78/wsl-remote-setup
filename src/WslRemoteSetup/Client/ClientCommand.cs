using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using WslRemoteSetup.Core.Client;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Client;

/// <summary>
/// `client &lt;파일&gt;`(ADR 0009): 노트북에서 연결 정보 파일 하나로 SSH 키·서버 확인·접속 이름·원격 데스크톱 바로가기를 준비한다.
/// 관리자 권한이 필요 없고, 바꾸는 것은 현재 사용자의 ~/.ssh 파일과 바탕화면 바로가기뿐이다(바꾸기 전 [Y/n]·백업).
/// 비밀번호·passphrase는 ssh/ssh-keygen이 직접 묻는다. 이 도구는 그 값을 읽거나 저장하지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ClientCommand
{
    private const int Steps = 6;
    private static readonly string OpenSshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
    private static readonly string Ssh = Path.Combine(OpenSshDir, "ssh.exe");
    private static readonly string SshKeygen = Path.Combine(OpenSshDir, "ssh-keygen.exe");

    public static async Task<int> RunAsync(string? file)
    {
        Ui.Title("노트북 접속 준비 (이 노트북의 사용자 설정만 바꿉니다)");
        if (file is null)
        {
            Ui.Blocked("서버에서 만든 연결 정보 파일(*.wrs.json)을 지정해 주세요. 예: WslRemoteSetup.exe client G2.wrs.json");
            return App.ExitUsage;
        }
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("확인 입력을 받을 수 없는 환경이라 진행하지 않습니다.");
            return App.ExitUserInputRequired;
        }

        var history = Ui.OpenHistory("노트북 접속 준비");
        history.Begin("연결 정보 파일 확인");
        ClientProfile p;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) throw new FileNotFoundException("파일이 없습니다.", file);
            if (info.Length > 16 * 1024) throw new InvalidDataException("연결 정보 파일이 너무 큽니다.");
            p = ClientProfile.Parse(await File.ReadAllTextAsync(file));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Ui.Blocked($"연결 정보 파일을 쓸 수 없습니다: {ex.Message}");
            history.Fail($"{ex.GetType().Name}: {ex.Message}");
            history.Finish("중단");
            Ui.HistoryHint(history);
            return App.ExitChecksFailed;
        }
        history.Detail("파일", Path.GetFullPath(file));
        history.Detail("서버", $"{p.Name} {p.Host}:{p.SshPort} user={p.User} rdp={p.RdpPort} key={p.HostKeyFingerprint}");

        Ui.Line($"접속할 PC: {p.Name} ({p.Host})");
        Ui.Line($"Linux 접속: 포트 {p.SshPort}, 사용자 {p.User}");
        if (p.RdpPort is { } rp) Ui.Line($"원격 데스크톱: 포트 {rp}, 계정 {p.RdpUser}");

        if (!File.Exists(Ssh) || !File.Exists(SshKeygen))
        {
            Ui.Blocked("이 노트북에 Windows 기본 SSH 프로그램(OpenSSH 클라이언트)이 없습니다. " +
                       "설정 > 시스템 > 선택적 기능에서 'OpenSSH 클라이언트'를 추가한 뒤 다시 실행해 주세요.");
            history.Fail($"OpenSSH 없음: {OpenSshDir}");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(home, ".ssh");
        var identity = Path.Combine(sshDir, "id_ed25519");
        var configPath = Path.Combine(sshDir, "config");
        var knownHostsPath = Path.Combine(sshDir, "known_hosts");
        var warnings = 0;

        // 1. 내 SSH 키
        Step(1, "이 노트북의 SSH 키", history);
        if (File.Exists(identity) && File.Exists(identity + ".pub"))
        {
            Ui.Item(1, "이 노트북의 SSH 키", "변경 없음", "이미 있는 키를 그대로 씁니다.");
            history.Skip(identity);
        }
        else if (File.Exists(identity) || File.Exists(identity + ".pub"))
        {
            Ui.Blocked("SSH 키 파일 한 쌍 중 하나만 있습니다. 기존 파일을 덮어쓰지 않으려고 멈춥니다. ~/.ssh 폴더를 확인해 주세요.");
            history.Fail("id_ed25519 / .pub 짝 불일치");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }
        else
        {
            Ui.Item(1, "이 노트북의 SSH 키", "만들기", "비밀번호 대신 쓰는 열쇠 파일을 만듭니다. 열쇠 암호(passphrase)는 Enter로 비워 두거나 직접 정할 수 있습니다.");
            if (!ConfirmPrompt.Ask(out var c1)) { history.Finish("취소"); return c1; }
            Directory.CreateDirectory(sshDir);
            var comment = $"{Environment.UserName}@{Environment.MachineName}".Replace(' ', '-');
            var code = RunInteractive(SshKeygen, ["-t", "ed25519", "-f", identity, "-C", comment]);
            history.Detail("ssh-keygen exit", code.ToString(CultureInfo.InvariantCulture));
            if (code != 0 || !File.Exists(identity + ".pub"))
            {
                Ui.Blocked("키를 만들지 못했습니다.");
                history.Fail("ssh-keygen 실패");
                history.Finish("중단");
                return App.ExitChecksFailed;
            }
            history.Done(identity);
        }

        // 2. 서버 확인 정보(known_hosts)
        Step(2, "서버 확인 정보 등록", history);
        var kh = SshClientFiles.UpsertKnownHosts(ReadOrNull(knownHostsPath), p);
        if (kh.Conflict is not null) return Conflict(2, "서버 확인 정보 등록", kh.Conflict, history);
        if (!kh.Changed) { Ui.Item(2, "서버 확인 정보 등록", "변경 없음"); history.Skip("이미 등록됨"); }
        else
        {
            Ui.Item(2, "서버 확인 정보 등록", "추가", "처음 접속할 때 '이 서버를 믿겠습니까?' 질문 없이 진짜 서버인지 자동으로 확인하게 합니다.");
            if (!ConfirmPrompt.Ask(out var c2)) { history.Finish("취소"); return c2; }
            WriteWithBackup(knownHostsPath, kh.NewText!, history);
        }

        // 3. 접속 이름(~/.ssh/config)
        Step(3, $"접속 이름 '{p.Name}' 등록", history);
        var cfg = SshClientFiles.UpsertConfig(ReadOrNull(configPath), p, identity.Replace('\\', '/'));
        if (cfg.Conflict is not null) return Conflict(3, $"접속 이름 '{p.Name}' 등록", cfg.Conflict, history);
        if (!cfg.Changed) { Ui.Item(3, $"접속 이름 '{p.Name}' 등록", "변경 없음"); history.Skip("이미 같은 내용"); }
        else
        {
            Ui.Item(3, $"접속 이름 '{p.Name}' 등록", "추가", $"이후 'ssh {p.Name}' 한 줄로 접속할 수 있게 합니다. 기존 설정은 그대로 두고 백업합니다.");
            if (!ConfirmPrompt.Ask(out var c3)) { history.Finish("취소"); return c3; }
            WriteWithBackup(configPath, cfg.NewText!, history);
        }

        // 4. 서버에 내 키 등록
        Step(4, "서버에 이 노트북 키 등록", history);
        if (await KeyLoginWorksAsync(p.Name, history))
        {
            Ui.Item(4, "서버에 이 노트북 키 등록", "변경 없음", "이미 키로 접속됩니다.");
            history.Skip("키 로그인 이미 가능");
        }
        else
        {
            string command;
            try { command = SshClientFiles.AuthorizedKeyCommand(await File.ReadAllTextAsync(identity + ".pub")); }
            catch (FormatException ex)
            {
                Ui.Blocked(ex.Message);
                history.Fail(ex.Message);
                history.Finish("중단");
                return App.ExitChecksFailed;
            }
            Ui.Item(4, "서버에 이 노트북 키 등록", "등록", $"서버의 Linux 비밀번호({p.User})로 한 번 접속해 이 노트북 키를 등록합니다. 비밀번호는 SSH 프로그램이 직접 묻고, 이 도구는 보지 않습니다.");
            if (!ConfirmPrompt.Ask(out var c4)) { history.Finish("취소"); return c4; }
            var code = RunInteractive(Ssh, ["-o", "PubkeyAuthentication=no", "-o", "PreferredAuthentications=keyboard-interactive,password", p.Name, command]);
            history.Detail("ssh 등록 exit", code.ToString(CultureInfo.InvariantCulture));
            if (code != 0)
            {
                Ui.Item(4, "서버에 이 노트북 키 등록", "확인 필요",
                    "등록하지 못했습니다. 비밀번호가 틀렸거나 서버가 키 전용 로그인일 수 있습니다. " +
                    $"키 전용이면 이 노트북의 {identity}.pub 내용을 서버 관리자에게 전달해 등록해 달라고 하세요.");
                history.Fail("공개키 등록 실패");
                warnings++;
            }
            else history.Done("공개키 등록(또는 이미 있음)");
        }

        // 5. 원격 데스크톱 바로가기
        Step(5, "원격 데스크톱 바로가기", history);
        if (p.RdpFile() is not { } rdp)
        {
            Ui.Item(5, "원격 데스크톱 바로가기", "해당 없음", "서버에서 원격 데스크톱이 꺼져 있습니다.");
            history.Skip("RDP 정보 없음");
        }
        else
        {
            var rdpPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{p.Name} 원격 데스크톱.rdp");
            if (File.Exists(rdpPath) && await File.ReadAllTextAsync(rdpPath) == rdp)
            {
                Ui.Item(5, "원격 데스크톱 바로가기", "변경 없음");
                history.Skip(rdpPath);
            }
            else
            {
                Ui.Item(5, "원격 데스크톱 바로가기", "만들기", "바탕화면에 두 번 클릭하면 큰 화면으로 서버에 접속하는 아이콘을 만듭니다(비밀번호는 저장하지 않음).");
                if (!ConfirmPrompt.Ask(out var c5)) { history.Finish("취소"); return c5; }
                if (File.Exists(rdpPath)) File.Copy(rdpPath, BackupName(rdpPath), overwrite: false);
                await File.WriteAllTextAsync(rdpPath, rdp, new UnicodeEncoding(false, true));
                history.Done(rdpPath);
            }
        }

        // 6. 확인
        Step(6, "키로 접속 확인", history);
        var ok = await KeyLoginWorksAsync(p.Name, history);
        if (ok) Ui.Item(6, "키로 접속 확인", "정상", $"이제 'ssh {p.Name}' 로 비밀번호 없이 접속됩니다.");
        else
        {
            Ui.Item(6, "키로 접속 확인", "확인 필요", $"자동 확인에 실패했습니다. 키에 암호(passphrase)를 걸었다면 정상일 수 있습니다. 'ssh {p.Name}' 를 직접 실행해 보세요.");
            warnings++;
        }
        history.Finish(warnings == 0 ? "완료" : $"완료(확인 필요 {warnings}건)");
        Console.WriteLine();
        Ui.Line(warnings == 0 ? "노트북 준비를 마쳤습니다." : "노트북 준비를 마쳤지만 확인이 필요한 항목이 있습니다.");
        Ui.HistoryHint(history);
        Contact.Show();
        return warnings == 0 ? App.ExitOk : App.ExitChecksFailed;
    }

    private static void Step(int n, string title, OperationHistory h)
    {
        Console.WriteLine();
        Console.WriteLine($"  [{n}/{Steps}] {title}");
        h.Begin(title);
    }

    private static int Conflict(int n, string title, string message, OperationHistory h)
    {
        Ui.Item(n, title, "진행 불가", message);
        h.Fail(message);
        h.Finish("중단(기존 설정 보존)");
        Ui.HistoryHint(h);
        return App.ExitChecksFailed;
    }

    private static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static string BackupName(string path) =>
        $"{path}.wrs-backup-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

    /// <summary>원본을 백업한 뒤 임시 파일→교체로 저장한다(LF, BOM 없음 — OpenSSH가 읽는 형식).</summary>
    private static void WriteWithBackup(string path, string text, OperationHistory h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".wrs-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            var backup = BackupName(path);
            File.Replace(tmp, path, backup);
            h.Detail("백업", backup);
        }
        else File.Move(tmp, path);
        h.Done(path);
    }

    private static async Task<bool> KeyLoginWorksAsync(string name, OperationHistory h)
    {
        var runner = new CommandRunner([Ssh]);
        var r = await runner.RunAsync(new CommandSpec(Ssh,
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "PasswordAuthentication=no", name, "echo", "WRS_OK"])
            { Timeout = TimeSpan.FromSeconds(30) });
        h.Detail("키 로그인 시험", $"exit={r.ExitCode} out={r.StandardOutput.Trim()} err={r.StandardError.Trim()}");
        return r.ExitedZero && r.StandardOutput.Contains("WRS_OK", StringComparison.Ordinal);
    }

    /// <summary>콘솔을 그대로 넘겨 실행한다(passphrase·비밀번호를 사용자가 직접 입력). 출력·입력은 이 도구가 읽지 않는다.</summary>
    private static int RunInteractive(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Reporting.ScreenRecorder.Note($"({Path.GetFileName(exe)} 화면 — 사용자가 직접 입력하며 기록하지 않음)");
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} 실행 실패");
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
