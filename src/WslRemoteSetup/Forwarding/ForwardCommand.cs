using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using WslRemoteSetup.Core;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Probing;

namespace WslRemoteSetup.Forwarding;

internal sealed record ForwardOptions(
    string Action,
    Guid? InstallationId,
    string? ListenAddress,
    int ListenPort,
    string? WslAddress,
    string? HostKey,
    string Distro);

/// <summary>
/// `forward plan|apply|remove` — Windows TCP 수신 → WSL SSH 포워딩과 LAN 한정 방화벽 규칙(M4).
/// Probe → Plan → Validate → 승인(대화형 입력) → Apply → Verify. apply/remove는 승격 프로세스에서만 동작한다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ForwardCommand
{
    public static async Task<int> RunAsync(ForwardOptions o)
    {
        return o.Action switch
        {
            "plan" => await PlanOrApplyAsync(o, apply: false),
            "apply" => await PlanOrApplyAsync(o, apply: true),
            "remove" => await RemoveAsync(),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("사용: forward plan|apply --installation-id <guid> --host-key SHA256:… [--wsl-ip <ipv4>] [--listen <ipv4>] [--port 1004]");
        Console.Error.WriteLine("      forward remove");
        return App.ExitUsage;
    }

    private static async Task<int> PlanOrApplyAsync(ForwardOptions o, bool apply)
    {
        if (apply && !WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("[ELEVATION_REQUIRED] forward apply는 승인된 관리자(UAC) 프로세스에서 실행해야 합니다.");
            return App.ExitUsage;
        }

        var runner = WindowsForwarding.Runner();
        var store = new StateStore(WindowsForwarding.StatePath);
        var loaded = store.Load();
        if (loaded.Outcome is LoadOutcome.Corrupt or LoadOutcome.UnsupportedVersion)
        {
            Console.Error.WriteLine($"[STATE_{loaded.Outcome.ToString().ToUpperInvariant()}] {loaded.Error}. 상태 파일을 초기화하지 않습니다.");
            return App.ExitChecksFailed;
        }
        var state = loaded.State;
        var installationId = o.InstallationId ?? state?.InstallationId;
        if (installationId is null) { Console.Error.WriteLine("--installation-id가 필요합니다."); return App.ExitUsage; }
        if (state is not null && state.InstallationId != installationId)
        {
            Console.Error.WriteLine($"[INSTALLATION_MISMATCH] 기존 상태의 설치 ID({state.InstallationId})와 다릅니다.");
            return App.ExitChecksFailed;
        }
        if (o.HostKey is null) { Console.Error.WriteLine("--host-key SHA256:… 가 필요합니다(WSL SSH 설치 시 출력된 ED25519 지문)."); return App.ExitUsage; }

        Console.WriteLine("진단 중(읽기 전용)...");
        var snapshot = await new WindowsProbe(runner).CollectAsync();
        var listen = o.ListenAddress ?? DefaultListenAddress(snapshot);
        if (listen is null) { Console.Error.WriteLine("수신 IPv4를 결정할 수 없습니다. --listen으로 지정하세요."); return App.ExitUsage; }

        IPAddress? target;
        if (o.WslAddress is not null)
        {
            if (!InputValidator.TryParseIPv4(o.WslAddress, out var t)) { Console.Error.WriteLine("--wsl-ip 형식 오류"); return App.ExitUsage; }
            target = t;
        }
        else if (apply)
        {
            Console.Error.WriteLine("apply는 --wsl-ip를 명시해야 합니다(관리자 프로세스에서 배포판을 실행하지 않음). 먼저 일반 세션에서 plan을 실행하세요.");
            return App.ExitUsage;
        }
        else
        {
            Console.WriteLine($"소유자 세션에서 {o.Distro} 주소 조회(배포판이 기동될 수 있음)...");
            target = await WindowsForwarding.ReadWslAddressAsync(runner, o.Distro);
            if (target is null) { Console.Error.WriteLine("WSL 주소를 확인하지 못했습니다."); return App.ExitChecksFailed; }
        }

        using var id = WindowsIdentity.GetCurrent();
        var facts = await CollectFactsAsync(runner, snapshot, installationId.Value, id.User!.Value, listen, o.ListenPort, target, o.HostKey, state);
        var plan = ForwardingPlanBuilder.Build(facts);
        PrintPlan(plan, facts);

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("이 명령은 시스템을 변경하지 않았습니다.");
            Console.WriteLine($"적용 명령(승인 후 관리자): forward apply --installation-id {installationId:D} --host-key {o.HostKey} --wsl-ip {target} --listen {listen} --port {o.ListenPort}");
            return plan.CanApply ? App.ExitOk : App.ExitChecksFailed;
        }

        if (!plan.CanApply) return App.ExitChecksFailed;
        if (!plan.HasChanges)
        {
            Console.WriteLine("변경할 항목이 없습니다. 검증만 수행합니다.");
            return await VerifyAsync(runner, facts) ? App.ExitOk : App.ExitChecksFailed;
        }
        if (!Confirm(out var confirmExit)) return confirmExit;

        WindowsForwarding.EnsureProtectedStateDirectory();
        var now = DateTimeOffset.UtcNow;
        state ??= new InstallState { InstallationId = installationId.Value, AppVersion = AppVersion() };
        var resources = state.ResourceOwnership.ToList();
        var before = new Dictionary<string, string>(state.BeforeSnapshots);
        var key = string.Create(CultureInfo.InvariantCulture, $"{listen}:{o.ListenPort}");

        try
        {
            if (plan.PortProxy!.Kind is ReconcileKind.Add or ReconcileKind.Update)
            {
                before.TryAdd($"portproxy:{key}", plan.PortProxy.Existing is { } ex ? $"{ex.ConnectAddress}:{ex.ConnectPort}" : "(none)");
                await WindowsForwarding.ApplyPortProxyAsync(runner, plan.PortProxy);
                resources.RemoveAll(r => r.Kind == ResourceKinds.PortProxy && r.Key == key);
                resources.Add(new OwnedResource(ResourceKinds.PortProxy, key, "Windows → WSL SSH", now)
                {
                    Value = string.Create(CultureInfo.InvariantCulture, $"{plan.PortProxy.Desired!.ConnectAddress}:{plan.PortProxy.Desired.ConnectPort}"),
                });
                Console.WriteLine($"  portproxy {plan.PortProxy.Kind}: {key} → {plan.PortProxy.Desired!.ConnectAddress}:{plan.PortProxy.Desired.ConnectPort}");
            }
            if (plan.Firewall == FirewallActionKind.Create)
            {
                WindowsForwarding.CreateFirewallRule(plan.Rule!);
                resources.RemoveAll(r => r.Kind == ResourceKinds.FirewallRule && r.Key == plan.Rule!.Name);
                resources.Add(new OwnedResource(ResourceKinds.FirewallRule, plan.Rule!.Name, plan.Rule.Description, now)
                {
                    Value = $"TCP {plan.Rule.LocalAddress}:{plan.Rule.Port} from {plan.Rule.RemoteCidr} ({plan.Rule.Profile})",
                });
                Console.WriteLine($"  방화벽 규칙 생성: {plan.Rule.Name}");
            }
        }
        finally
        {
            store.Save(state with
            {
                Phase = InstallPhase.Verifying,
                ResourceOwnership = resources,
                BeforeSnapshots = before,
                ApprovalHistory = [.. state.ApprovalHistory, new ApprovalRecord(now, id.User!.Value, PlanHash(plan), ["portproxy", "firewall-ssh"])],
            });
        }

        var ok = await VerifyAsync(runner, facts);
        var latest = store.Load().State!;
        store.Save(latest with
        {
            Phase = ok ? InstallPhase.AwaitingAuthenticationSetup : InstallPhase.Failed,
            PendingAction = ok ? "다른 LAN PC에서 실제 SSH 인증 확인 후 공개키 등록·비밀번호 로그인 해제" : null,
            LastError = ok ? null : "포워딩 사후 검증 실패",
            VerificationResults = [.. latest.VerificationResults, new VerificationRecord("forward-local", ok ? "PASS" : "FAIL", key, DateTimeOffset.UtcNow)],
        });
        return ok ? App.ExitOk : App.ExitChecksFailed;
    }

    private static async Task<ForwardingFacts> CollectFactsAsync(
        Core.Execution.ICommandRunner runner, EnvironmentSnapshot s, Guid installationId, string ownerSid,
        string listen, int port, IPAddress target, string hostKey, InstallState? state)
    {
        var banner = await WindowsForwarding.ReadBannerAsync(target, DesiredConfiguration.DefaultWslSshPort, TimeSpan.FromSeconds(5));
        var keys = await WindowsForwarding.ProbeHostKeyAsync(runner, target, DesiredConfiguration.DefaultWslSshPort);
        return new ForwardingFacts
        {
            InstallationId = installationId,
            OwnerSid = ownerSid,
            ListenAddress = listen,
            ListenPort = port,
            TargetAddress = target,
            ExpectedHostKey = hostKey,
            Interfaces = s.Interfaces ?? [],
            PortProxies = s.PortProxies ?? [],
            TcpListenPorts = s.TcpListenPorts ?? [],
            FirewallRules = s.FirewallRules ?? [],
            WslHostCidr = WindowsForwarding.GetWslHostCidr(),
            EndpointBanner = banner,
            ObservedHostKeys = keys,
            Owned = state?.ResourceOwnership ?? [],
        };
    }

    private static async Task<bool> VerifyAsync(Core.Execution.ICommandRunner runner, ForwardingFacts f)
    {
        Console.WriteLine();
        Console.WriteLine("사후 검증(이 PC 안에서):");
        var results = new List<CheckResult>();
        var snapshot = await new WindowsProbe(runner).CollectAsync();
        var proxy = snapshot.PortProxies?.FirstOrDefault(p => p.ListenAddress == f.ListenAddress && p.ListenPort == f.ListenPort);
        results.Add(proxy is not null && proxy.ConnectAddress == f.TargetAddress.ToString() && proxy.ConnectPort == f.ConnectPort
            ? new("FWD_RULE", "portproxy 규칙", CheckStatus.Pass, $"{proxy.ListenAddress}:{proxy.ListenPort} → {proxy.ConnectAddress}:{proxy.ConnectPort}")
            : new("FWD_RULE", "portproxy 규칙", CheckStatus.Fail, "기대한 규칙이 없습니다."));

        var name = ForwardingPlanBuilder.RuleName(f.InstallationId, f.ListenPort);
        var rules = snapshot.FirewallRules?.Where(r => r.Name == name).ToList() ?? [];
        results.Add(rules.Count == 1 && rules[0].Enabled
            ? new("FWD_FIREWALL", "방화벽 규칙", CheckStatus.Pass, $"{name} [{rules[0].Profiles}/{rules[0].RemoteAddresses}]")
            : new("FWD_FIREWALL", "방화벽 규칙", CheckStatus.Fail, $"{name} 규칙 {rules.Count}개"));

        var listenIp = IPAddress.Parse(f.ListenAddress);
        var banner = await WindowsForwarding.ReadBannerAsync(listenIp, f.ListenPort, TimeSpan.FromSeconds(5));
        results.Add(banner?.StartsWith("SSH-2.0-", StringComparison.Ordinal) == true
            ? new("FWD_BANNER", "Windows 수신 경유 SSH 배너", CheckStatus.Pass, banner)
            : new("FWD_BANNER", "Windows 수신 경유 SSH 배너", CheckStatus.Fail, "배너 없음"));

        var keys = await WindowsForwarding.ProbeHostKeyAsync(runner, listenIp, f.ListenPort);
        results.Add(keys.Any(k => k.Fingerprint == f.ExpectedHostKey)
            ? new("FWD_HOSTKEY", "경유 host key 일치", CheckStatus.Pass, f.ExpectedHostKey)
            : new("FWD_HOSTKEY", "경유 host key 일치", CheckStatus.Fail, "등록 지문과 다르거나 조회 실패"));

        results.Add(new("SSH_LAN_AUTH", "다른 LAN PC의 실제 SSH 인증", CheckStatus.NotTested, "다른 PC에서 확인해야 합니다."));
        results.Add(new("FWD_RESTART", "WSL 재시작 후 대상 갱신", CheckStatus.Pending, "자동 갱신은 M5에서 구현합니다. 지금은 WSL 주소가 바뀌면 forward apply를 다시 실행해야 합니다."));
        ConsoleReport.PrintChecks(results);
        return results.All(r => r.Status != CheckStatus.Fail);
    }

    private static async Task<int> RemoveAsync()
    {
        if (!WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("[ELEVATION_REQUIRED] forward remove는 관리자(UAC) 프로세스에서 실행해야 합니다.");
            return App.ExitUsage;
        }
        var store = new StateStore(WindowsForwarding.StatePath);
        var loaded = store.Load();
        if (loaded.Outcome != LoadOutcome.Loaded) { Console.WriteLine("이 도구가 만든 포워딩 기록이 없습니다."); return App.ExitOk; }
        var state = loaded.State!;
        var owned = state.ResourceOwnership.Where(r => r.Kind is ResourceKinds.PortProxy or ResourceKinds.FirewallRule).ToList();
        if (owned.Count == 0) { Console.WriteLine("제거할 도구 소유 리소스가 없습니다."); return App.ExitOk; }

        Console.WriteLine("제거 대상(이 도구가 만든 리소스만):");
        foreach (var r in owned) Console.WriteLine($"  {r.Kind}: {r.Key} {r.Value}");
        if (!Confirm(out var confirmExit)) return confirmExit;

        var runner = WindowsForwarding.Runner();
        var snapshot = await new WindowsProbe(runner).CollectAsync();
        var remaining = state.ResourceOwnership.ToList();
        var failed = false;
        foreach (var r in owned)
        {
            if (r.Kind == ResourceKinds.PortProxy)
            {
                var sep = r.Key.LastIndexOf(':');
                var addr = r.Key[..sep];
                var port = int.Parse(r.Key[(sep + 1)..], CultureInfo.InvariantCulture);
                var current = snapshot.PortProxies?.FirstOrDefault(p => p.ListenAddress == addr && p.ListenPort == port);
                if (current is null) { remaining.Remove(r); Console.WriteLine($"  이미 없음: {r.Key}"); continue; }
                if ($"{current.ConnectAddress}:{current.ConnectPort}" != r.Value)
                {
                    failed = true;
                    Console.WriteLine($"  [CONFLICT] {r.Key}가 외부에서 바뀌었습니다({current.ConnectAddress}:{current.ConnectPort}). 제거하지 않습니다.");
                    continue;
                }
                await WindowsForwarding.DeletePortProxyAsync(runner, addr, port);
                remaining.Remove(r);
                Console.WriteLine($"  portproxy 제거: {r.Key}");
            }
            else if (WindowsForwarding.RemoveFirewallRule(r.Key, state.InstallationId))
            {
                remaining.Remove(r);
                Console.WriteLine($"  방화벽 규칙 제거: {r.Key}");
            }
            else
            {
                failed = true;
                Console.WriteLine($"  [CONFLICT] 방화벽 규칙 {r.Key}를 안전하게 식별하지 못해 제거하지 않았습니다.");
            }
        }
        store.Save(state with { ResourceOwnership = remaining, Phase = failed ? InstallPhase.RecoveryPlanned : InstallPhase.Planned });
        return failed ? App.ExitChecksFailed : App.ExitOk;
    }

    private static bool Confirm(out int exitCode)
    {
        exitCode = App.ExitOk;
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("[UserInputRequired] 적용 확인 입력을 받을 수 없습니다.");
            exitCode = App.ExitUserInputRequired;
            return false;
        }
        Console.WriteLine();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Console.Write($"위 변경을 진행할까요? {Confirmation.Prompt} ");
            switch (Confirmation.Parse(Console.ReadLine()))
            {
                case ConfirmAnswer.Yes:
                    return true;
                case ConfirmAnswer.No:
                    Console.WriteLine("취소했습니다. 변경하지 않았습니다.");
                    exitCode = App.ExitCanceled;
                    return false;
                default:
                    Console.WriteLine("Y 또는 n으로 입력하세요(Enter = Y).");
                    break;
            }
        }
        Console.WriteLine("확인 입력이 올바르지 않아 취소했습니다. 변경하지 않았습니다.");
        exitCode = App.ExitCanceled;
        return false;
    }

    private static void PrintPlan(ForwardingPlan plan, ForwardingFacts f)
    {
        Console.WriteLine();
        Console.WriteLine($"포워딩 계획: {plan.Summary}");
        Console.WriteLine($"  WSL 호스트 대역: {f.WslHostCidr?.ToString() ?? "없음"}, 대상 배너: {f.EndpointBanner ?? "없음"}");
        Console.WriteLine($"  대상 host key: {string.Join(", ", f.ObservedHostKeys.Select(k => $"{k.KeyType} {k.Fingerprint}"))}");
        Console.WriteLine($"  기대 host key: {f.ExpectedHostKey}");
        if (plan.PortProxy is { } p)
            Console.WriteLine($"  portproxy: {p.Kind} — {p.Reason}{(p.Existing is { } e ? $" (현재 {e.ConnectAddress}:{e.ConnectPort})" : "")}");
        Console.WriteLine($"  방화벽: {plan.Firewall}{(plan.Rule is { } r ? $" — '{r.Name}' TCP {r.LocalAddress}:{r.Port}, 원격 {r.RemoteCidr}, 프로필 {r.Profile}" : "")}");
        foreach (var w in plan.Warnings) Console.WriteLine($"  [WARN] {w}");
        foreach (var b in plan.Blockers) Console.WriteLine($"  [BLOCK] {b}");
        Console.WriteLine(plan.CanApply ? "  → 적용 가능" : "  → 적용 불가(차단 사유 해결 필요)");
    }

    private static string? DefaultListenAddress(EnvironmentSnapshot s)
    {
        var routed = s.Interfaces?.Where(i => i.IsUp && i.Gateways.Count > 0 && i.Ipv4.Count == 1).ToList();
        return routed is { Count: 1 } ? routed[0].Ipv4[0].Address : null;
    }

    private static string PlanHash(ForwardingPlan plan) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{plan.Summary}|{plan.PortProxy?.Kind}|{plan.Firewall}|{plan.Rule}")));

    private static string AppVersion() =>
        typeof(ForwardCommand).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
