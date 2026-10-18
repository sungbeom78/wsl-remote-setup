using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using WslRemoteSetup.Core;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Core.Ssh;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Probing;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

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
/// `forward plan|apply|remove` — 외부 접속을 WSL SSH로 전달하는 통로와 LAN 한정 방화벽 규칙(M4).
/// 화면에는 쉬운 말, 설치 ID·서버 키·규칙 이름 등 기술 값은 작업 기록에 남긴다(ADR 0006).
/// apply/remove는 관리자 창에서만 동작한다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ForwardCommand
{
    public static async Task<int> RunAsync(ForwardOptions o) => o.Action switch
    {
        "plan" => await PlanOrApplyAsync(o, apply: false),
        "apply" => await PlanOrApplyAsync(o, apply: true),
        "remove" => await RemoveAsync(),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("사용: forward plan            (일반 창: 현재 상태로 계획 확인)");
        Console.Error.WriteLine("      forward apply --wsl-ip <plan이 알려 준 값>   (관리자 창)");
        Console.Error.WriteLine("      forward remove          (관리자 창)");
        return App.ExitUsage;
    }

    private static async Task<int> PlanOrApplyAsync(ForwardOptions o, bool apply)
    {
        if (apply && !WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요.");
            return App.ExitUsage;
        }

        var history = Ui.OpenHistory(apply ? "SSH 연결 통로 적용" : "SSH 연결 통로 계획 확인");
        var runner = WindowsForwarding.Runner();
        var store = new StateStore(WindowsForwarding.StatePath);
        var loaded = store.Load();
        if (loaded.Outcome is LoadOutcome.Corrupt or LoadOutcome.UnsupportedVersion)
        {
            Ui.Blocked("설정 기록 파일을 읽을 수 없습니다. 기록을 지우지 않고 멈춥니다.");
            history.Note($"상태 파일 {loaded.Outcome}: {loaded.Error}");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }
        var state = loaded.State;
        var installationId = o.InstallationId ?? state?.InstallationId ?? Guid.NewGuid();
        if (state is not null && state.InstallationId != installationId)
        {
            Ui.Blocked("지정한 설치 정보가 이 PC의 기록과 다릅니다.");
            history.Finish("중단: 설치 ID 불일치");
            return App.ExitChecksFailed;
        }

        Console.WriteLine("  현재 상태를 확인하는 중입니다(아무것도 바꾸지 않습니다)...");
        history.Begin("현재 상태 확인");
        var hostKey = o.HostKey ?? state?.SshHostKey;
        if (hostKey is null && !apply) hostKey = await ReadRecordedHostKeyAsync(runner, o.Distro);
        if (!HostKeyFingerprint.IsWellFormed(hostKey))
        {
            Ui.Blocked("WSL SSH 서버 확인 정보를 찾지 못했습니다. WSL에 SSH 서버를 먼저 설치해 주세요.");
            history.Fail("WSL SSH 서버 키 기록 없음");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }

        var snapshot = await new WindowsProbe(runner).CollectAsync();
        var listen = o.ListenAddress ?? DefaultListenAddress(snapshot);
        if (listen is null)
        {
            Ui.Blocked("외부에서 접속할 이 PC의 주소를 정하지 못했습니다(연결된 네트워크가 없거나 여러 개).");
            history.Finish("중단: 수신 주소 미정");
            return App.ExitUsage;
        }

        IPAddress? target;
        if (o.WslAddress is not null)
        {
            if (!InputValidator.TryParseIPv4(o.WslAddress, out var t)) { Ui.Blocked("WSL 주소 형식이 올바르지 않습니다."); return App.ExitUsage; }
            target = t;
        }
        else if (apply)
        {
            Ui.Blocked("관리자 창에서는 WSL을 직접 실행하지 않습니다. 먼저 일반 창에서 'forward plan'을 실행하고, 안내된 명령을 관리자 창에서 실행해 주세요.");
            history.Finish("중단: WSL 주소 미지정");
            return App.ExitUsage;
        }
        else
        {
            target = await WindowsForwarding.ReadWslAddressAsync(runner, o.Distro);
            if (target is null) { Ui.Blocked("WSL 주소를 확인하지 못했습니다. WSL이 실행되는지 확인해 주세요."); history.Finish("중단"); return App.ExitChecksFailed; }
        }

        using var id = WindowsIdentity.GetCurrent();
        var facts = await CollectFactsAsync(runner, snapshot, installationId, id.User!.Value, listen, o.ListenPort, target, hostKey!, state);
        var plan = ForwardingPlanBuilder.Build(facts);
        LogPlan(history, plan, facts);
        history.Done(plan.CanApply ? "진행 가능" : "진행 불가");
        PrintPlan(plan, facts, apply);

        if (!apply)
        {
            if (plan.CanApply && plan.HasChanges)
            {
                Console.WriteLine();
                Ui.Line("적용하려면 관리자 창에서 아래 명령을 실행하세요:");
                Ui.Line($"  WslRemoteSetup.exe forward apply --wsl-ip {target}");
            }
            history.Finish("계획 확인(변경 없음)");
            Ui.HistoryHint(history);
            return plan.CanApply ? App.ExitOk : App.ExitChecksFailed;
        }

        if (!plan.CanApply) { history.Finish("진행 불가"); Ui.HistoryHint(history); return App.ExitChecksFailed; }
        if (!plan.HasChanges)
        {
            Ui.Line("바꿀 것이 없습니다. 연결 상태만 확인합니다.");
            var verified = await VerifyAsync(runner, facts, history);
            history.Finish(verified ? "변경 없음, 확인 통과" : "변경 없음, 확인 실패");
            Ui.HistoryHint(history);
            return verified ? App.ExitOk : App.ExitChecksFailed;
        }
        if (!ConfirmPrompt.Ask(out var confirmExit)) { history.Finish("사용자가 취소함(변경 없음)"); return confirmExit; }
        history.Note("사용자가 [Y/n] 확인에서 진행을 선택함");

        WindowsForwarding.EnsureProtectedStateDirectory();
        var now = DateTimeOffset.UtcNow;
        state ??= new InstallState { InstallationId = installationId, AppVersion = AppVersion() };
        var resources = state.ResourceOwnership.ToList();
        var before = new Dictionary<string, string>(state.BeforeSnapshots);
        var key = string.Create(CultureInfo.InvariantCulture, $"{listen}:{o.ListenPort}");
        var total = (plan.PortProxy!.Kind is ReconcileKind.Add or ReconcileKind.Update ? 1 : 0) + (plan.Firewall == FirewallActionKind.Create ? 1 : 0);
        var n = 0;
        Console.WriteLine();
        try
        {
            if (plan.PortProxy.Kind is ReconcileKind.Add or ReconcileKind.Update)
            {
                Ui.Progress(++n, total, plan.PortProxy.Kind == ReconcileKind.Add ? "연결 전달 규칙 추가" : "연결 전달 규칙의 WSL 주소 갱신", () =>
                {
                    before.TryAdd($"portproxy:{key}", plan.PortProxy.Existing is { } ex ? $"{ex.ConnectAddress}:{ex.ConnectPort}" : "(none)");
                    history.Detail("규칙", $"{key} → {plan.PortProxy.Desired!.ConnectAddress}:{plan.PortProxy.Desired.ConnectPort}");
                    history.Detail("이전 값", before[$"portproxy:{key}"]);
                    WindowsForwarding.ApplyPortProxyAsync(runner, plan.PortProxy).GetAwaiter().GetResult();
                    resources.RemoveAll(r => r.Kind == ResourceKinds.PortProxy && r.Key == key);
                    resources.Add(new OwnedResource(ResourceKinds.PortProxy, key, "Windows → WSL SSH", now)
                    {
                        Value = string.Create(CultureInfo.InvariantCulture, $"{plan.PortProxy.Desired.ConnectAddress}:{plan.PortProxy.Desired.ConnectPort}"),
                    });
                    history.Done("적용");
                }, history);
            }
            if (plan.Firewall == FirewallActionKind.Create)
            {
                Ui.Progress(++n, total, "방화벽 허용 규칙 추가(같은 공유기의 기기만)", () =>
                {
                    history.Detail("규칙 이름", plan.Rule!.Name);
                    history.Detail("내용", $"TCP {plan.Rule.LocalAddress}:{plan.Rule.Port}, 원격 {plan.Rule.RemoteCidr}, 프로필 {plan.Rule.Profile}");
                    WindowsForwarding.CreateFirewallRule(plan.Rule);
                    resources.RemoveAll(r => r.Kind == ResourceKinds.FirewallRule && r.Key == plan.Rule.Name);
                    resources.Add(new OwnedResource(ResourceKinds.FirewallRule, plan.Rule.Name, plan.Rule.Description, now)
                    {
                        Value = $"TCP {plan.Rule.LocalAddress}:{plan.Rule.Port} from {plan.Rule.RemoteCidr} ({plan.Rule.Profile})",
                    });
                    history.Done("적용");
                }, history);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Ui.Blocked(ex.Message);
        }
        finally
        {
            store.Save(state with
            {
                Phase = InstallPhase.Verifying,
                OwnerSid = id.User!.Value,
                DistroName = o.Distro,
                SshHostKey = hostKey,
                ResourceOwnership = resources,
                BeforeSnapshots = before,
                ApprovalHistory = [.. state.ApprovalHistory, new ApprovalRecord(now, id.User!.Value, PlanHash(plan), ["portproxy", "firewall-ssh"])],
            });
        }

        var ok = await VerifyAsync(runner, facts, history);
        var latest = store.Load().State!;
        store.Save(latest with
        {
            Phase = ok ? InstallPhase.AwaitingAuthenticationSetup : InstallPhase.Failed,
            PendingAction = ok ? "다른 PC에서 실제 SSH 로그인 확인" : null,
            LastError = ok ? null : "연결 통로 사후 확인 실패",
            VerificationResults = [.. latest.VerificationResults, new VerificationRecord("forward-local", ok ? "PASS" : "FAIL", key, DateTimeOffset.UtcNow)],
        });
        history.Finish(ok ? "성공" : "실패");
        Ui.HistoryHint(history);
        return ok ? App.ExitOk : App.ExitChecksFailed;
    }

    /// <summary>WSL 안 SSH 설치 기록(root 소유)에서 ED25519 서버 키 지문을 읽는다. 일반(소유자) 창에서만 사용.</summary>
    internal static async Task<string?> ReadRecordedHostKeyAsync(CommandRunner runner, string distro)
    {
        var r = await runner.RunAsync(new CommandSpec(WindowsForwarding.Wsl,
            ["--distribution", distro, "--exec", "/bin/cat", "/var/lib/wsl-remote-setup/ssh.json"]) { Timeout = TimeSpan.FromSeconds(60) });
        if (!r.ExitedZero) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StandardOutput);
            if (!doc.RootElement.TryGetProperty("hostKeyFingerprints", out var arr)) return null;
            foreach (var e in arr.EnumerateArray())
            {
                var s = e.GetString() ?? "";
                if (s.EndsWith("(ED25519)", StringComparison.Ordinal)) return s.Split(' ')[0];
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task<ForwardingFacts> CollectFactsAsync(
        ICommandRunner runner, EnvironmentSnapshot s, Guid installationId, string ownerSid,
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

    private static void LogPlan(OperationHistory h, ForwardingPlan plan, ForwardingFacts f)
    {
        h.Detail("설치 ID", f.InstallationId.ToString("D"));
        h.Detail("소유자 SID", f.OwnerSid);
        h.Detail("요약", plan.Summary);
        h.Detail("WSL 호스트 대역", f.WslHostCidr?.ToString());
        h.Detail("SSH 배너", f.EndpointBanner);
        h.Detail("관찰한 서버 키", string.Join(", ", f.ObservedHostKeys.Select(k => $"{k.KeyType} {k.Fingerprint}")));
        h.Detail("등록된 서버 키", f.ExpectedHostKey);
        h.Detail("연결 전달", plan.PortProxy is { } p ? $"{p.Kind}: {p.Reason}" : null);
        h.Detail("방화벽", plan.Rule is { } r ? $"{plan.Firewall}: {r.Name} / TCP {r.LocalAddress}:{r.Port} / {r.RemoteCidr} / {r.Profile}" : plan.Firewall.ToString());
        foreach (var w in plan.Warnings) h.Note($"주의: {w}");
        foreach (var b in plan.Blockers) h.Note($"진행 불가 사유: {b}");
    }

    private static void PrintPlan(ForwardingPlan plan, ForwardingFacts f, bool apply)
    {
        Ui.Title(apply ? "SSH 연결 통로 설정" : "SSH 연결 통로 계획 (아직 아무것도 바꾸지 않았습니다)");
        Ui.Line($"외부에서 접속할 주소: {f.ListenAddress}, {f.ListenPort}번");
        Ui.Line("연결될 곳: 이 PC 안의 WSL(Ubuntu) SSH 서버");
        var keyOk = f.ObservedHostKeys.Any(k => k.Fingerprint == f.ExpectedHostKey);
        Ui.Line($"WSL SSH 서버 확인: {(keyOk ? "처음 설치 때 등록한 서버와 같습니다" : "등록한 서버와 다르거나 확인하지 못했습니다")}");
        var lan = plan.Rule?.RemoteCidr is { } cidr ? LanRange(cidr) : "같은 공유기에 연결된 기기";
        Console.WriteLine();
        Ui.Line("할 일");
        Ui.Item(1, "연결 전달 규칙", plan.PortProxy?.Kind switch
        {
            ReconcileKind.Add => "새로 추가",
            ReconcileKind.Update => "WSL 주소 갱신",
            ReconcileKind.NoChange => "변경 없음",
            _ => "진행 불가",
        }, $"외부 {f.ListenPort}번으로 들어온 접속을 WSL SSH로 전달");
        Ui.Item(2, "방화벽 허용 규칙", plan.Firewall switch
        {
            FirewallActionKind.Create => "새로 추가",
            FirewallActionKind.NoChange => "변경 없음",
            _ => "진행 불가",
        }, $"{lan}만 허용, '개인 네트워크'로 연결됐을 때만");
        Console.WriteLine();
        Ui.Line("바뀌지 않는 것: 이 PC의 IP 주소·DNS·원격 데스크톱 설정·기존 방화벽 규칙");
        foreach (var w in plan.Warnings) Ui.Note(w);
        Console.WriteLine();
        if (plan.CanApply) Ui.Line(plan.HasChanges ? "확인 결과: 진행할 수 있습니다." : "확인 결과: 이미 설정되어 있습니다.");
        else foreach (var b in plan.Blockers) Ui.Blocked(b);
    }

    /// <summary>"192.168.1.0/24" → "같은 공유기에 연결된 기기(192.168.1.0~192.168.1.255)".</summary>
    private static string LanRange(string cidr)
    {
        if (!Core.Network.Ipv4Cidr.TryParse(cidr, out var c)) return "같은 공유기에 연결된 기기";
        var start = c.Network;
        var end = start | (c.PrefixLength == 0 ? uint.MaxValue : (uint.MaxValue >> c.PrefixLength));
        static string Ip(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
        return $"같은 공유기에 연결된 기기({Ip(start)}~{Ip(end)})";
    }

    private static async Task<bool> VerifyAsync(ICommandRunner runner, ForwardingFacts f, OperationHistory history)
    {
        Console.WriteLine();
        Ui.Line("설정 후 확인(이 PC 안에서):");
        history.Begin("설정 후 확인");
        var results = new List<CheckResult>();
        var snapshot = await new WindowsProbe(runner).CollectAsync();
        var proxy = snapshot.PortProxies?.FirstOrDefault(p => p.ListenAddress == f.ListenAddress && p.ListenPort == f.ListenPort);
        var proxyOk = proxy is not null && proxy.ConnectAddress == f.TargetAddress.ToString() && proxy.ConnectPort == f.ConnectPort;
        results.Add(new("FWD_RULE", "연결 전달 규칙", proxyOk ? CheckStatus.Pass : CheckStatus.Fail, proxyOk ? "있음" : "없음"));
        history.Detail("연결 전달 규칙", proxy is null ? "없음" : $"{proxy.ListenAddress}:{proxy.ListenPort} → {proxy.ConnectAddress}:{proxy.ConnectPort}");

        var name = ForwardingPlanBuilder.RuleName(f.InstallationId, f.ListenPort);
        var rules = snapshot.FirewallRules?.Where(r => r.Name == name).ToList() ?? [];
        var fwOk = rules.Count == 1 && rules[0].Enabled;
        results.Add(new("FWD_FIREWALL", "방화벽 허용 규칙", fwOk ? CheckStatus.Pass : CheckStatus.Fail, fwOk ? "있음" : $"{rules.Count}개"));
        history.Detail("방화벽 규칙", fwOk ? $"{name} [{rules[0].Profiles}/{rules[0].RemoteAddresses}]" : $"{name} {rules.Count}개");

        var listenIp = IPAddress.Parse(f.ListenAddress);
        var banner = await WindowsForwarding.ReadBannerAsync(listenIp, f.ListenPort, TimeSpan.FromSeconds(5));
        var bannerOk = banner?.StartsWith("SSH-2.0-", StringComparison.Ordinal) == true;
        results.Add(new("FWD_BANNER", $"{f.ListenPort}번으로 SSH 응답", bannerOk ? CheckStatus.Pass : CheckStatus.Fail, bannerOk ? "응답함" : "응답 없음"));
        history.Detail("SSH 배너", banner);

        var keys = await WindowsForwarding.ProbeHostKeyAsync(runner, listenIp, f.ListenPort);
        var keyOk = keys.Any(k => k.Fingerprint == f.ExpectedHostKey);
        results.Add(new("FWD_HOSTKEY", "응답한 서버가 등록한 WSL 서버인지", keyOk ? CheckStatus.Pass : CheckStatus.Fail, keyOk ? "같음" : "다르거나 확인 못 함"));
        history.Detail("경유 서버 키", string.Join(", ", keys.Select(k => k.Fingerprint)));

        results.Add(new("SSH_LAN_AUTH", "다른 PC에서 실제 로그인", CheckStatus.NotTested, "다른 PC에서 직접 확인해 주세요."));
        ConsoleReport.PrintChecks(results);
        var ok = results.All(r => r.Status != CheckStatus.Fail);
        if (ok) history.Done("모두 통과"); else history.Fail("일부 실패");
        return ok;
    }

    private static async Task<int> RemoveAsync()
    {
        if (!WindowsForwarding.IsElevated())
        {
            Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요.");
            return App.ExitUsage;
        }
        var history = Ui.OpenHistory("SSH 연결 통로 제거");
        var store = new StateStore(WindowsForwarding.StatePath);
        var loaded = store.Load();
        if (loaded.Outcome != LoadOutcome.Loaded) { Ui.Line("이 도구가 만든 연결 통로가 없습니다."); history.Finish("대상 없음"); return App.ExitOk; }
        var state = loaded.State!;
        var owned = state.ResourceOwnership.Where(r => r.Kind is ResourceKinds.PortProxy or ResourceKinds.FirewallRule).ToList();
        if (owned.Count == 0) { Ui.Line("제거할 연결 통로가 없습니다."); history.Finish("대상 없음"); return App.ExitOk; }

        Ui.Title("SSH 연결 통로 제거");
        Ui.Line("이 도구가 만든 것만 지웁니다.");
        var n = 0;
        foreach (var r in owned) Ui.Item(++n, r.Kind == ResourceKinds.PortProxy ? "연결 전달 규칙" : "방화벽 허용 규칙", "제거");
        foreach (var r in owned) history.Detail(r.Kind, $"{r.Key} {r.Value}");
        if (!ConfirmPrompt.Ask(out var confirmExit)) { history.Finish("사용자가 취소함"); return confirmExit; }

        var runner = WindowsForwarding.Runner();
        var snapshot = await new WindowsProbe(runner).CollectAsync();
        var remaining = state.ResourceOwnership.ToList();
        var failed = false;
        var i = 0;
        foreach (var r in owned)
        {
            history.Begin($"{r.Kind} 제거");
            history.Detail("대상", r.Key);
            Console.Write($"  [{++i}/{owned.Count}] {(r.Kind == ResourceKinds.PortProxy ? "연결 전달 규칙" : "방화벽 허용 규칙")} 제거 ... ");
            if (r.Kind == ResourceKinds.PortProxy)
            {
                var sep = r.Key.LastIndexOf(':');
                var addr = r.Key[..sep];
                var port = int.Parse(r.Key[(sep + 1)..], CultureInfo.InvariantCulture);
                var current = snapshot.PortProxies?.FirstOrDefault(p => p.ListenAddress == addr && p.ListenPort == port);
                if (current is null) { remaining.Remove(r); Console.WriteLine("이미 없음"); history.Skip("이미 없음"); continue; }
                if ($"{current.ConnectAddress}:{current.ConnectPort}" != r.Value)
                {
                    failed = true;
                    Console.WriteLine("건너뜀(다른 곳에서 바뀜)");
                    history.Skip($"외부 변경 감지: 현재 {current.ConnectAddress}:{current.ConnectPort}, 기록 {r.Value}");
                    continue;
                }
                await WindowsForwarding.DeletePortProxyAsync(runner, addr, port);
                remaining.Remove(r);
                Console.WriteLine("완료");
                history.Done("제거");
            }
            else if (WindowsForwarding.RemoveFirewallRule(r.Key, state.InstallationId))
            {
                remaining.Remove(r);
                Console.WriteLine("완료");
                history.Done("제거");
            }
            else
            {
                failed = true;
                Console.WriteLine("건너뜀(이 도구가 만든 규칙인지 확실하지 않음)");
                history.Skip("규칙을 안전하게 식별하지 못함");
            }
        }
        store.Save(state with { ResourceOwnership = remaining, Phase = failed ? InstallPhase.RecoveryPlanned : InstallPhase.Planned });
        history.Finish(failed ? "일부 건너뜀" : "완료");
        Ui.HistoryHint(history);
        return failed ? App.ExitChecksFailed : App.ExitOk;
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
