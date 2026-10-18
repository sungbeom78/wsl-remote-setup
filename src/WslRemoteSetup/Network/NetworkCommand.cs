using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Network;

/// <summary>
/// `network plan|apply|revert` — 이 PC 주소 고정(고급, 후속 범위). 기본은 지금 주소를 그대로 고정한다.
/// apply는 관리자 창에서 실행하며, 주소가 바뀌면 5분 안에 새 주소로 다시 접속해 Enter를 눌러야 유지되고,
/// 그렇지 않으면 자동으로 원래(자동 할당)로 되돌린다. revert는 이 도구가 고정한 주소만 자동 할당으로 되돌린다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NetworkCommand
{
    private static readonly string Netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromMinutes(5);
    private const string Kind = "static-ip";

    public static async Task<int> RunAsync(string action, string? requestedIp) => action switch
    {
        "plan" => await PlanOrApplyAsync(requestedIp, apply: false),
        "apply" => await PlanOrApplyAsync(requestedIp, apply: true),
        "revert" => await RevertAsync(),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("사용: network plan [--ip 주소] | network apply [--ip 주소] | network revert   (apply/revert는 관리자 창)");
        return App.ExitUsage;
    }

    private static async Task<int> PlanOrApplyAsync(string? requestedIp, bool apply)
    {
        if (apply && !WindowsForwarding.IsElevated()) { Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요."); return App.ExitUsage; }
        var history = Ui.OpenHistory(apply ? "이 PC 주소 고정 적용" : "이 PC 주소 고정 계획");
        var adapter = FindRoutedAdapter();
        if (adapter is null)
        {
            Ui.Blocked("인터넷에 연결된 네트워크 카드를 하나로 정하지 못했습니다(없거나 여러 개).");
            history.Finish("중단");
            return App.ExitChecksFailed;
        }
        var inUse = requestedIp is not null && requestedIp != adapter.Address && await RespondsAsync(requestedIp);
        var plan = StaticIpPlanner.Plan(adapter, requestedIp, inUse);
        history.Begin("계획");
        history.Detail("카드", $"{adapter.Name} (index {adapter.InterfaceIndex}), DHCP={adapter.Dhcp}");
        history.Detail("현재", $"{adapter.Address}/{adapter.PrefixLength} GW {adapter.Gateway} DNS {string.Join(",", adapter.Dns)}");
        history.Detail("고정 값", $"{plan.NewAddress} {plan.Mask} GW {plan.Gateway} DNS {string.Join(",", plan.Dns)}");
        foreach (var b in plan.Blockers) history.Note($"진행 불가: {b}");
        history.Done(plan.CanApply ? "진행 가능" : "진행 불가");

        Ui.Title(apply ? "이 PC 주소 고정" : "이 PC 주소 고정 계획 (아직 아무것도 바꾸지 않았습니다)");
        Ui.Line($"네트워크: {adapter.Name}");
        Ui.Line($"지금: {adapter.Address} ({(adapter.Dhcp ? "공유기가 자동으로 정한 주소" : "고정 주소")})");
        Ui.Line($"바꿀 값: {plan.NewAddress} 고정 (공유기 {plan.Gateway}, DNS {string.Join(", ", plan.Dns)}는 지금 값 유지)");
        foreach (var w in plan.Warnings) Ui.Note(w);
        Ui.Note("되돌리기: 관리자 창에서 'network revert' (자동 할당으로 돌아감)");
        if (!plan.CanApply) { foreach (var b in plan.Blockers) Ui.Blocked(b); history.Finish("진행 불가"); return App.ExitChecksFailed; }
        if (!apply)
        {
            Console.WriteLine();
            Ui.Line("적용하려면 관리자 창에서: WslRemoteSetup.exe network apply" + (requestedIp is null ? "" : $" --ip {requestedIp}"));
            history.Finish("계획 확인(변경 없음)");
            Ui.HistoryHint(history);
            return App.ExitOk;
        }
        if (!ConfirmPrompt.Ask(out var code)) { history.Finish("취소"); return code; }

        var runner = new CommandRunner([Netsh]);
        var store = new StateStore(ProtectedPaths.StateFile);
        WindowsForwarding.EnsureProtectedStateDirectory();
        var state = store.Load().State ?? new InstallState { AppVersion = "0.1.0" };
        var key = adapter.InterfaceIndex.ToString(CultureInfo.InvariantCulture);
        var before = new Dictionary<string, string>(state.BeforeSnapshots) { [$"network:{key}"] = $"dhcp ({adapter.Address})" };
        var resources = state.ResourceOwnership.Where(r => !(r.Kind == Kind && r.Key == key)).ToList();
        resources.Add(new OwnedResource(Kind, key, $"{adapter.Name} 주소 고정", DateTimeOffset.UtcNow) { Value = plan.NewAddress });
        store.Save(state with { BeforeSnapshots = before, ResourceOwnership = resources });

        history.Begin("주소 고정 적용");
        try
        {
            await SetStaticAsync(runner, adapter.InterfaceIndex, plan);
        }
        catch (InvalidOperationException ex)
        {
            history.Fail(ex.Message);
            Ui.Blocked(ex.Message);
            await RevertNetworkAsync(runner, adapter.InterfaceIndex, history);
            RemoveRecord(store, key);
            return App.ExitChecksFailed;
        }

        // 적용 확인: 새 주소가 붙고 공유기가 응답하는지
        var healthy = await WaitHealthyAsync(plan.NewAddress, plan.Gateway);
        history.Detail("적용 후 확인", healthy ? "주소·공유기 응답 정상" : "공유기 응답 없음");
        if (!healthy)
        {
            Ui.Blocked("고정 후 공유기와 통신하지 못해 원래대로 되돌립니다.");
            await RevertNetworkAsync(runner, adapter.InterfaceIndex, history);
            RemoveRecord(store, key);
            history.Finish("자동 원복");
            return App.ExitChecksFailed;
        }

        if (plan.AddressChanges)
        {
            Console.WriteLine();
            Ui.Line($"주소를 {plan.NewAddress}(으)로 바꿨습니다. 원격 데스크톱이 끊겼다면 새 주소로 다시 접속하세요.");
            Ui.Line($"이 창에서 {ConfirmWindow.TotalMinutes:F0}분 안에 Enter를 누르면 유지합니다. 누르지 않으면 원래(자동 할당)로 되돌립니다.");
            Console.Write("  유지하려면 Enter: ");
            var read = Task.Run(Console.ReadLine);
            if (await Task.WhenAny(read, Task.Delay(ConfirmWindow)) != read)
            {
                Console.WriteLine();
                Ui.Line("확인이 없어 원래대로 되돌립니다.");
                await RevertNetworkAsync(runner, adapter.InterfaceIndex, history);
                RemoveRecord(store, key);
                history.Finish("확인 없음 → 자동 원복");
                return App.ExitCanceled;
            }
            history.Note("사용자가 새 주소에서 유지 확인");
            Ui.Note("SSH 연결 통로가 예전 주소에 맞춰져 있다면 'install'을 다시 실행해 새 주소로 맞추세요(주소 따라가기를 켰다면 자동으로 옮겨집니다).");
        }
        history.Done("고정 완료");
        history.Finish("성공");
        Ui.Line($"✔ 이 PC 주소를 {plan.NewAddress}(으)로 고정했습니다.");
        Ui.HistoryHint(history);
        return App.ExitOk;
    }

    private static async Task<int> RevertAsync()
    {
        if (!WindowsForwarding.IsElevated()) { Console.Error.WriteLine("관리자 권한이 필요합니다. 관리자 창(UAC 승인)에서 다시 실행해 주세요."); return App.ExitUsage; }
        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State;
        var owned = state?.ResourceOwnership.FirstOrDefault(r => r.Kind == Kind);
        if (owned is null) { Ui.Line("이 도구가 고정한 주소가 없습니다."); return App.ExitOk; }
        var history = Ui.OpenHistory("이 PC 주소 고정 해제");
        Ui.Title("이 PC 주소 고정 해제");
        Ui.Line($"고정한 주소 {owned.Value}를 공유기 자동 할당으로 되돌립니다.");
        Ui.Note("주소가 바뀌면 원격 연결이 끊기고, 다른 PC의 접속 주소도 바꿔야 할 수 있습니다.");
        if (!ConfirmPrompt.Ask(out var code)) { history.Finish("취소"); return code; }
        await RevertNetworkAsync(new CommandRunner([Netsh]), int.Parse(owned.Key, CultureInfo.InvariantCulture), history);
        RemoveRecord(store, owned.Key);
        history.Finish("완료");
        Ui.Line("✔ 자동 할당으로 되돌렸습니다.");
        return App.ExitOk;
    }

    private static AdapterAddressing? FindRoutedAdapter()
    {
        var found = new List<AdapterAddressing>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (ni.Name.StartsWith("vEthernet", StringComparison.Ordinal)) continue;
            var p = ni.GetIPProperties();
            var gw = p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))?.Address.ToString();
            var v4 = p.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)).ToList();
            if (gw is null || v4.Count != 1) continue;
            IPv4InterfaceProperties v4p;
            try { v4p = p.GetIPv4Properties(); } catch (NetworkInformationException) { continue; }
            found.Add(new AdapterAddressing(v4p.Index, ni.Name, v4p.IsDhcpEnabled, v4[0].Address.ToString(), v4[0].PrefixLength, gw,
                p.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList()));
        }
        return found.Count == 1 ? found[0] : null;
    }

    private static async Task<bool> RespondsAsync(string ip)
    {
        if (!InputValidator.TryParseIPv4(ip, out var addr)) return false;
        using var ping = new Ping();
        for (var i = 0; i < 2; i++)
        {
            try { if ((await ping.SendPingAsync(addr, 1000)).Status == IPStatus.Success) return true; }
            catch (PingException) { }
        }
        return false;
    }

    private static async Task SetStaticAsync(CommandRunner runner, int index, StaticIpPlan plan)
    {
        var idx = index.ToString(CultureInfo.InvariantCulture);
        await Must(runner, ["interface", "ipv4", "set", "address", $"name={idx}", "static", plan.NewAddress, plan.Mask, plan.Gateway]);
        await Must(runner, ["interface", "ipv4", "set", "dnsservers", $"name={idx}", "static", plan.Dns[0], "primary", "validate=no"]);
        for (var i = 1; i < plan.Dns.Count; i++)
            await Must(runner, ["interface", "ipv4", "add", "dnsservers", $"name={idx}", plan.Dns[i], $"index={i + 1}", "validate=no"]);
    }

    private static async Task RevertNetworkAsync(CommandRunner runner, int index, OperationHistory history)
    {
        var idx = index.ToString(CultureInfo.InvariantCulture);
        history.Begin("자동 할당으로 되돌리기");
        var a = await runner.RunAsync(new CommandSpec(Netsh, ["interface", "ipv4", "set", "address", $"name={idx}", "source=dhcp"]));
        var d = await runner.RunAsync(new CommandSpec(Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={idx}", "source=dhcp"]));
        history.Detail("결과", $"address exit={a.ExitCode}, dns exit={d.ExitCode}");
        if (a.ExitedZero && d.ExitedZero) history.Done("되돌림"); else history.Fail("일부 실패");
    }

    private static async Task<bool> WaitHealthyAsync(string address, string gateway)
    {
        for (var i = 0; i < 15; i++)
        {
            var present = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString() == address));
            if (present && await RespondsAsync(gateway)) return true;
            await Task.Delay(1000);
        }
        return false;
    }

    private static void RemoveRecord(StateStore store, string key)
    {
        var s = store.Load().State;
        if (s is null) return;
        store.Save(s with { ResourceOwnership = s.ResourceOwnership.Where(r => !(r.Kind == Kind && r.Key == key)).ToList() });
    }

    private static async Task Must(CommandRunner runner, IReadOnlyList<string> args)
    {
        var r = await runner.RunAsync(new CommandSpec(Netsh, args) { Timeout = TimeSpan.FromSeconds(60) });
        if (!r.ExitedZero) throw new InvalidOperationException($"주소 설정 실패(종료코드 {r.ExitCode}): {r.StandardOutput.Trim()}");
    }
}
