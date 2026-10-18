using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Core.Setup;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Probing;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Forwarding;

/// <summary>
/// SSH 외 추가 서비스 포트(예: web:8080:80). SSH와 같은 WSL 주소로 전달하고 같은 LAN 한정 방화벽을 만든다.
/// `forward apply --service …`, `forward add-service --service …`, `forward remove-service --name …`(관리자).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceForwarding
{
    /// <summary>"이름:외부포트:WSL포트" 형식 해석.</summary>
    public static ServiceForward? Parse(string spec)
    {
        var p = spec.Split(':');
        return p.Length == 3
            && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var listen)
            && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
            ? new ServiceForward(p[0].Trim().ToLowerInvariant(), listen, to)
            : null;
    }

    /// <summary>
    /// 서비스 규칙을 만든다(이미 이 도구 것이면 대상만 맞춤). 남의 규칙이나 사용 중인 포트면 건너뛰고 이유를 돌려준다.
    /// resources·services 목록을 갱신한다. 반환: (적용한 서비스, 건너뛴 이유 목록).
    /// </summary>
    public static (List<ServiceForward> Applied, List<string> Skipped) Apply(
        IReadOnlyList<ServiceForward> services, Guid installationId, string listen, string target, string lanCidr,
        List<OwnedResource> resources, CommandRunner runner, OperationHistory history)
    {
        var applied = new List<ServiceForward>();
        var skipped = new List<string>();
        var snapshot = new WindowsProbe(runner).CollectAsync().GetAwaiter().GetResult();
        var listening = snapshot.TcpListenPorts ?? [];
        var proxies = snapshot.PortProxies ?? [];
        foreach (var s in services)
        {
            history.Begin($"추가 서비스 {s.Name} ({s.ListenPort}번 → WSL {s.ConnectPort}번)");
            var key = string.Create(CultureInfo.InvariantCulture, $"{listen}:{s.ListenPort}");
            var owned = resources.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy && r.Key == key);
            var check = PortAdvisor.Check(s.ListenPort, listening, proxies.Select(p => p.ListenPort).ToList(), [], snapshot.Rdp?.Port, owned is null ? null : s.ListenPort);
            if (!check.Usable)
            {
                skipped.Add($"{s.Name}({s.ListenPort}번): {check.Reason}");
                history.Skip(check.Reason);
                continue;
            }
            var desired = new PortProxyRule(listen, s.ListenPort, target, s.ConnectPort);
            var existing = proxies.FirstOrDefault(p => p.ListenAddress == listen && p.ListenPort == s.ListenPort);
            var kind = existing is null ? ReconcileKind.Add : ReconcileKind.Update;
            if (existing is null || existing.ConnectAddress != target || existing.ConnectPort != s.ConnectPort)
                WindowsForwarding.ApplyPortProxyAsync(runner, new ReconcileAction(kind, desired, existing, "추가 서비스")).GetAwaiter().GetResult();
            resources.RemoveAll(r => r.Kind == ResourceKinds.PortProxy && r.Key == key);
            resources.Add(new OwnedResource(ResourceKinds.PortProxy, key, $"서비스 {s.Name}", DateTimeOffset.UtcNow)
            {
                Value = string.Create(CultureInfo.InvariantCulture, $"{target}:{s.ConnectPort}"),
            });

            var rule = ForwardingPlanBuilder.RuleName(installationId, s.ListenPort, s.Name);
            if (snapshot.FirewallRules?.Any(r => r.Name == rule) != true)
                WindowsForwarding.CreateFirewallRule(new FirewallRuleSpec(rule,
                    $"WslRemoteSetup installationId {installationId:D}: LAN {s.Name} → WSL", listen, s.ListenPort, lanCidr, FirewallProfiles.Private));
            resources.RemoveAll(r => r.Kind == ResourceKinds.FirewallRule && r.Key == rule);
            resources.Add(new OwnedResource(ResourceKinds.FirewallRule, rule, $"서비스 {s.Name}", DateTimeOffset.UtcNow)
            {
                Value = $"TCP {listen}:{s.ListenPort} from {lanCidr} (Private)",
            });
            history.Detail("규칙", $"{key} → {target}:{s.ConnectPort}, 방화벽 {rule}");
            history.Done("적용");
            applied.Add(s);
        }
        return (applied, skipped);
    }

    public static async Task<int> AddAsync(IReadOnlyList<ServiceForward> services)
    {
        if (!WindowsForwarding.IsElevated()) { Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요."); return App.ExitUsage; }
        if (services.Count == 0) { Console.Error.WriteLine("사용: forward add-service --service 이름:외부포트:WSL포트 (예: web:8080:80)"); return App.ExitUsage; }
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State;
        var ssh = state?.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy && (r.Description?.Contains("SSH", StringComparison.Ordinal) ?? false));
        if (state is null || ssh?.Value is null) { Ui.Blocked("SSH 연결 통로가 먼저 있어야 합니다(같은 WSL로 전달하기 때문)."); return App.ExitChecksFailed; }
        var sep = ssh.Key.LastIndexOf(':');
        var listen = ssh.Key[..sep];
        var sshPort = int.Parse(ssh.Key[(sep + 1)..], CultureInfo.InvariantCulture);
        var target = ssh.Value[..ssh.Value.LastIndexOf(':')];
        var merged = state.Services.Where(x => services.All(n => n.Name != x.Name)).Concat(services).ToList();
        var errors = ServiceForward.Validate(merged, sshPort);
        if (errors.Count > 0) { foreach (var e in errors) Ui.Blocked(e); return App.ExitUsage; }
        var lan = LanOf(listen) ?? "";

        var history = Ui.OpenHistory("추가 서비스 포트 열기");
        Ui.Title("추가 서비스 포트 열기");
        var n = 0;
        foreach (var s in services) Ui.Item(++n, $"{s.Name}", "새로 추가", $"외부 {s.ListenPort}번 → WSL {s.ConnectPort}번, 같은 공유기에 연결된 기기만 허용");
        Ui.Note("WSL 안에서 해당 프로그램(예: 웹 서버)이 실행 중이어야 실제로 접속됩니다.");
        if (!ConfirmPrompt.Ask(out var code)) { history.Finish("취소"); return code; }

        var resources = state.ResourceOwnership.ToList();
        var (applied, skipped) = Apply(services, state.InstallationId, listen, target, lan, resources, new CommandRunner([WindowsForwarding.Netsh, WindowsForwarding.Wsl]), history);
        var finalServices = state.Services.Where(x => applied.All(a => a.Name != x.Name)).Concat(applied).ToList();
        store.Save(state with { ResourceOwnership = resources, Services = finalServices });
        UpdatePolicyAndRestart(finalServices, history);
        foreach (var a in applied) Ui.Line($"✔ {a.Name}: 외부 {a.ListenPort}번 → WSL {a.ConnectPort}번");
        foreach (var s in skipped) Ui.Blocked(s);
        history.Finish(skipped.Count == 0 ? "완료" : "일부 건너뜀");
        Ui.HistoryHint(history);
        return skipped.Count == 0 ? App.ExitOk : App.ExitChecksFailed;
    }

    public static async Task<int> RemoveAsync(string? name)
    {
        if (!WindowsForwarding.IsElevated()) { Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요."); return App.ExitUsage; }
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State;
        var svc = state?.Services.FirstOrDefault(s => s.Name == name);
        if (state is null || svc is null) { Ui.Line($"'{name}' 서비스 기록이 없습니다."); return App.ExitOk; }

        var history = Ui.OpenHistory("추가 서비스 포트 닫기");
        Ui.Title("추가 서비스 포트 닫기");
        Ui.Item(1, svc.Name, "제거", $"외부 {svc.ListenPort}번 연결 통로와 방화벽 허용 규칙을 지웁니다. 다른 PC에서 이 서비스에 접속할 수 없게 됩니다.");
        if (!ConfirmPrompt.Ask(out var code)) { history.Finish("취소"); return code; }

        var runner = new CommandRunner([WindowsForwarding.Netsh]);
        var resources = state.ResourceOwnership.ToList();
        var proxy = resources.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy && r.Key.EndsWith(string.Create(CultureInfo.InvariantCulture, $":{svc.ListenPort}"), StringComparison.Ordinal));
        if (proxy is not null)
        {
            var addr = proxy.Key[..proxy.Key.LastIndexOf(':')];
            var current = WindowsProbe.ProbePortProxy().FirstOrDefault(p => p.ListenAddress == addr && p.ListenPort == svc.ListenPort);
            if (current is not null && $"{current.ConnectAddress}:{current.ConnectPort}" == proxy.Value)
                await WindowsForwarding.DeletePortProxyAsync(runner, addr, svc.ListenPort);
            resources.Remove(proxy);
        }
        var rule = ForwardingPlanBuilder.RuleName(state.InstallationId, svc.ListenPort, svc.Name);
        if (WindowsForwarding.RemoveFirewallRule(rule, state.InstallationId)) resources.RemoveAll(r => r.Kind == ResourceKinds.FirewallRule && r.Key == rule);
        var remaining = state.Services.Where(s => s.Name != svc.Name).ToList();
        store.Save(state with { ResourceOwnership = resources, Services = remaining });
        UpdatePolicyAndRestart(remaining, history);
        history.Finish("완료");
        Ui.Line($"✔ {svc.Name} 포트를 닫았습니다.");
        Ui.HistoryHint(history);
        return App.ExitOk;
    }

    /// <summary>자동 실행이 설치되어 있으면 보호 정책의 서비스 목록을 고치고 백그라운드 프로그램을 다시 시작한다.</summary>
    private static void UpdatePolicyAndRestart(IReadOnlyList<ServiceForward> services, OperationHistory history)
    {
        if (!File.Exists(ProtectedPaths.PolicyFile) || !ProtectedPaths.IsAdminOnlyWritable(ProtectedPaths.PolicyFile, out _)) return;
        var policy = ForwardingPolicy.Parse(File.ReadAllText(ProtectedPaths.PolicyFile)) with { Services = services };
        File.WriteAllText(ProtectedPaths.PolicyFile, System.Text.Json.JsonSerializer.Serialize(policy, StateStore.JsonOptions), new UTF8Encoding(false));
        var sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        var r = new CommandRunner([sc]);
        r.RunAsync(new CommandSpec(sc, ["stop", ProtectedPaths.ServiceName]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
        Thread.Sleep(3000);
        r.RunAsync(new CommandSpec(sc, ["start", ProtectedPaths.ServiceName]) { Timeout = TimeSpan.FromSeconds(30) }).GetAwaiter().GetResult();
        history.Note("자동 실행 정책의 서비스 목록을 갱신하고 백그라운드 프로그램을 다시 시작함");
    }

    private static string? LanOf(string listen)
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            var ua = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.ToString() == listen);
            if (ua is not null) return Core.Network.Ipv4Cidr.FromAddress(ua.Address, ua.PrefixLength).ToString();
        }
        return null;
    }
}
