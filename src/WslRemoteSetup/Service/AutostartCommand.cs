using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WslRemoteSetup.Core.Autostart;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Core.Ssh;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Forwarding;

namespace WslRemoteSetup.Service;

internal sealed record AutostartOptions(string Action, Guid? InstallationId, string? HostKey, string Distro);

/// <summary>
/// `autostart plan|install|remove` (M5): 보호 경로 설치, 관리자 정책, 네트워크 관리부 서비스, 소유자 로그온 작업.
/// install/remove는 승격 + 확인 입력이 필요하다. plan은 변경하지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AutostartCommand
{
    private static readonly string Sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string Sc = Path.Combine(Sys, "sc.exe");
    private static readonly string Schtasks = Path.Combine(Sys, "schtasks.exe");
    private static readonly string Conhost = Path.Combine(Sys, "conhost.exe");

    private sealed record Plan(
        Guid InstallationId, string OwnerSid, string OwnerName, ForwardingPolicy Policy,
        string SourceExe, string SourceHash, string? InstalledHash,
        ServiceInfo? Service, bool TaskExists, string TaskName, string TaskArguments,
        List<string> Blockers, List<string> Warnings);

    private sealed record ServiceInfo(string PathName, string StartName, string State, string StartMode);

    public static async Task<int> RunAsync(AutostartOptions o)
    {
        switch (o.Action)
        {
            case "plan": return await PlanOrInstallAsync(o, install: false);
            case "install": return await PlanOrInstallAsync(o, install: true);
            case "remove": return await RemoveAsync(o);
            default:
                Console.Error.WriteLine("사용: autostart plan|install --installation-id <guid> --host-key SHA256:… [--distro <이름>] | autostart remove --installation-id <guid>");
                return App.ExitUsage;
        }
    }

    private static CommandRunner Runner() => new([Sc, Schtasks]);

    private static async Task<int> PlanOrInstallAsync(AutostartOptions o, bool install)
    {
        if (install && !WindowsForwarding.IsElevated()) { Console.Error.WriteLine("[ELEVATION_REQUIRED] autostart install은 관리자(UAC) 프로세스에서 실행해야 합니다."); return App.ExitUsage; }
        if (o.InstallationId is null || o.HostKey is null) return await RunAsync(o with { Action = "" });

        var plan = await BuildPlanAsync(o);
        Print(plan, o);
        if (!install)
        {
            Console.WriteLine();
            Console.WriteLine("이 명령은 시스템을 변경하지 않았습니다.");
            return plan.Blockers.Count == 0 ? App.ExitOk : App.ExitChecksFailed;
        }
        if (plan.Blockers.Count > 0) return App.ExitChecksFailed;
        if (!ConfirmPrompt.Ask(out var code)) return code;

        var runner = Runner();
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State ?? throw new InvalidOperationException("설치 상태가 없습니다. forward apply를 먼저 실행하세요.");
        var resources = state.ResourceOwnership.ToList();
        var now = DateTimeOffset.UtcNow;
        void Own(string kind, string key, string desc, string? value)
        {
            resources.RemoveAll(r => r.Kind == kind && r.Key == key);
            resources.Add(new OwnedResource(kind, key, desc, now) { Value = value });
        }

        try
        {
            // 1. 보호 경로에 실행 파일
            if (plan.InstalledHash != plan.SourceHash)
            {
                StopServiceIfOurs(runner, plan);
                Directory.CreateDirectory(ProtectedPaths.InstallDirectory); // Program Files 상속 ACL(일반 사용자 쓰기 불가)
                File.Copy(plan.SourceExe, ProtectedPaths.InstalledExe, overwrite: true);
                Console.WriteLine($"  실행 파일 설치: {ProtectedPaths.InstalledExe}");
            }
            if (!ProtectedPaths.IsAdminOnlyWritable(ProtectedPaths.InstalledExe, out var exeWhy)) throw new InvalidOperationException($"설치 경로 ACL 검증 실패: {exeWhy}");
            Own("file", ProtectedPaths.InstalledExe, "WslRemoteSetup 실행 파일", plan.SourceHash);

            // 2. 관리자 정책
            ProtectedPaths.EnsureProtectedDirectory(ProtectedPaths.PolicyDirectory);
            await File.WriteAllTextAsync(ProtectedPaths.PolicyFile, System.Text.Json.JsonSerializer.Serialize(plan.Policy, StateStore.JsonOptions), new UTF8Encoding(false));
            if (!ProtectedPaths.IsAdminOnlyWritable(ProtectedPaths.PolicyFile, out var polWhy)) throw new InvalidOperationException($"정책 ACL 검증 실패: {polWhy}");
            Own("policy", ProtectedPaths.PolicyFile, "포워딩 정책", null);
            Console.WriteLine($"  정책 작성: {ProtectedPaths.PolicyFile}");

            // 3. 네트워크 관리부 서비스
            var binPath = $"\"{ProtectedPaths.InstalledExe}\" service";
            if (plan.Service is null)
            {
                await Must(runner, Sc, ["create", ProtectedPaths.ServiceName, "binPath=", binPath, "start=", "delayed-auto", "obj=", "LocalSystem", "DisplayName=", "WSL Remote Setup Network Manager"]);
                await Must(runner, Sc, ["description", ProtectedPaths.ServiceName, $"WslRemoteSetup {plan.InstallationId:D}: 승인된 SSH 포워딩 tuple만 갱신"]);
                await Must(runner, Sc, ["failure", ProtectedPaths.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000//"]);
                Console.WriteLine($"  서비스 등록: {ProtectedPaths.ServiceName}");
            }
            Own("service", ProtectedPaths.ServiceName, "네트워크 관리부", binPath);
            await runner.RunAsync(new CommandSpec(Sc, ["start", ProtectedPaths.ServiceName]) { Timeout = TimeSpan.FromSeconds(30) });

            // 4. 소유자 로그온 작업
            var xml = LogonTaskXml.Build(plan.OwnerSid, Conhost, plan.TaskArguments, $"WslRemoteSetup {plan.InstallationId:D}: 소유자 로그온 후 WSL 유지·포워딩 확인");
            var xmlPath = Path.Combine(ProtectedPaths.PolicyDirectory, "agent-task.xml");
            await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode);
            if (!plan.TaskExists)
                await Must(runner, Schtasks, ["/Create", "/TN", plan.TaskName, "/XML", xmlPath]);
            Own("scheduled-task", plan.TaskName, "소유자 로그온 에이전트", plan.OwnerSid);
            Console.WriteLine($"  로그온 작업 등록: {plan.TaskName}");
        }
        finally
        {
            store.Save(state with { ResourceOwnership = resources, OwnerSid = plan.OwnerSid, DistroName = o.Distro });
        }

        // 5. 지금 바로 에이전트 시작(다시 로그인하지 않아도 되도록)
        await runner.RunAsync(new CommandSpec(Schtasks, ["/Run", "/TN", plan.TaskName]) { Timeout = TimeSpan.FromSeconds(30) });
        Console.WriteLine("  에이전트 시작 요청(/Run). 상태는 일반 세션에서 'status'로 확인하세요.");
        return App.ExitOk;
    }

    private static async Task<Plan> BuildPlanAsync(AutostartOptions o)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        using var id = WindowsIdentity.GetCurrent();
        var ownerSid = id.User!.Value;
        var ownerName = id.Name;

        var state = new StateStore(ProtectedPaths.StateFile).Load().State;
        var listen = "";
        var port = 0;
        if (state is null) blockers.Add("설치 상태(포워딩 기록)가 없습니다. forward apply를 먼저 완료하세요.");
        else
        {
            if (state.InstallationId != o.InstallationId) blockers.Add("설치 ID가 상태 파일과 다릅니다.");
            var proxy = state.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy);
            if (proxy is null) blockers.Add("소유한 portproxy 기록이 없습니다.");
            else
            {
                var sep = proxy.Key.LastIndexOf(':');
                listen = proxy.Key[..sep];
                port = int.Parse(proxy.Key[(sep + 1)..], CultureInfo.InvariantCulture);
            }
            if (state.OwnerSid is { } recordedOwner && recordedOwner != ownerSid)
                blockers.Add($"현재 계정이 기록된 소유자({recordedOwner})와 다릅니다.");
        }

        var policy = new ForwardingPolicy
        {
            InstallationId = o.InstallationId!.Value,
            OwnerSid = ownerSid,
            ListenAddress = string.IsNullOrEmpty(listen) ? "0.0.0.0" : listen,
            ListenPort = port == 0 ? 1 : port,
            ConnectPort = 22,
            SshHostKeyFingerprints = [o.HostKey!],
        };
        if (port != 0) blockers.AddRange(policy.Validate());

        var source = Environment.ProcessPath ?? "";
        var dir = Path.GetDirectoryName(source) ?? "";
        if (File.Exists(Path.Combine(dir, "WslRemoteSetup.dll")))
            blockers.Add($"단일 EXE(publish 산출물)로 실행해야 합니다. 현재: {source}");
        var sourceHash = File.Exists(source) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))) : "";
        var installedHash = File.Exists(ProtectedPaths.InstalledExe) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ProtectedPaths.InstalledExe))) : null;
        if (installedHash is not null && state?.ResourceOwnership.Any(r => r.Kind == "file" && r.Key == ProtectedPaths.InstalledExe) != true)
            blockers.Add($"{ProtectedPaths.InstalledExe}가 이미 있지만 이 도구 기록에 없습니다. 덮어쓰지 않습니다.");

        var service = QueryService();
        var expectedBin = $"\"{ProtectedPaths.InstalledExe}\" service";
        if (service is not null && !string.Equals(service.PathName, expectedBin, StringComparison.OrdinalIgnoreCase))
            blockers.Add($"같은 이름의 서비스가 다른 실행 경로로 있습니다: {service.PathName}");

        var taskName = $@"\WslRemoteSetup\Agent-{o.InstallationId!.Value.ToString("N")[..8]}";
        var q = await Runner().RunAsync(new CommandSpec(Schtasks, ["/Query", "/TN", taskName, "/XML"]) { Timeout = TimeSpan.FromSeconds(20) });
        var taskExists = q.ExitedZero;
        if (taskExists && !q.StandardOutput.Contains(o.InstallationId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase))
            blockers.Add($"같은 이름의 작업이 있지만 이 설치의 것이 아닙니다: {taskName}");

        var args = $"--headless \"{ProtectedPaths.InstalledExe}\" agent --installation-id {o.InstallationId:D} --distro {o.Distro}";
        if (!InputValidator.DistroName(o.Distro).IsValid) blockers.Add("배포판 이름 오류");
        if (!HostKeyFingerprint.IsWellFormed(o.HostKey)) blockers.Add("host key 형식 오류");
        warnings.Add("소유자 계정이 Administrators 구성원입니다. 에이전트는 LeastPrivilege(비승격)로 실행되지만, 일반 사용자 소유자 경계(T09)는 이 환경에서 입증되지 않습니다.");
        warnings.Add("로그아웃·재부팅 후에는 소유자가 다시 로그인할 때까지 WSL·SSH를 사용할 수 없습니다(1.0 한계). 서비스는 부팅 시 시작되지만 WSL을 실행하지 않습니다.");

        return new Plan(o.InstallationId.Value, ownerSid, ownerName, policy, source, sourceHash, installedHash, service, taskExists, taskName, args, blockers, warnings);
    }

    private static void Print(Plan p, AutostartOptions o)
    {
        Console.WriteLine();
        Console.WriteLine($"자동 실행 계획 — 설치 {p.InstallationId:D}, 소유자 {p.OwnerName} ({p.OwnerSid}), 배포판 {o.Distro}");
        Console.WriteLine($"  1. 실행 파일: {p.SourceExe} → {ProtectedPaths.InstalledExe} [{(p.InstalledHash is null ? "설치" : p.InstalledHash == p.SourceHash ? "변경 없음" : "갱신")}]");
        Console.WriteLine($"     SHA-256 {p.SourceHash}");
        Console.WriteLine($"  2. 정책: {ProtectedPaths.PolicyFile} [SYSTEM·Administrators만 쓰기]");
        Console.WriteLine($"     소유자 {p.Policy.OwnerSid}, 수신 {p.Policy.ListenAddress}:{p.Policy.ListenPort} → WSL :{p.Policy.ConnectPort}, host key {string.Join(",", p.Policy.SshHostKeyFingerprints)}, 갱신 간격 ≥ {p.Policy.MinUpdateInterval.TotalSeconds:F0}s");
        Console.WriteLine($"  3. 서비스 {ProtectedPaths.ServiceName} (LocalSystem, 지연된 자동 시작, 실패 시 재시작) [{(p.Service is null ? "등록" : $"이미 있음 {p.Service.State}")}]");
        Console.WriteLine($"     파이프 \\\\.\\pipe\\WslRemoteSetup.Net.{p.InstallationId:N} (SYSTEM·Administrators, 소유자 ReadWrite, NETWORK 거부)");
        Console.WriteLine($"  4. 로그온 작업 {p.TaskName} [{(p.TaskExists ? "이미 있음" : "등록")}]");
        Console.WriteLine($"     트리거: 소유자 로그온(+15초), InteractiveToken, LeastPrivilege, 비밀번호 저장 없음");
        Console.WriteLine($"     실행: {Conhost} {p.TaskArguments}");
        Console.WriteLine("  5. 등록 직후 작업을 한 번 실행해 에이전트 시작");
        Console.WriteLine("  되돌리기: autostart remove (작업·서비스·정책·설치 파일 제거. 포워딩·방화벽은 forward remove)");
        foreach (var w in p.Warnings) Console.WriteLine($"  [WARN] {w}");
        foreach (var b in p.Blockers) Console.WriteLine($"  [BLOCK] {b}");
        Console.WriteLine(p.Blockers.Count == 0 ? "  → 적용 가능" : "  → 적용 불가");
    }

    private static ServiceInfo? QueryService()
    {
        using var s = new ManagementObjectSearcher($"SELECT PathName, StartName, State, StartMode FROM Win32_Service WHERE Name='{ProtectedPaths.ServiceName}'");
        foreach (ManagementObject m in s.Get())
            return new ServiceInfo((string)m["PathName"], (string)m["StartName"], (string)m["State"], (string)m["StartMode"]);
        return null;
    }

    private static void StopServiceIfOurs(CommandRunner runner, Plan plan)
    {
        if (plan.Service is null) return;
        runner.RunAsync(new CommandSpec(Sc, ["stop", ProtectedPaths.ServiceName]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
        Thread.Sleep(2000);
    }

    private static async Task Must(CommandRunner runner, string exe, IReadOnlyList<string> args)
    {
        var r = await runner.RunAsync(new CommandSpec(exe, args) { Timeout = TimeSpan.FromSeconds(60) });
        if (!r.ExitedZero) throw new InvalidOperationException($"{Path.GetFileName(exe)} {args[0]} 실패(exit {r.ExitCode}): {r.StandardOutput.Trim()} {r.StandardError.Trim()}");
    }

    private static async Task<int> RemoveAsync(AutostartOptions o)
    {
        if (!WindowsForwarding.IsElevated()) { Console.Error.WriteLine("[ELEVATION_REQUIRED] autostart remove는 관리자(UAC) 프로세스에서 실행해야 합니다."); return App.ExitUsage; }
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State;
        if (state is null || (o.InstallationId is { } gid && state.InstallationId != gid)) { Console.WriteLine("이 설치의 자동 실행 기록이 없습니다."); return App.ExitOk; }
        var owned = state.ResourceOwnership.Where(r => r.Kind is "scheduled-task" or "service" or "policy" or "file").ToList();
        if (owned.Count == 0) { Console.WriteLine("제거할 자동 실행 리소스가 없습니다."); return App.ExitOk; }
        Console.WriteLine("제거 대상(이 도구 기록만):");
        foreach (var r in owned) Console.WriteLine($"  {r.Kind}: {r.Key}");
        Console.WriteLine("  (portproxy·방화벽 규칙은 유지 — 필요하면 forward remove)");
        if (!ConfirmPrompt.Ask(out var code)) return code;

        var runner = Runner();
        var remaining = state.ResourceOwnership.ToList();
        foreach (var r in owned.OrderBy(r => r.Kind switch { "scheduled-task" => 0, "service" => 1, "policy" => 2, _ => 3 }))
        {
            switch (r.Kind)
            {
                case "scheduled-task":
                    await runner.RunAsync(new CommandSpec(Schtasks, ["/End", "/TN", r.Key]) { Timeout = TimeSpan.FromSeconds(20) });
                    await runner.RunAsync(new CommandSpec(Schtasks, ["/Delete", "/TN", r.Key, "/F"]) { Timeout = TimeSpan.FromSeconds(20) });
                    break;
                case "service":
                    var svc = QueryService();
                    if (svc is not null && !string.Equals(svc.PathName, r.Value, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"  [CONFLICT] 서비스 경로가 바뀌어 제거하지 않습니다: {svc.PathName}"); continue; }
                    await runner.RunAsync(new CommandSpec(Sc, ["stop", r.Key]) { Timeout = TimeSpan.FromSeconds(30) });
                    await Task.Delay(2000);
                    await runner.RunAsync(new CommandSpec(Sc, ["delete", r.Key]) { Timeout = TimeSpan.FromSeconds(30) });
                    break;
                case "policy":
                    File.Delete(r.Key);
                    File.Delete(Path.Combine(ProtectedPaths.PolicyDirectory, "agent-task.xml"));
                    break;
                case "file":
                    if (File.Exists(r.Key) && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(r.Key))) != r.Value) { Console.WriteLine($"  [CONFLICT] {r.Key}가 설치 후 바뀌어 지우지 않습니다."); continue; }
                    File.Delete(r.Key);
                    break;
            }
            remaining.Remove(r);
            Console.WriteLine($"  제거: {r.Kind} {r.Key}");
        }
        store.Save(state with { ResourceOwnership = remaining });
        return App.ExitOk;
    }
}

/// <summary>공통 [Y/n] 확인(ADR 0004).</summary>
internal static class ConfirmPrompt
{
    public static bool Ask(out int exitCode)
    {
        exitCode = App.ExitOk;
        if (Console.IsInputRedirected) { Console.Error.WriteLine("[UserInputRequired] 확인 입력을 받을 수 없습니다."); exitCode = App.ExitUserInputRequired; return false; }
        Console.WriteLine();
        for (var i = 0; i < 3; i++)
        {
            Console.Write($"위 변경을 진행할까요? {Confirmation.Prompt} ");
            switch (Confirmation.Parse(Console.ReadLine()))
            {
                case ConfirmAnswer.Yes: return true;
                case ConfirmAnswer.No: Console.WriteLine("취소했습니다. 변경하지 않았습니다."); exitCode = App.ExitCanceled; return false;
                default: Console.WriteLine("Y 또는 n으로 입력하세요(Enter = Y)."); break;
            }
        }
        Console.WriteLine("확인 입력이 올바르지 않아 취소했습니다.");
        exitCode = App.ExitCanceled;
        return false;
    }
}
