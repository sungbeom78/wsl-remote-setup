using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using WslRemoteSetup.Core.Client;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Probing;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Client;

/// <summary>
/// `export-client`(ADR 0009): 노트북 접속 도우미가 읽을 연결 정보 파일을 바탕화면에 만든다.
/// 공개 정보만 담고 시스템 설정은 바꾸지 않는다. WSL 소유자 계정에서 실행한다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExportClientCommand
{
    private const string HostKeyPath = "/etc/ssh/ssh_host_ed25519_key.pub";

    public static async Task<int> RunAsync(string? name, string? linuxUser, string? outputDir)
    {
        Ui.Title("노트북 연결 정보 만들기 (설정은 바꾸지 않습니다)");
        var history = Ui.OpenHistory("연결 정보 내보내기");
        history.Begin("설정 기록 읽기");

        var state = new StateStore(ProtectedPaths.StateFile).Load().State;
        var proxy = state?.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy);
        if (state is null || proxy is null)
        {
            Ui.Blocked("이 PC에 외부 연결 설정이 아직 없습니다. 먼저 설치를 마쳐 주세요.");
            history.Fail("상태 파일 또는 portproxy 기록 없음");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }
        var sep = proxy.Key.LastIndexOf(':');
        var host = proxy.Key[..sep];
        var port = int.Parse(proxy.Key[(sep + 1)..], CultureInfo.InvariantCulture);
        var distro = state.DistroName ?? "Ubuntu-24.04";
        history.Detail("연결 통로", $"{proxy.Key} → {proxy.Value}");

        history.Begin("WSL에서 서버 키·사용자 읽기");
        var runner = new CommandRunner([WindowsForwarding.Wsl]);
        var key = await runner.RunAsync(new CommandSpec(WindowsForwarding.Wsl, ["--distribution", distro, "--exec", "/bin/cat", HostKeyPath]) { Timeout = TimeSpan.FromSeconds(60) });
        var hostKeyLine = key.ExitedZero ? FirstLine(key.StandardOutput) : null;
        if (hostKeyLine is null)
        {
            Ui.Blocked("Linux에서 SSH 서버 정보를 읽지 못했습니다. 이 PC의 WSL 소유 계정에서 실행했는지 확인해 주세요.");
            history.Fail($"host key 읽기 실패 exit={key.ExitCode} err={key.StandardError.Trim()}");
            history.Finish("중단");
            Ui.HistoryHint(history);
            return App.ExitChecksFailed;
        }
        // 공개키는 "type base64 comment" — comment(호스트 이름)는 버린다.
        var keyParts = hostKeyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hostKey = keyParts.Length >= 2 ? $"{keyParts[0]} {keyParts[1]}" : hostKeyLine;
        var parsed = Core.Ssh.HostKeyFingerprint.ParseKeyscan("h " + hostKey);
        if (parsed.Count != 1)
        {
            Ui.Blocked("SSH 서버 정보 형식이 올바르지 않습니다.");
            history.Fail($"host key 형식 오류: {hostKey}");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }
        var fingerprint = parsed[0].Fingerprint;
        history.Detail("서버 키 지문", fingerprint);
        if (state.SshHostKey is { } recorded && recorded != fingerprint)
        {
            Ui.Blocked("SSH 서버 키가 설치할 때 기록한 것과 다릅니다. 서버를 다시 설치했다면 연결 설정을 다시 적용해 주세요.");
            history.Fail($"지문 불일치: 기록 {recorded}, 현재 {fingerprint}");
            history.Finish("중단");
            Ui.HistoryHint(history);
            return App.ExitChecksFailed;
        }

        if (linuxUser is null)
        {
            var who = await runner.RunAsync(new CommandSpec(WindowsForwarding.Wsl, ["--distribution", distro, "--exec", "/usr/bin/id", "-un"]) { Timeout = TimeSpan.FromSeconds(30) });
            linuxUser = who.ExitedZero ? FirstLine(who.StandardOutput) : null;
        }
        if (linuxUser is null || !InputValidator.LinuxUserName(linuxUser).IsValid)
        {
            Ui.Blocked("Linux 사용자 이름을 알 수 없습니다. --linux-user 로 지정해 주세요.");
            history.Fail($"Linux 사용자 확인 실패: {linuxUser}");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }

        history.Begin("원격 데스크톱 확인");
        int? rdpPort = null;
        try
        {
            var rdp = WindowsProbe.ProbeRdp();
            history.Detail("RDP", $"enabled={rdp.Enabled} port={rdp.Port} nla={rdp.NlaRequired}");
            if (rdp.Enabled) rdpPort = rdp.Port;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            history.Note($"RDP 확인 실패: {ex.Message}");
        }

        var profile = new ClientProfile
        {
            Name = name ?? DefaultName(),
            Host = host,
            SshPort = port,
            User = linuxUser,
            RdpPort = rdpPort,
            RdpUser = rdpPort is null ? null : Ui.CurrentUserName(),
            HostKey = hostKey,
            HostKeyFingerprint = fingerprint,
            CreatedAt = DateTimeOffset.Now,
        };
        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            Ui.Blocked(string.Join(" ", errors));
            history.Fail(string.Join(" / ", errors));
            history.Finish("중단");
            return App.ExitUsage;
        }

        history.Begin("파일 저장");
        var dir = outputDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(dir, $"{profile.Name}.wrs.json");
        Ui.Line($"이름: {profile.Name}");
        Ui.Line($"이 PC 주소: {profile.Host}  ·  Linux 접속 포트: {profile.SshPort}  ·  Linux 사용자: {profile.User}");
        Ui.Line(rdpPort is null ? "원격 데스크톱: 꺼져 있음(파일에 넣지 않음)" : $"원격 데스크톱 포트: {rdpPort}  ·  계정: {profile.RdpUser}");
        Ui.Line($"저장 위치: {path}");
        Ui.Note("비밀번호·개인키는 넣지 않습니다. 이 파일을 노트북으로 옮긴 뒤 노트북에서 'client' 모드로 여세요.");
        if (File.Exists(path))
        {
            Ui.Note("같은 이름의 파일이 이미 있어 새 내용으로 바꿉니다.");
            if (!ConfirmPrompt.Ask(out var code)) { history.Finish("취소"); return code; }
        }
        await File.WriteAllTextAsync(path, profile.ToJson(), new UTF8Encoding(false));
        history.Done(path);
        history.Finish("완료");
        Console.WriteLine();
        Ui.Line("연결 정보 파일을 만들었습니다.");
        Ui.HistoryHint(history);
        return App.ExitOk;
    }

    private static string? FirstLine(string s)
    {
        var line = s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static string DefaultName()
    {
        var sb = new StringBuilder();
        foreach (var c in Environment.MachineName)
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
        var s = sb.ToString().TrimStart('-', '.', '_');
        return s.Length == 0 ? "server" : s[..Math.Min(32, s.Length)];
    }
}
