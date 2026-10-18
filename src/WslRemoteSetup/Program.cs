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
return await App.RunAsync(args);

internal static class App
{
    public const int ExitOk = 0;
    public const int ExitChecksFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitUserInputRequired = 3;
    public const int ExitError = 4;

    private static string Version =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

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
            var sub = command == "forward" && args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1].ToLowerInvariant() : null;
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
                "status" => Status(),
                "poc-lifetime" => await PocLifetimeAsync(options),
                "forward" => await WslRemoteSetup.Forwarding.ForwardCommand.RunAsync(new(
                    sub ?? "", options.InstallationId, options.Listen, options.Port ?? DesiredConfiguration.DefaultListenPort,
                    options.WslIp, options.HostKey, options.Distro ?? DesiredConfiguration.DefaultDistro)),
                "help" or "--help" or "-h" or "/?" => Help(),
                "version" or "--version" => PrintVersion(),
                "install" or "resume" or "repair" or "pause" or "resume-monitoring" or "uninstall" => NotYet(command),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
            return ExitError;
        }
    }

    private static async Task<int> InteractiveAsync()
    {
        Console.WriteLine($"WSL Remote Setup {Version}");
        Console.WriteLine();
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("[UserInputRequired] 대화형 입력을 받을 수 없습니다. 'doctor', 'plan', 'status' 명령을 사용하세요.");
            return ExitUserInputRequired;
        }

        while (true)
        {
            Console.WriteLine("1. 새로 설치 / 기존 환경에 추가 설정");
            Console.WriteLine("2. 현재 상태 확인");
            Console.WriteLine("3. 연결 복구");
            Console.WriteLine("4. 설정 변경");
            Console.WriteLine("5. 자동 실행 일시중지 / 재개");
            Console.WriteLine("6. 이 도구가 만든 설정 제거");
            Console.WriteLine("0. 종료");
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
                    await PlanAsync(Options.Empty);
                    Console.WriteLine();
                    Console.WriteLine("이 버전은 진단과 계획까지만 제공합니다. 적용 단계는 이후 버전에서 단계별 승인과 함께 제공됩니다.");
                    break;
                case "2":
                    await DoctorAsync(Options.Empty);
                    Status();
                    break;
                case "3" or "4" or "5" or "6":
                    Console.WriteLine("이 기능은 아직 구현되지 않았습니다.");
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
        return results.Any(r => r.Status == CheckStatus.Fail) ? ExitChecksFailed : ExitOk;
    }

    private static async Task<int> PlanAsync(Options o)
    {
        var desired = o.ToDesired();
        var invalid = desired.Validate();
        if (invalid.Count > 0) { invalid.ToList().ForEach(Console.Error.WriteLine); return ExitUsage; }

        var snapshot = await CollectAsync();
        var plan = Planner.Create(snapshot, desired, DateTimeOffset.Now);
        Console.WriteLine();
        Console.WriteLine($"설치 계획 — 소유 계정 {desired.OwnerAccountName ?? "(현재 사용자)"}, 배포판 {desired.DistroName}, Linux {desired.LinuxUser}, 수신 TCP {desired.ListenPort} → WSL {desired.WslSshPort}");
        Console.WriteLine("이 명령은 시스템을 변경하지 않습니다.");
        Console.WriteLine();
        ConsoleReport.PrintPlan(plan);
        return ExitOk;
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

    private static int Status()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WslRemoteSetup", "state", "install-state.json");
        var result = new StateStore(path).Load();
        Console.WriteLine();
        Console.WriteLine($"설치 상태 파일: {path}");
        switch (result.Outcome)
        {
            case LoadOutcome.NotFound:
                Console.WriteLine("  이 도구로 진행한 설치 기록이 없습니다.");
                return ExitOk;
            case LoadOutcome.Loaded:
                var s = result.State!;
                Console.WriteLine($"  설치 ID {s.InstallationId}, 단계 {s.Phase}, 갱신 {s.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
                if (s.PendingAction is not null) Console.WriteLine($"  대기 작업: {s.PendingAction}");
                if (s.LastError is not null) Console.WriteLine($"  마지막 오류: {s.LastError}");
                return ExitOk;
            default:
                Console.WriteLine($"  [STATE_{result.Outcome.ToString().ToUpperInvariant()}] {result.Error}");
                Console.WriteLine("  상태 파일을 초기화하지 않았습니다. 복구 절차가 필요합니다.");
                return ExitChecksFailed;
        }
    }

    private static int Help()
    {
        Console.WriteLine($"""
            WSL Remote Setup {Version}

            사용법:
              WslRemoteSetup.exe                 대화형 마법사
              WslRemoteSetup.exe doctor [옵션]   비파괴 진단
              WslRemoteSetup.exe plan [옵션]     현재 상태 기반 설치 계획(변경 없음)
              WslRemoteSetup.exe status          설치 상태 파일 확인
              WslRemoteSetup.exe forward plan|apply|remove  Windows→WSL SSH 포워딩·LAN 방화벽(apply/remove는 관리자)
              WslRemoteSetup.exe version

            옵션:
              --owner <Windows 계정>    WSL 배포판 소유 계정 (기본: 현재 사용자)
              --distro <이름>           배포판 (기본: {DesiredConfiguration.DefaultDistro})
              --linux-user <이름>       Linux 사용자 (기본: {DesiredConfiguration.DefaultLinuxUser})
              --port <번호>             Windows SSH 수신 포트 (기본: {DesiredConfiguration.DefaultListenPort})
              --json <파일>             doctor 결과를 JSON으로 저장

            install/resume/repair/pause/resume-monitoring/uninstall은 이후 버전에서 제공됩니다.
            """);
        return ExitOk;
    }

    private static int PrintVersion() { Console.WriteLine(Version); return ExitOk; }

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
    public string? Error { get; init; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            var key = args[i];
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
                case "--host-key":
                    if (!WslRemoteSetup.Core.Ssh.HostKeyFingerprint.IsWellFormed(value)) return o with { Error = "--host-key는 SHA256:… 형식이어야 합니다." };
                    o = o with { HostKey = value };
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
    };
}
