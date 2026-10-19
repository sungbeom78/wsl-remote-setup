using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WslRemoteSetup.Core.Autostart;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Core.Ssh;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Reporting;

namespace WslRemoteSetup.Service;

/// <param name="PreLogin">install과 함께 로그인 전 접속을 켤지(true)/끌지(false). null이면 건드리지 않는다.</param>
internal sealed record AutostartOptions(string Action, Guid? InstallationId, string? HostKey, string Distro, bool? PreLogin = null);

/// <summary>
/// `autostart plan|install|remove` (M5): 보호 경로 설치, 관리자 정책, 네트워크 관리부 서비스, 소유자 로그온 작업.
/// 화면에는 쉬운 말만 보이고, 설치 ID·SID·파일 확인값·서버 키·명령줄은 작업 기록에 남긴다(ADR 0006).
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
        List<string> Blockers, List<string> TechNotes, bool AddressIsDhcp = false);

    private sealed record ServiceInfo(string PathName, string StartName, string State, string StartMode);

    public static async Task<int> RunAsync(AutostartOptions o) => o.Action switch
    {
        "plan" => await PlanOrInstallAsync(o, install: false),
        "install" => await InstallWithPreLoginAsync(o),
        "remove" => await RemoveAsync(o),
        // boot-poc-*는 시험 때 쓰던 이름(호환용)
        "prelogin-on" or "boot-poc-create" => await PreLoginAsync(enable: true, confirm: true),
        "prelogin-off" or "boot-poc-remove" => await PreLoginAsync(enable: false, confirm: true),
        _ => Usage(),
    };

    private static async Task<int> InstallWithPreLoginAsync(AutostartOptions o)
    {
        var code = await PlanOrInstallAsync(o, install: true);
        if (code != App.ExitOk || o.PreLogin is not { } enable) return code;
        // 설치 마법사에서 이미 [Y/n]으로 고른 값이므로 다시 묻지 않는다.
        return await PreLoginAsync(enable, confirm: false);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("사용: autostart plan | install [--prelogin on|off] | remove | prelogin-on | prelogin-off   (plan 외에는 관리자 창에서)");
        return App.ExitUsage;
    }

    private static CommandRunner Runner() => new([Sc, Schtasks]);

    private static async Task<int> PlanOrInstallAsync(AutostartOptions o, bool install)
    {
        if (install && !WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요.");
            return App.ExitUsage;
        }

        var history = Ui.OpenHistory(install ? "자동 실행 설정 적용" : "자동 실행 설정 계획 확인");
        var plan = await BuildPlanAsync(o, history);
        Print(plan, o, install);

        if (!install)
        {
            history.Finish(plan.Blockers.Count == 0 ? "진행 가능(변경 없음)" : "진행 불가(변경 없음)");
            Ui.HistoryHint(history);
            return plan.Blockers.Count == 0 ? App.ExitOk : App.ExitChecksFailed;
        }
        if (plan.Blockers.Count > 0) { history.Finish("진행 불가"); Ui.HistoryHint(history); return App.ExitChecksFailed; }
        if (plan.AddressIsDhcp)
        {
            Console.WriteLine();
            Ui.Line($"이 PC 주소({plan.Policy.ListenAddress})는 공유기가 자동으로 정한 주소라 바뀔 수 있습니다.");
            Ui.Line("주소가 바뀌면, 같은 공유기 범위 안에서만 새 주소로 연결 통로를 옮길 수 있습니다.");
            Ui.Line("(다른 네트워크·공용 네트워크로 바뀌면 옮기지 않고 멈춥니다. 접속하는 PC의 주소 설정은 직접 바꿔야 합니다.)");
            Console.Write($"  주소가 바뀌면 따라갈까요? {Confirmation.Prompt} ");
            var follow = Confirmation.Parse(Console.ReadLine()) != ConfirmAnswer.No;
            plan = plan with { Policy = plan.Policy with { FollowAddressChange = follow } };
            history.Note($"주소 따라가기 선택: {(follow ? "켬" : "끔")}");
        }
        if (!ConfirmPrompt.Ask(out var code)) { history.Finish("사용자가 취소함(변경 없음)"); return code; }
        history.Note("사용자가 [Y/n] 확인에서 진행을 선택함");

        var runner = Runner();
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State!;
        var resources = state.ResourceOwnership.ToList();
        var now = DateTimeOffset.UtcNow;
        void Own(string kind, string key, string desc, string? value)
        {
            resources.RemoveAll(r => r.Kind == kind && r.Key == key);
            resources.Add(new OwnedResource(kind, key, desc, now) { Value = value });
        }

        Console.WriteLine();
        var ok = false;
        try
        {
            Ui.Progress(1, 5, "프로그램을 안전한 위치에 설치", () =>
            {
                history.Detail("원본", plan.SourceExe);
                history.Detail("설치 위치", ProtectedPaths.InstalledExe);
                history.Detail("파일 확인값(SHA-256)", plan.SourceHash);
                if (plan.InstalledHash != plan.SourceHash)
                {
                    StopServiceIfPresent(runner, plan, history);
                    StopAgentIfRunning(runner, plan, history);
                    Directory.CreateDirectory(ProtectedPaths.InstallDirectory);
                    File.Copy(plan.SourceExe, ProtectedPaths.InstalledExe, overwrite: true);
                }
                else history.Note("같은 파일이 이미 설치되어 있어 복사하지 않음");
                if (!ProtectedPaths.IsAdminOnlyWritable(ProtectedPaths.InstalledExe, out var why))
                    throw new InvalidOperationException($"설치 위치 권한 검사 실패: {why}");
                Own("file", ProtectedPaths.InstalledExe, "WslRemoteSetup 실행 파일", plan.SourceHash);
                history.Done("설치·권한 검사 통과");
            }, history);

            Ui.Progress(2, 5, "연결 규칙 저장(관리자만 수정 가능)", () =>
            {
                ProtectedPaths.EnsureProtectedDirectory(ProtectedPaths.PolicyDirectory);
                File.WriteAllText(ProtectedPaths.PolicyFile, System.Text.Json.JsonSerializer.Serialize(plan.Policy, StateStore.JsonOptions), new UTF8Encoding(false));
                if (!ProtectedPaths.IsAdminOnlyWritable(ProtectedPaths.PolicyFile, out var why))
                    throw new InvalidOperationException($"규칙 파일 권한 검사 실패: {why}");
                Own("policy", ProtectedPaths.PolicyFile, "포워딩 정책", null);
                history.Detail("파일", ProtectedPaths.PolicyFile);
                history.Detail("설치 ID", plan.InstallationId.ToString("D"));
                history.Detail("소유자 SID", plan.OwnerSid);
                history.Detail("수신", $"{plan.Policy.ListenAddress}:{plan.Policy.ListenPort} → WSL:{plan.Policy.ConnectPort}");
                history.Detail("WSL SSH 서버 키", string.Join(", ", plan.Policy.SshHostKeyFingerprints));
                history.Done("저장·권한 검사 통과");
            }, history);

            Ui.Progress(3, 5, "백그라운드 연결 관리 프로그램 등록", () =>
            {
                var binPath = $"\"{ProtectedPaths.InstalledExe}\" service";
                history.Detail("서비스 이름", ProtectedPaths.ServiceName);
                history.Detail("실행 계정", "LocalSystem, 시작 유형 자동(지연 없음)");
                history.Detail("명령", binPath);
                if (plan.Service is null)
                {
                    Must(runner, Sc, ["create", ProtectedPaths.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "WSL Remote Setup Network Manager"]);
                    Must(runner, Sc, ["description", ProtectedPaths.ServiceName, "WslRemoteSetup: 승인된 SSH 연결 전달 규칙 하나만 관리합니다."]);
                    Must(runner, Sc, ["failure", ProtectedPaths.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000//"]);
                }
                else history.Note("서비스가 이미 등록되어 있음(경로 동일)");
                // 부팅 직후 연결 통로를 빨리 복구하도록 지연 없는 자동 시작(G2 무인 실행 POC 실측: 지연 시작은 약 2분 20초 늦음).
                Must(runner, Sc, ["config", ProtectedPaths.ServiceName, "start=", "auto"]);
                Own("service", ProtectedPaths.ServiceName, "네트워크 관리부", binPath);
                runner.RunAsync(new CommandSpec(Sc, ["start", ProtectedPaths.ServiceName]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
                history.Done("등록·시작 요청");
            }, history);

            Ui.Progress(4, 5, "로그인 시 자동 실행 등록", () =>
            {
                var xml = LogonTaskXml.Build(plan.OwnerSid, Conhost, plan.TaskArguments, $"WslRemoteSetup {plan.InstallationId:D}: 로그인 후 WSL 유지·연결 점검");
                var xmlPath = Path.Combine(ProtectedPaths.PolicyDirectory, "agent-task.xml");
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                history.Detail("작업 이름", plan.TaskName);
                history.Detail("실행", $"{Conhost} {plan.TaskArguments}");
                history.Detail("방식", "로그온 트리거, InteractiveToken, LeastPrivilege, 비밀번호 저장 없음");
                if (!plan.TaskExists) Must(runner, Schtasks, ["/Create", "/TN", plan.TaskName, "/XML", xmlPath]);
                else history.Note("작업이 이미 등록되어 있음");
                Own("scheduled-task", plan.TaskName, "소유자 로그온 에이전트", plan.OwnerSid);
                history.Done("등록");
            }, history);

            Ui.Progress(5, 5, "지금 바로 한 번 실행", () =>
            {
                runner.RunAsync(new CommandSpec(Schtasks, ["/Run", "/TN", plan.TaskName]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
                history.Done("실행 요청");
            }, history);
            ok = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine();
            Ui.Blocked(ex.Message);
        }
        finally
        {
            store.Save(state with { ResourceOwnership = resources, OwnerSid = plan.OwnerSid, DistroName = o.Distro, SshHostKey = plan.Policy.SshHostKeyFingerprints.Count > 0 ? plan.Policy.SshHostKeyFingerprints[0] : null });
            history.Finish(ok ? "성공" : "실패(완료된 단계까지 기록됨)");
        }

        Console.WriteLine();
        Ui.Line(ok ? "설정을 마쳤습니다. 일반 창에서 'status'로 상태를 확인할 수 있습니다." : "설정을 끝내지 못했습니다. 아래 기록에서 어느 단계에서 멈췄는지 확인할 수 있습니다.");
        Ui.HistoryHint(history);
        return ok ? App.ExitOk : App.ExitChecksFailed;
    }

    private static async Task<Plan> BuildPlanAsync(AutostartOptions o, OperationHistory history)
    {
        var blockers = new List<string>();
        var tech = new List<string>();
        using var id = WindowsIdentity.GetCurrent();
        var ownerSid = id.User!.Value;

        history.Begin("현재 상태 확인");
        var state = new StateStore(ProtectedPaths.StateFile).Load().State;
        var installationId = o.InstallationId ?? state?.InstallationId ?? Guid.Empty;
        string listen = "", hostKey = o.HostKey ?? state?.SshHostKey ?? ExistingPolicyHostKey() ?? "";
        var port = 0;
        if (state is null)
            blockers.Add("SSH 연결 통로가 아직 만들어지지 않았습니다. 먼저 'forward apply'를 완료해 주세요.");
        else
        {
            if (o.InstallationId is { } given && given != state.InstallationId) blockers.Add("지정한 설치 정보가 이 PC의 기록과 다릅니다.");
            var proxy = state.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy);
            if (proxy is null) blockers.Add("이 도구가 만든 SSH 연결 통로 기록이 없습니다. 먼저 'forward apply'를 완료해 주세요.");
            else
            {
                var sep = proxy.Key.LastIndexOf(':');
                listen = proxy.Key[..sep];
                port = int.Parse(proxy.Key[(sep + 1)..], CultureInfo.InvariantCulture);
            }
            if (state.OwnerSid is { } recordedOwner && recordedOwner != ownerSid)
                blockers.Add("지금 로그인한 계정이 처음 설정한 계정과 다릅니다. 처음 설정한 계정으로 실행해 주세요.");
        }
        if (!HostKeyFingerprint.IsWellFormed(hostKey)) blockers.Add("WSL SSH 서버 확인 정보가 기록에 없습니다. 'forward apply'를 다시 실행해 주세요.");
        if (!InputValidator.DistroName(o.Distro).IsValid) blockers.Add("WSL 배포판 이름이 올바르지 않습니다.");

        // 접속 주소를 가진 네트워크 카드·LAN 범위·DHCP 여부(ADR 0007)
        string? interfaceId = null, lanCidr = null;
        var isDhcp = false;
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            var ua = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.ToString() == listen);
            if (ua is null) continue;
            interfaceId = ni.Id;
            lanCidr = Core.Network.Ipv4Cidr.FromAddress(ua.Address, ua.PrefixLength).ToString();
            try { isDhcp = ni.GetIPProperties().GetIPv4Properties().IsDhcpEnabled; } catch (System.Net.NetworkInformation.NetworkInformationException) { }
            break;
        }
        var previousFollow = ExistingPolicy()?.FollowAddressChange ?? false;

        var policy = new ForwardingPolicy
        {
            InstallationId = installationId == Guid.Empty ? Guid.NewGuid() : installationId,
            OwnerSid = ownerSid,
            ListenAddress = string.IsNullOrEmpty(listen) ? "0.0.0.0" : listen,
            ListenPort = port == 0 ? 1 : port,
            ConnectPort = 22,
            SshHostKeyFingerprints = string.IsNullOrEmpty(hostKey) ? [] : [hostKey],
            ListenInterfaceId = interfaceId,
            LanCidr = lanCidr,
            FollowAddressChange = isDhcp && previousFollow,
            Services = state?.Services ?? [],
        };
        if (port != 0) blockers.AddRange(policy.Validate());

        var source = Environment.ProcessPath ?? "";
        if (File.Exists(Path.Combine(Path.GetDirectoryName(source) ?? "", "WslRemoteSetup.dll")))
            blockers.Add("배포용 단일 실행 파일(WslRemoteSetup.exe)로 실행해 주세요. 개발용 빌드로는 설치하지 않습니다.");
        var sourceHash = File.Exists(source) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))) : "";
        var installedHash = File.Exists(ProtectedPaths.InstalledExe) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ProtectedPaths.InstalledExe))) : null;
        if (installedHash is not null && state?.ResourceOwnership.Any(r => r.Kind == "file" && r.Key == ProtectedPaths.InstalledExe) != true)
            blockers.Add($"{ProtectedPaths.InstallDirectory}에 이 도구가 설치하지 않은 파일이 있어 덮어쓰지 않습니다.");

        var service = QueryService();
        var expectedBin = $"\"{ProtectedPaths.InstalledExe}\" service";
        if (service is not null && !string.Equals(service.PathName, expectedBin, StringComparison.OrdinalIgnoreCase))
            blockers.Add("같은 이름의 다른 백그라운드 프로그램이 이미 있어 바꾸지 않습니다.");

        var taskName = $@"\WslRemoteSetup\Agent-{policy.InstallationId.ToString("N")[..8]}";
        var q = await Runner().RunAsync(new CommandSpec(Schtasks, ["/Query", "/TN", taskName, "/XML"]) { Timeout = TimeSpan.FromSeconds(20) });
        var taskExists = q.ExitedZero;
        if (taskExists && !q.StandardOutput.Contains(policy.InstallationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            blockers.Add("같은 이름의 다른 자동 실행 작업이 있어 바꾸지 않습니다.");

        var args = $"--headless \"{ProtectedPaths.InstalledExe}\" agent --installation-id {policy.InstallationId:D} --distro {o.Distro}";

        history.Detail("설치 ID", installationId.ToString("D"));
        history.Detail("소유자", $"{id.Name} ({ownerSid})");
        history.Detail("원본 실행 파일", $"{source} (SHA-256 {sourceHash})");
        history.Detail("설치된 실행 파일", installedHash is null ? "없음" : $"SHA-256 {installedHash}");
        history.Detail("서비스", service is null ? "없음" : $"{service.State} {service.PathName}");
        history.Detail("로그온 작업", taskExists ? $"{taskName} 있음" : $"{taskName} 없음");
        history.Detail("WSL SSH 서버 키", hostKey);
        history.Note("소유자 계정이 Administrators 구성원이면 일반 사용자 소유자 경계(T09)는 이 환경에서 입증되지 않음. 에이전트는 LeastPrivilege로 실행");
        foreach (var b in blockers) history.Note($"진행 불가 사유: {b}");
        history.Done(blockers.Count == 0 ? "진행 가능" : "진행 불가");

        history.Detail("주소", $"{listen} DHCP={isDhcp} 카드={interfaceId} 범위={lanCidr} 따라가기(기존)={previousFollow}");
        return new Plan(policy.InstallationId, ownerSid, Ui.CurrentUserName(), policy, source, sourceHash, installedHash, service, taskExists, taskName, args, blockers, tech, isDhcp);
    }

    private static void Print(Plan p, AutostartOptions o, bool install)
    {
        Ui.Title(install ? "자동 실행 설정" : "자동 실행 설정 계획 (아직 아무것도 바꾸지 않았습니다)");
        Ui.Line($"대상 계정: {p.OwnerName}    WSL: {o.Distro}");
        Ui.Line($"SSH 접속 주소: {p.Policy.ListenAddress}, {p.Policy.ListenPort}번");
        Console.WriteLine();
        Ui.Line("할 일");
        Ui.Item(1, "프로그램을 안전한 위치에 설치", p.InstalledHash is null ? "새로 설치" : p.InstalledHash == p.SourceHash ? "변경 없음" : "새 버전으로 교체",
            $"{ProtectedPaths.InstallDirectory} (관리자만 수정 가능)");
        Ui.Item(2, "연결 규칙 저장", "저장", $"외부 {p.Policy.ListenPort}번으로 들어온 접속을 WSL의 SSH로 전달. 처음 등록한 WSL 서버일 때만 허용");
        Ui.Item(3, "백그라운드 연결 관리 프로그램", p.Service is null ? "새로 등록" : "변경 없음",
            "컴퓨터가 켜지면 바로 시작. WSL 주소가 바뀌면 연결 규칙만 고쳐 줌. 문제가 생기면 자동으로 다시 시작");
        Ui.Item(4, "로그인하면 자동 실행", p.TaskExists ? "변경 없음" : "새로 등록",
            $"{p.OwnerName}으로 로그인하면 WSL을 켜 두고 1분마다 연결 점검 (관리자 권한 없이, 비밀번호 저장 안 함)");
        Ui.Item(5, "지금 바로 한 번 실행", "실행", "다시 로그인하지 않아도 바로 동작");
        Console.WriteLine();
        Ui.Line("알아 둘 점");
        Ui.Note($"컴퓨터를 다시 켜거나 로그아웃하면, {p.OwnerName}으로 다시 로그인해야 SSH 접속이 됩니다.");
        Ui.Note("잠시 멈추려면 'pause', 다시 켜려면 'resume-monitoring'.");
        if (p.AddressIsDhcp) Ui.Note("이 PC 주소가 자동 할당이라 바뀔 수 있습니다. 설치할 때 '주소가 바뀌면 따라갈지' 묻습니다. 공유기에서 주소를 고정 배정하는 것을 권장합니다.");
        Ui.Note("되돌리기: 관리자 창에서 'autostart remove'.");
        Console.WriteLine();
        if (p.Blockers.Count == 0) Ui.Line("확인 결과: 진행할 수 있습니다.");
        else foreach (var b in p.Blockers) Ui.Blocked(b);
    }

    /// <summary>
    /// 로그인 전 접속(선택 기능, 기본 꺼짐): 시스템 시작 시 소유자 S4U(암호 저장 안 함)로 에이전트를 실행하는 작업.
    /// 로그인 후 작업과 동시에 돌아도 에이전트 잠금 파일로 하나만 동작한다. 기록은 scheduled-task로 남겨 제거 대상에 포함된다.
    /// G2 실기: 부팅 47초 뒤 준비, 로그인 전 외부 LAN SSH 로그인 PASS(2026-10-18 22:00).
    /// </summary>
    public static string PreLoginTaskName(Guid installationId) => $@"\WslRemoteSetup\BootAgent-{installationId.ToString("N")[..8]}";

    private static async Task<int> PreLoginAsync(bool enable, bool confirm)
    {
        var create = enable;
        if (!WindowsForwarding.IsElevated()) { Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요."); return App.ExitUsage; }
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State;
        if (state?.OwnerSid is null || !File.Exists(ProtectedPaths.InstalledExe)) { Ui.Blocked("자동 실행 설정(autostart install)이 먼저 필요합니다."); return App.ExitChecksFailed; }
        var taskName = PreLoginTaskName(state.InstallationId);
        var exists = state.ResourceOwnership.Any(r => r.Kind == "scheduled-task" && r.Key == taskName);
        if (!create && !exists)
        {
            Ui.Item(1, "로그인 전 접속", "변경 없음", "꺼져 있습니다.");
            return App.ExitOk;
        }
        var history = Ui.OpenHistory(create ? "로그인 전 접속 켜기" : "로그인 전 접속 끄기");
        var runner = Runner();
        Ui.Title(create ? "로그인 전 접속 켜기" : "로그인 전 접속 끄기");
        if (create)
        {
            Ui.Line("컴퓨터가 켜지면 로그인하지 않아도 약 1분 뒤부터 다른 기기에서 SSH로 접속할 수 있게 합니다.");
            Ui.Line("  - 실행 계정: 설정한 계정(관리자 권한 없이). 비밀번호는 저장하지 않습니다.");
            Ui.Line("  - 정전·업데이트로 재부팅되어도 로그인 없이 다시 접속할 수 있습니다.");
            Ui.Line("  - 로그인 후 자동 실행과 함께 있어도 하나만 동작합니다.");
            Ui.Line("  - 끄기: 'autostart prelogin-off' 또는 되돌리기 2단계.");
        }
        else Ui.Line("로그인 전 접속을 끕니다. 재부팅 후에는 로그인해야 접속할 수 있습니다. 로그인 후 자동 실행은 그대로 둡니다.");
        if (confirm && !ConfirmPrompt.Ask(out var code)) { history.Finish("취소"); return code; }

        var resources = state.ResourceOwnership.ToList();
        if (create)
        {
            var args = $"--headless \"{ProtectedPaths.InstalledExe}\" agent --installation-id {state.InstallationId:D} --distro {state.DistroName ?? "Ubuntu-24.04"}";
            var xml = LogonTaskXml.BuildBoot(state.OwnerSid, Conhost, args, $"WslRemoteSetup {state.InstallationId:D}: 로그인 전 접속");
            var xmlPath = Path.Combine(ProtectedPaths.PolicyDirectory, "boot-task.xml");
            await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode);
            history.Detail("작업", taskName);
            history.Detail("방식", "BootTrigger(+30s), S4U, LeastPrivilege");
            var r = await runner.RunAsync(new CommandSpec(Schtasks, ["/Create", "/TN", taskName, "/XML", xmlPath, "/F"]) { Timeout = TimeSpan.FromSeconds(30) });
            if (!r.ExitedZero) { Ui.Blocked($"등록 실패: {r.StandardOutput.Trim()} {r.StandardError.Trim()}"); history.Finish("실패"); return App.ExitChecksFailed; }
            resources.RemoveAll(x => x.Kind == "scheduled-task" && x.Key == taskName);
            resources.Add(new OwnedResource("scheduled-task", taskName, "로그인 전 접속", DateTimeOffset.UtcNow) { Value = state.OwnerSid });
            Ui.Line("✔ 켰습니다. 다음에 컴퓨터를 켜면 로그인하지 않아도 접속할 수 있습니다.");
        }
        else
        {
            await runner.RunAsync(new CommandSpec(Schtasks, ["/End", "/TN", taskName]) { Timeout = TimeSpan.FromSeconds(20) });
            await runner.RunAsync(new CommandSpec(Schtasks, ["/Delete", "/TN", taskName, "/F"]) { Timeout = TimeSpan.FromSeconds(20) });
            resources.RemoveAll(x => x.Kind == "scheduled-task" && x.Key == taskName);
            // /End는 conhost만 끝내고 그 아래 에이전트(세션 0)는 남을 수 있다(G2 실측). 남으면 단일 실행 잠금 때문에
            // 로그인 자동 실행 에이전트가 시작하지 못하므로, 세션 0의 우리 에이전트를 끝내고 로그인 작업을 다시 시작한다.
            var orphans = SessionZeroAgents();
            foreach (var pid in orphans)
            {
                history.Note($"로그인 전 작업이 남긴 에이전트 종료: PID {pid}");
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch (ArgumentException) { } catch (InvalidOperationException) { }
            }
            if (orphans.Count > 0)
            {
                var logonTask = LogonTaskName(state.InstallationId);
                var run = await runner.RunAsync(new CommandSpec(Schtasks, ["/Run", "/TN", logonTask]) { Timeout = TimeSpan.FromSeconds(20) });
                history.Detail("로그인 작업 재시작", $"{logonTask} exit={run.ExitCode}");
            }
            Ui.Line("✔ 껐습니다.");
        }
        store.Save(state with { ResourceOwnership = resources });
        history.Finish("완료");
        Ui.HistoryHint(history);
        return App.ExitOk;
    }

    private static string? ExistingPolicyHostKey()
    {
        var keys = ExistingPolicy()?.SshHostKeyFingerprints;
        return keys is { Count: > 0 } ? keys[0] : null;
    }

    /// <summary>보호 경로의 기존 정책(관리자만 쓸 수 있을 때만 신뢰).</summary>
    private static ForwardingPolicy? ExistingPolicy()
    {
        try
        {
            if (!File.Exists(ProtectedPaths.PolicyFile) || !ProtectedPaths.IsAdminOnlyWritable(ProtectedPaths.PolicyFile, out _)) return null;
            return ForwardingPolicy.Parse(File.ReadAllText(ProtectedPaths.PolicyFile));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { return null; }
    }

    private static ServiceInfo? QueryService()
    {
        using var s = new ManagementObjectSearcher($"SELECT PathName, StartName, State, StartMode FROM Win32_Service WHERE Name='{ProtectedPaths.ServiceName}'");
        foreach (ManagementObject m in s.Get())
            return new ServiceInfo((string)m["PathName"], (string)m["StartName"], (string)m["State"], (string)m["StartMode"]);
        return null;
    }

    private static void StopServiceIfPresent(CommandRunner runner, Plan plan, OperationHistory history)
    {
        if (plan.Service is null) return;
        history.Note("실행 파일 교체를 위해 서비스를 잠시 중지");
        runner.RunAsync(new CommandSpec(Sc, ["stop", ProtectedPaths.ServiceName]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
        for (var i = 0; i < 20 && QueryService()?.State != "Stopped"; i++) Thread.Sleep(500);
    }

    /// <summary>설치 경로의 실행 파일을 쓰는 에이전트를 끝낸다(작업 종료 → 남은 프로세스 정리). 우리 설치 경로의 프로세스만 대상.</summary>
    private static void StopAgentIfRunning(CommandRunner runner, Plan plan, OperationHistory history)
    {
        if (plan.TaskExists)
        {
            history.Note("실행 파일 교체를 위해 자동 실행 작업을 잠시 종료");
            runner.RunAsync(new CommandSpec(Schtasks, ["/End", "/TN", plan.TaskName]) { Timeout = TimeSpan.FromSeconds(20) }).GetAwaiter().GetResult();
        }
        for (var i = 0; i < 20; i++)
        {
            var ours = OurProcesses();
            if (ours.Count == 0) return;
            if (i == 10)
                foreach (var pid in ours)
                {
                    history.Note($"남은 에이전트 프로세스 종료: PID {pid}");
                    try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch (ArgumentException) { } catch (InvalidOperationException) { }
                }
            Thread.Sleep(500);
        }
    }

    private static string Label(OwnedResource r) =>
        r.Kind == "scheduled-task" && r.Key.Contains(@"\BootAgent-", StringComparison.Ordinal) ? "로그인 전 접속" : Plain(r.Kind);

    private static string LogonTaskName(Guid installationId) => $@"\WslRemoteSetup\Agent-{installationId.ToString("N")[..8]}";

    /// <summary>세션 0에서 실행 중인 우리 설치 경로 에이전트(로그인 전 작업이 남긴 것). 관리자 권한에서만 명령줄을 읽을 수 있다.</summary>
    private static List<int> SessionZeroAgents() => AgentProcesses(sessionZeroOnly: true);

    /// <summary>
    /// 외부 연결을 닫기 전에 자동 복구를 멈춘다: 백그라운드 연결 관리 프로그램을 멈추고 연결 규칙 파일을 지운다.
    /// 그대로 두면 에이전트의 복구 요청으로 방금 지운 전달 규칙이 다시 생긴다(G2 실측 2026-10-19).
    /// 서비스·작업 등록 자체는 '자동 실행 중지' 단계에서 지운다. 관리자 권한에서 호출한다.
    /// </summary>
    public static void DisableAutoRepair(OperationHistory history)
    {
        var runner = Runner();
        if (QueryService() is { } svc && svc.State != "Stopped")
        {
            runner.RunAsync(new CommandSpec(Sc, ["stop", ProtectedPaths.ServiceName]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
            for (var k = 0; k < 20 && QueryService()?.State is { } st && st != "Stopped"; k++) Thread.Sleep(500);
            history.Detail("자동 복구 중지", $"{ProtectedPaths.ServiceName} → {QueryService()?.State}");
        }
        if (File.Exists(ProtectedPaths.PolicyFile))
        {
            File.Delete(ProtectedPaths.PolicyFile);
            history.Detail("연결 규칙 파일 삭제", ProtectedPaths.PolicyFile);
        }
    }

    /// <summary>설치 경로의 에이전트 프로세스(자기 자신 제외). 관리자 권한에서만 다른 세션의 명령줄을 읽을 수 있다.</summary>
    private static List<int> AgentProcesses(bool sessionZeroOnly)
    {
        var list = new List<int>();
        using var s = new ManagementObjectSearcher("SELECT ProcessId, SessionId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='WslRemoteSetup.exe'");
        foreach (ManagementObject m in s.Get())
            if ((!sessionZeroOnly || Convert.ToInt32(m["SessionId"], CultureInfo.InvariantCulture) == 0)
                && Convert.ToInt32(m["ProcessId"], CultureInfo.InvariantCulture) != Environment.ProcessId
                && string.Equals(m["ExecutablePath"] as string, ProtectedPaths.InstalledExe, StringComparison.OrdinalIgnoreCase)
                && (m["CommandLine"] as string ?? "").Contains(" agent ", StringComparison.Ordinal))
                list.Add(Convert.ToInt32(m["ProcessId"], CultureInfo.InvariantCulture));
        return list;
    }

    private static List<int> OurProcesses()
    {
        var list = new List<int>();
        using var s = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name='WslRemoteSetup.exe'");
        foreach (ManagementObject m in s.Get())
            if (string.Equals(m["ExecutablePath"] as string, ProtectedPaths.InstalledExe, StringComparison.OrdinalIgnoreCase))
                list.Add(Convert.ToInt32(m["ProcessId"], CultureInfo.InvariantCulture));
        return list;
    }

    private static void Must(CommandRunner runner, string exe, IReadOnlyList<string> args)
    {
        var r = runner.RunAsync(new CommandSpec(exe, args) { Timeout = TimeSpan.FromSeconds(60) }).GetAwaiter().GetResult();
        if (!r.ExitedZero) throw new InvalidOperationException($"{Path.GetFileName(exe)} {args[0]} 실패(종료코드 {r.ExitCode}): {r.StandardOutput.Trim()} {r.StandardError.Trim()}");
    }

    private static async Task<int> RemoveAsync(AutostartOptions o)
    {
        if (!WindowsForwarding.IsElevated()) { Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요."); return App.ExitUsage; }
        var history = Ui.OpenHistory("자동 실행 설정 제거");
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State;
        if (state is null || (o.InstallationId is { } gid && state.InstallationId != gid)) { Console.WriteLine("이 도구가 만든 자동 실행 설정이 없습니다."); history.Finish("대상 없음"); return App.ExitOk; }
        // 설치된 프로그램 파일은 지우지 않는다(사용자 결정). 기록에 남기고 위치만 안내한다.
        var owned = state.ResourceOwnership.Where(r => r.Kind is "scheduled-task" or "service" or "policy").ToList();
        if (owned.Count == 0) { Console.WriteLine("제거할 자동 실행 설정이 없습니다."); history.Finish("대상 없음"); return App.ExitOk; }

        Ui.Title("자동 실행 설정 제거");
        Ui.Line("이 도구가 만든 것만 지웁니다.");
        var n = 0;
        foreach (var r in owned) Ui.Item(++n, Label(r), "제거");
        Ui.Note("SSH 연결 통로와 방화벽 규칙은 그대로 둡니다(필요하면 'forward remove').");
        Ui.Note($"설치된 프로그램({ProtectedPaths.InstallDirectory})은 지우지 않습니다. 필요하면 직접 폴더를 지우면 됩니다.");
        foreach (var r in owned) history.Detail(r.Kind, r.Key);
        if (!ConfirmPrompt.Ask(out var code)) { history.Finish("사용자가 취소함"); return code; }

        var runner = Runner();
        var remaining = state.ResourceOwnership.ToList();
        var i = 0;
        foreach (var r in owned.OrderBy(r => r.Kind switch { "scheduled-task" => 0, "service" => 1, "policy" => 2, _ => 3 }))
        {
            var skipped = false;
            Ui.Progress(++i, owned.Count, Label(r) + " 제거", () =>
            {
                history.Detail("대상", r.Key);
                switch (r.Kind)
                {
                    case "scheduled-task":
                        runner.RunAsync(new CommandSpec(Schtasks, ["/End", "/TN", r.Key]) { Timeout = TimeSpan.FromSeconds(20) }).GetAwaiter().GetResult();
                        runner.RunAsync(new CommandSpec(Schtasks, ["/Delete", "/TN", r.Key, "/F"]) { Timeout = TimeSpan.FromSeconds(20) }).GetAwaiter().GetResult();
                        break;
                    case "service":
                        var svc = QueryService();
                        if (svc is not null && !string.Equals(svc.PathName, r.Value, StringComparison.OrdinalIgnoreCase)) { history.Skip($"경로가 바뀌어 제거하지 않음: {svc.PathName}"); skipped = true; return; }
                        runner.RunAsync(new CommandSpec(Sc, ["stop", r.Key]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
                        for (var k = 0; k < 20 && QueryService()?.State is { } st && st != "Stopped"; k++) Thread.Sleep(500);
                        runner.RunAsync(new CommandSpec(Sc, ["delete", r.Key]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
                        break;
                    case "policy":
                        File.Delete(r.Key);
                        File.Delete(Path.Combine(ProtectedPaths.PolicyDirectory, "agent-task.xml"));
                        break;
                    case "file":
                        if (File.Exists(r.Key) && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(r.Key))) != r.Value) { history.Skip("설치 후 파일이 바뀌어 지우지 않음"); skipped = true; return; }
                        File.Delete(r.Key);
                        break;
                }
                history.Done("제거");
            }, history);
            if (!skipped) remaining.Remove(r);
        }
        // 작업을 지워도 이미 실행 중인 에이전트(로그인 세션·세션 0)는 남는다(G2 실측 2026-10-19). 모두 끝낸다.
        foreach (var pid in AgentProcesses(sessionZeroOnly: false))
        {
            history.Note($"남은 에이전트 종료: PID {pid}");
            try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        store.Save(state with { ResourceOwnership = remaining });
        history.Finish("완료");
        Ui.HistoryHint(history);
        return App.ExitOk;
    }

    private static string Plain(string kind) => kind switch
    {
        "scheduled-task" => "로그인 시 자동 실행",
        "service" => "백그라운드 연결 관리 프로그램",
        "policy" => "연결 규칙 파일",
        "file" => "설치된 프로그램 파일",
        _ => kind,
    };
}

/// <summary>공통 [Y/n] 확인(ADR 0004).</summary>
internal static class ConfirmPrompt
{
    public static bool Ask(out int exitCode)
    {
        exitCode = App.ExitOk;
        if (Console.IsInputRedirected) { Console.Error.WriteLine("확인 입력을 받을 수 없는 환경이라 진행하지 않습니다."); exitCode = App.ExitUserInputRequired; return false; }
        Console.WriteLine();
        for (var i = 0; i < 3; i++)
        {
            Console.Write($"  위 내용대로 진행할까요? {Confirmation.Prompt} ");
            switch (Confirmation.Parse(Console.ReadLine()))
            {
                case ConfirmAnswer.Yes: return true;
                case ConfirmAnswer.No: Console.WriteLine("  취소했습니다. 아무것도 바꾸지 않았습니다."); exitCode = App.ExitCanceled; return false;
                default: Console.WriteLine("  Y 또는 n으로 입력해 주세요(Enter = Y)."); break;
            }
        }
        Console.WriteLine("  입력이 올바르지 않아 취소했습니다.");
        exitCode = App.ExitCanceled;
        return false;
    }
}
