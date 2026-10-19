using System.Reflection;
using System.Text;
using System.Text.Json;
using WslRemoteSetup;
using WslRemoteSetup.Core;
using WslRemoteSetup.Core.Diagnostics;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Planning;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Probing;

Console.OutputEncoding = Encoding.UTF8;
args = WslRemoteSetup.Reporting.ScreenRecorder.Start(args);
var exitCode = await App.RunAsync(args);
WslRemoteSetup.Reporting.ScreenRecorder.Stop(exitCode);
return exitCode;

internal static class App
{
    public const int ExitOk = 0;
    public const int ExitChecksFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitUserInputRequired = 3;
    public const int ExitError = 4;
    public const int ExitCanceled = 5;

    /// <summary>빌드 식별값(버전+커밋). 화면에 보이지 않고 작업 기록에만 남긴다.</summary>
    internal static string BuildId =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>화면용 버전(예: 0.1.0). 커밋 값은 빼고 보여 준다(ADR 0006).</summary>
    internal static string Version => BuildId.Split('+')[0];

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("이 도구는 Windows에서만 실행됩니다.");
            return ExitError;
        }

        try
        {
            if (args.Length == 0) return await InteractiveAsync();

            var command = args[0].ToLowerInvariant();
            var sub = command is "forward" or "autostart" or "network" or "power" && args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1].ToLowerInvariant() : null;
            if (command == "client")
            {
                // client <파일>: 파일 경로는 대소문자를 바꾸지 않는다.
                var file = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
                var copts = Options.Parse(args.Skip(file is null ? 1 : 2).ToArray());
                if (copts.Error is not null) { Console.Error.WriteLine(copts.Error); return ExitUsage; }
                return await WithPause(copts, () => WslRemoteSetup.Client.ClientCommand.RunAsync(file));
            }
            var options = Options.Parse(args.Skip(sub is null ? 1 : 2).ToArray());
            if (options.Error is not null)
            {
                Console.Error.WriteLine(options.Error);
                return ExitUsage;
            }
            return command switch
            {
                "doctor" => await DoctorAsync(options),
                "plan" => await PlanAsync(options),
                "status" => await WslRemoteSetup.Status.StatusCommand.RunAsync(),
                "poc-lifetime" => await PocLifetimeAsync(options),
                "forward" => await WithPause(options, () => WslRemoteSetup.Forwarding.ForwardCommand.RunAsync(new(
                    sub ?? "", options.InstallationId, options.Listen, options.Port ?? DesiredConfiguration.DefaultListenPort,
                    options.WslIp, options.HostKey, options.Distro ?? DesiredConfiguration.DefaultDistro, options.Services, options.Name))),
                "help" or "--help" or "-h" or "/?" => Help(),
                "version" or "--version" => PrintVersion(),
                "agent" => options.InstallationId is { } aid
                    ? await WslRemoteSetup.Agent.AgentCommand.RunAsync(aid, options.Distro ?? DesiredConfiguration.DefaultDistro)
                    : Usage("agent --installation-id <guid> [--distro <이름>]"),
                "service" => await WslRemoteSetup.Service.ServiceCommand.RunAsync(),
                "autostart" => await WithPause(options, () => WslRemoteSetup.Service.AutostartCommand.RunAsync(new(sub ?? "", options.InstallationId, options.HostKey, options.Distro ?? DesiredConfiguration.DefaultDistro, options.PreLogin))),
                "network" => await WithPause(options, () => WslRemoteSetup.Network.NetworkCommand.RunAsync(sub ?? "", options.Ip)),
                "export-client" => await WithPause(options, () => WslRemoteSetup.Client.ExportClientCommand.RunAsync(options.Name, options.LinuxUser, options.OutDir)),
                "power" => await WithPause(options, () => WslRemoteSetup.Power.PowerCommand.RunAsync(sub ?? "")),
                "pause" => WslRemoteSetup.Agent.AgentCommand.SetPaused(true),
                "resume-monitoring" => WslRemoteSetup.Agent.AgentCommand.SetPaused(false),
                "install" or "resume" => await WslRemoteSetup.Setup.InstallWizard.RunAsync(),
                "internal-install-wsl" => WslRemoteSetup.Setup.InternalCommands.InstallWsl(),
                "uninstall" => await WslRemoteSetup.Setup.UninstallWizard.RunAsync(),
                "repair" => NotYet(command),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            // 화면에는 쉬운 말만, 오류 종류·위치는 작업 기록에 남긴다(ADR 0006).
            Console.Error.WriteLine($"예상하지 못한 문제로 멈췄습니다: {ex.Message}");
            try
            {
                var h = WslRemoteSetup.Reporting.Ui.OpenHistory("오류");
                h.Fail(ex.ToString());
                h.Finish("중단");
                Console.Error.WriteLine($"자세한 내용: {h.FilePath}");
            }
            catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException) { }
            return ExitError;
        }
    }

    private static async Task<int> InteractiveAsync()
    {
        Console.WriteLine($"WSL Remote Setup {Version}");
        Console.WriteLine();
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("질문에 답할 수 있는 창이 아니라 메뉴를 열 수 없습니다. EXE를 더블클릭해 실행하거나 'status' 같은 명령을 사용하세요.");
            return ExitUserInputRequired;
        }

        while (true)
        {
            Console.WriteLine("1. 새로 설치 / 이어서 설치");
            Console.WriteLine("2. 현재 상태 확인");
            Console.WriteLine("3. 연결 복구 (설치 단계를 다시 확인하고 빠진 것만 고침)");
            Console.WriteLine("4. 설정 변경 (포트·계정 등을 다시 묻고 적용)");
            Console.WriteLine("5. 자동 연결 관리 잠시 멈춤 / 다시 켜기");
            Console.WriteLine("6. 되돌리기 (외부 연결 닫기부터 단계별로)");
            Console.WriteLine("0. 종료");
            Console.WriteLine($"   {WslRemoteSetup.Reporting.Contact.Line}");
            Console.WriteLine();
            Console.Write("선택 [2]: ");
            var choice = (Console.ReadLine() ?? "0").Trim();
            if (choice == "") choice = "2";
            Console.WriteLine();
            switch (choice)
            {
                case "0":
                    return ExitOk;
                case "1":
                    await WslRemoteSetup.Setup.InstallWizard.RunAsync();
                    break;
                case "2":
                    await WslRemoteSetup.Status.StatusCommand.RunAsync();
                    break;
                case "3":
                    await WslRemoteSetup.Setup.InstallWizard.RunAsync();
                    break;
                case "4":
                    WslRemoteSetup.Setup.SetupWizard.ForgetChoices();
                    await WslRemoteSetup.Setup.InstallWizard.RunAsync();
                    break;
                case "5":
                {
                    var paused = new WslRemoteSetup.Agent.FilePauseSource().IsPaused();
                    Console.Write(paused ? "  지금 멈춰 있습니다. 다시 켤까요? [Y/n] " : "  자동 연결 관리를 잠시 멈출까요? WSL이 꺼지고 SSH 접속이 끊깁니다. [y/N] ");
                    var input = Console.ReadLine();
                    var yes = paused ? Confirmation.Parse(input) != ConfirmAnswer.No : Confirmation.ParseDefaultNo(input) == ConfirmAnswer.Yes;
                    if (yes) WslRemoteSetup.Agent.AgentCommand.SetPaused(!paused);
                    break;
                }
                case "6":
                    await WslRemoteSetup.Setup.UninstallWizard.RunAsync();
                    break;
                default:
                    Console.WriteLine("잘못된 선택입니다.");
                    break;
            }
            Console.WriteLine();
            Console.Write("Enter를 누르면 메뉴로 돌아갑니다...");
            Console.ReadLine();
            Console.WriteLine();
        }
    }

    private static async Task<EnvironmentSnapshot> CollectAsync()
    {
        Console.WriteLine("진단 중(읽기 전용)...");
        var probe = new WindowsProbe(CommandRunner.ForWindowsSystem());
        return await probe.CollectAsync();
    }

    private static async Task<int> DoctorAsync(Options o)
    {
        var desired = o.ToDesired();
        var invalid = desired.Validate();
        if (invalid.Count > 0) { invalid.ToList().ForEach(Console.Error.WriteLine); return ExitUsage; }

        var snapshot = await CollectAsync();
        var results = DoctorEvaluator.Evaluate(snapshot, desired);
        var history = WslRemoteSetup.Reporting.Ui.OpenHistory("상태 진단(읽기 전용)");
        history.Begin("진단 결과");
        if (snapshot.Identity is { } who) history.Detail("실행 계정", $"{who.UserName} ({who.Sid}), 승격={who.IsElevated}");
        foreach (var a in snapshot.Accounts ?? []) history.Detail($"계정 {a.Name}", $"{a.Sid}, 사용={a.Enabled}, 관리자={a.IsAdministrator}, 원격데스크톱={a.IsRemoteDesktopUser}");
        foreach (var r in results) history.Detail(r.Id, $"{CheckResult.Label(r.Status)} {r.Detail}");
        history.Done($"FAIL {results.Count(r => r.Status == CheckStatus.Fail)}, WARN {results.Count(r => r.Status == CheckStatus.Warn)}");
        history.Finish("완료");
        Console.WriteLine();
        Console.WriteLine($"진단 시각 {snapshot.CollectedAt:yyyy-MM-dd HH:mm:ss}");
        ConsoleReport.PrintChecks(results);

        if (o.JsonPath is not null)
        {
            var json = JsonSerializer.Serialize(new { snapshot, results }, StateStore.JsonOptions);
            File.WriteAllText(o.JsonPath, json, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine($"JSON 저장: {Path.GetFullPath(o.JsonPath)}  (계정 SID·내부 IP 포함, 공유 전 확인)");
        }
        WslRemoteSetup.Reporting.Ui.HistoryHint(history);
        return results.Any(r => r.Status == CheckStatus.Fail) ? ExitChecksFailed : ExitOk;
    }

    private static async Task<int> PlanAsync(Options o, DesiredConfiguration? given = null, EnvironmentSnapshot? snap = null)
    {
        var desired = given ?? o.ToDesired();
        var invalid = desired.Validate();
        if (invalid.Count > 0) { invalid.ToList().ForEach(Console.Error.WriteLine); return ExitUsage; }

        var snapshot = snap ?? await CollectAsync();
        var plan = Planner.Create(snapshot, desired, DateTimeOffset.Now);
        Console.WriteLine();
        Console.WriteLine($"설치 계획 — 계정 {desired.OwnerAccountName ?? "(현재 사용자)"}, WSL {desired.DistroName}, Linux 사용자 {desired.LinuxUser}, 외부 포트 {desired.ListenPort}번, 로그인 {(desired.SshAuth == SshAuthentication.PublicKeyOnly ? "키로만" : "비밀번호 또는 키")}");
        Console.WriteLine("이 명령은 시스템을 변경하지 않습니다.");
        DhcpNotice(snapshot);
        Console.WriteLine();
        ConsoleReport.PrintPlan(plan);
        return ExitOk;
    }

    /// <summary>이 PC 주소가 자동 할당(DHCP)이면 바뀔 수 있음을 알린다(ADR 0007 이전의 기본 동작).</summary>
    internal static void DhcpNotice(EnvironmentSnapshot s)
    {
        var routed = s.Interfaces?.Where(i => i.IsUp && i.Gateways.Count > 0 && i.Ipv4.Count > 0).ToList();
        if (routed is not { Count: 1 } || routed[0].DhcpEnabled != true) return;
        Console.WriteLine();
        WslRemoteSetup.Reporting.Ui.Note($"이 PC 주소({routed[0].Ipv4[0].Address})는 공유기가 자동으로 정해 주는 주소라 바뀔 수 있습니다.");
        WslRemoteSetup.Reporting.Ui.Note("주소가 바뀌면 다른 PC에서 접속이 끊깁니다. 공유기 설정에서 이 PC에 주소를 고정 배정(DHCP 예약)하는 것을 권장합니다.");
    }

    /// <summary>개발용 M3 실기 POC. 배포판을 기동한다. 사용자 승인 후에만 실행한다(help에 표시하지 않음).</summary>
    private static async Task<int> PocLifetimeAsync(Options o)
    {
        var desired = o.ToDesired();
        var invalid = desired.Validate();
        if (invalid.Count > 0) { invalid.ToList().ForEach(Console.Error.WriteLine); return ExitUsage; }

        Console.WriteLine($"M3 POC — WSL 수명 실험: {desired.DistroName} (배포판을 기동합니다. terminate/shutdown은 하지 않습니다)");
        var poc = new WslRemoteSetup.Lifetime.LifetimePoc(desired.DistroName, TimeSpan.FromSeconds(o.HoldSeconds ?? 120), TimeSpan.FromSeconds(180));
        await poc.RunAsync(CancellationToken.None);
        Console.WriteLine();
        ConsoleReport.PrintChecks(poc.Results);
        if (o.JsonPath is not null)
        {
            File.WriteAllText(o.JsonPath, JsonSerializer.Serialize(new { poc.Timeline, poc.Results }, StateStore.JsonOptions), new UTF8Encoding(false));
            Console.WriteLine($"JSON 저장: {Path.GetFullPath(o.JsonPath)}");
        }
        return poc.Results.Any(r => r.Status == CheckStatus.Fail) ? ExitChecksFailed : ExitOk;
    }

    private static int Help()
    {
        Console.WriteLine($"""
            WSL Remote Setup {Version}

            사용법:
              WslRemoteSetup.exe                 대화형 메뉴
              WslRemoteSetup.exe install         처음부터 끝까지 단계별 설치(일반 창에서 실행, 중간에 멈추면 다시 실행해 이어 하기)
              WslRemoteSetup.exe uninstall       단계별 되돌리기(외부 연결 닫기 → 자동 실행 중지 → SSH 설정 → 선택: WSL 삭제)
              WslRemoteSetup.exe doctor [옵션]   비파괴 진단
              WslRemoteSetup.exe plan [옵션]     현재 상태 기반 설치 계획(변경 없음)
              WslRemoteSetup.exe status          서비스·자동 실행·SSH 접속 상태를 한눈에(읽기 전용)
              WslRemoteSetup.exe forward plan|apply|remove  SSH 연결 통로·방화벽 허용 규칙(apply/remove는 관리자 창)
              WslRemoteSetup.exe forward add-service --service web:8080:80   추가 포트 열기(관리자 창)
              WslRemoteSetup.exe forward remove-service --name web           추가 포트 닫기(관리자 창)
              WslRemoteSetup.exe autostart plan|install|remove  로그인 시 자동 실행·백그라운드 연결 관리(install/remove는 관리자 창)
              WslRemoteSetup.exe autostart prelogin-on|prelogin-off  (선택) 로그인하지 않아도 켜지면 접속 허용·해제(관리자 창, 기본 꺼짐)
              WslRemoteSetup.exe network plan|apply|revert [--ip 주소]  (고급) 이 PC 주소 고정·해제(apply/revert는 관리자 창, 5분 안에 확인 없으면 자동 원복)
              WslRemoteSetup.exe pause | resume-monitoring  자동 복구 일시중지/재개(소유자 계정)
              WslRemoteSetup.exe power [keep-awake|restore]  절전 점검 · 전원 연결 시 절전 안 함 · 되돌리기
              WslRemoteSetup.exe export-client [--name G2] [--out 폴더]  노트북용 연결 정보 파일 만들기(서버, 소유자 계정)
              WslRemoteSetup.exe client <파일.wrs.json>   노트북에서 접속 준비(키·서버 확인·접속 이름·원격 데스크톱 바로가기)
              WslRemoteSetup.exe version

            옵션:
              --owner <Windows 계정>    WSL 배포판 소유 계정 (기본: 현재 사용자)
              --distro <이름>           배포판 (기본: {DesiredConfiguration.DefaultDistro})
              --linux-user <이름>       Linux 사용자 (기본: {DesiredConfiguration.DefaultLinuxUser})
              --port <번호>             Windows SSH 수신 포트 (기본: {DesiredConfiguration.DefaultListenPort})
              --ssh-auth password|key-only  SSH 로그인 방식 (기본: password = 비밀번호+공개키)
              --json <파일>             doctor 결과를 JSON으로 저장

            repair는 이후 버전에서 제공됩니다(지금은 install을 다시 실행하면 같은 역할).

            {WslRemoteSetup.Reporting.Contact.Line}
            """);
        return ExitOk;
    }

    private static int PrintVersion() { Console.WriteLine(Version); return ExitOk; }

    /// <summary>설치 마법사가 연 관리자 창이면 결과를 읽을 수 있게 끝에서 Enter를 기다린다.</summary>
    private static async Task<int> WithPause(Options o, Func<Task<int>> run)
    {
        var code = await run();
        if (o.PauseAtEnd) WslRemoteSetup.Setup.Elevation.PauseIfOwnWindow();
        return code;
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"사용: {usage}");
        return ExitUsage;
    }

    private static int NotYet(string command)
    {
        Console.Error.WriteLine($"'{command}'은(는) 아직 구현되지 않았습니다. 현재는 doctor/plan/status만 제공합니다.");
        return ExitUsage;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"알 수 없는 명령: {command}. 'help'를 확인하세요.");
        return ExitUsage;
    }
}

internal sealed record Options
{
    public static readonly Options Empty = new();
    public string? Owner { get; init; }
    public string? Distro { get; init; }
    public string? LinuxUser { get; init; }
    public int? Port { get; init; }
    public string? JsonPath { get; init; }
    public int? HoldSeconds { get; init; }
    public Guid? InstallationId { get; init; }
    public string? Listen { get; init; }
    public string? WslIp { get; init; }
    public string? HostKey { get; init; }
    public bool PauseAtEnd { get; init; }
    public IReadOnlyList<WslRemoteSetup.Core.Model.ServiceForward> Services { get; init; } = [];
    public string? Name { get; init; }
    public string? OutDir { get; init; }
    public bool? PreLogin { get; init; }
    public string? Ip { get; init; }
    public SshAuthentication? SshAuth { get; init; }
    public string? Error { get; init; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            var key = args[i];
            if (args[i] == "--pause-at-end") { o = o with { PauseAtEnd = true }; continue; }
            var value = Next();
            if (value is null) return o with { Error = $"{key} 옵션에 값이 필요합니다." };
            switch (key)
            {
                case "--owner": o = o with { Owner = value }; break;
                case "--distro": o = o with { Distro = value }; break;
                case "--linux-user": o = o with { LinuxUser = value }; break;
                case "--port":
                    if (!int.TryParse(value, out var p)) return o with { Error = "--port는 숫자여야 합니다." };
                    o = o with { Port = p };
                    break;
                case "--json": o = o with { JsonPath = value }; break;
                case "--installation-id":
                    if (!Guid.TryParseExact(value, "D", out var gid)) return o with { Error = "--installation-id 형식 오류" };
                    o = o with { InstallationId = gid };
                    break;
                case "--listen":
                    if (!InputValidator.TryParseIPv4(value, out _)) return o with { Error = "--listen은 IPv4여야 합니다." };
                    o = o with { Listen = value };
                    break;
                case "--wsl-ip":
                    if (!InputValidator.TryParseIPv4(value, out _)) return o with { Error = "--wsl-ip는 IPv4여야 합니다." };
                    o = o with { WslIp = value };
                    break;
                case "--ssh-auth":
                    SshAuthentication? mode = value switch { "password" => SshAuthentication.PasswordAndPublicKey, "key-only" => SshAuthentication.PublicKeyOnly, _ => null };
                    if (mode is null) return o with { Error = "--ssh-auth는 password 또는 key-only입니다." };
                    o = o with { SshAuth = mode };
                    break;
                case "--service":
                    if (WslRemoteSetup.Forwarding.ServiceForwarding.Parse(value) is not { } svc) return o with { Error = "--service는 이름:외부포트:WSL포트 형식입니다(예: web:8080:80)." };
                    o = o with { Services = [.. o.Services, svc] };
                    break;
                case "--ip":
                    if (!InputValidator.TryParseIPv4(value, out _)) return o with { Error = "--ip는 IPv4여야 합니다." };
                    o = o with { Ip = value };
                    break;
                case "--name":
                    o = o with { Name = value.Trim().ToLowerInvariant() };
                    break;
                case "--host-key":
                    if (!WslRemoteSetup.Core.Ssh.HostKeyFingerprint.IsWellFormed(value)) return o with { Error = "--host-key는 SHA256:… 형식이어야 합니다." };
                    o = o with { HostKey = value };
                    break;
                case "--prelogin":
                    if (value is not ("on" or "off")) return o with { Error = "--prelogin은 on 또는 off여야 합니다." };
                    o = o with { PreLogin = value == "on" };
                    break;
                case "--out":
                    if (!Directory.Exists(value)) return o with { Error = "--out 폴더가 없습니다." };
                    o = o with { OutDir = value };
                    break;
                case "--hold":
                    if (!int.TryParse(value, out var h) || h is < 10 or > 3600) return o with { Error = "--hold는 10~3600초여야 합니다." };
                    o = o with { HoldSeconds = h };
                    break;
                default: return o with { Error = $"알 수 없는 옵션: {key}" };
            }
        }
        return o;
    }

    public DesiredConfiguration ToDesired() => new()
    {
        OwnerAccountName = Owner,
        DistroName = Distro ?? DesiredConfiguration.DefaultDistro,
        LinuxUser = LinuxUser ?? DesiredConfiguration.DefaultLinuxUser,
        ListenPort = Port ?? DesiredConfiguration.DefaultListenPort,
        SshAuth = SshAuth ?? SshAuthentication.PasswordAndPublicKey,
    };
}
