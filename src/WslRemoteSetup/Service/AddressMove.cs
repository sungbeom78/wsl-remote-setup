using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Core.Reporting;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Probing;

namespace WslRemoteSetup.Service;

/// <summary>
/// DHCP 주소 변경 따라가기 적용(ADR 0007). 판단은 Core의 AddressFollower가 하고, 여기서는 이 도구 소유 리소스만 옮긴다.
/// 연결 전달 규칙(같은 대상·포트), 방화벽 규칙의 로컬 주소, 정책·상태 파일의 접속 주소.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AddressMove
{
    public static IReadOnlyList<AdapterView> ObserveAdapters()
    {
        var categories = WindowsProbe.ProbeNetworkCategories();
        var list = new List<AdapterView>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            int? index = null;
            try { index = props.GetIPv4Properties().Index; } catch (NetworkInformationException) { }
            var v4 = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => new Ipv4AddressInfo(u.Address.ToString(), u.PrefixLength))
                .ToList();
            list.Add(new AdapterView(ni.Id, ni.OperationalStatus == OperationalStatus.Up,
                index is int i && categories.TryGetValue(i, out var c) ? c : NetworkCategory.Unknown, v4));
        }
        return list;
    }

    public static ForwardingPolicy Apply(ForwardingPolicy policy, string newAddress, StateStore store, CommandRunner runner, ServiceLog log)
    {
        var history = new OperationHistory(Path.Combine(ProtectedPaths.LogDirectory, "history.log"), "접속 주소 자동 이동", "SYSTEM(백그라운드 연결 관리 프로그램)");
        var oldKey = string.Create(CultureInfo.InvariantCulture, $"{policy.ListenAddress}:{policy.ListenPort}");
        var newKey = string.Create(CultureInfo.InvariantCulture, $"{newAddress}:{policy.ListenPort}");
        history.Detail("이전 주소", oldKey);
        history.Detail("새 주소", newKey);

        var state = store.Load().State ?? throw new InvalidOperationException("상태 파일이 없습니다.");
        var owned = state.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy && r.Key == oldKey)
            ?? throw new InvalidOperationException("이 도구가 만든 연결 전달 규칙 기록이 없습니다.");
        var target = owned.Value ?? throw new InvalidOperationException("연결 전달 대상 기록이 없습니다.");
        var sep = target.LastIndexOf(':');
        var connectAddress = target[..sep];
        var connectPort = int.Parse(target[(sep + 1)..], CultureInfo.InvariantCulture);

        // 1. 연결 전달 규칙: 기록과 같은 이전 규칙만 지우고, 같은 대상으로 새 주소 규칙을 만든다.
        history.Begin("연결 전달 규칙 이동");
        var current = WindowsProbe.ProbePortProxy();
        if (current.Any(p => p.ListenPort == policy.ListenPort && p.ListenAddress == newAddress))
            throw new InvalidOperationException($"{newKey}에 이미 다른 연결 전달 규칙이 있어 옮기지 않습니다.");
        var old = current.FirstOrDefault(p => p.ListenAddress == policy.ListenAddress && p.ListenPort == policy.ListenPort);
        if (old is not null)
        {
            if (old.ConnectAddress != connectAddress || old.ConnectPort != connectPort)
                throw new InvalidOperationException("이전 규칙이 기록과 달라(외부 변경) 옮기지 않습니다.");
            WindowsForwarding.DeletePortProxyAsync(runner, policy.ListenAddress, policy.ListenPort).GetAwaiter().GetResult();
        }
        WindowsForwarding.ApplyPortProxyAsync(runner, new ReconcileAction(ReconcileKind.Add,
            new PortProxyRule(newAddress, policy.ListenPort, connectAddress, connectPort), null, "주소 이동")).GetAwaiter().GetResult();
        history.Done($"{newKey} → {target}");

        // 2. 방화벽 규칙: 로컬 주소만 바꾼다(원격 범위·프로필 유지).
        history.Begin("방화벽 규칙의 로컬 주소 변경");
        var ruleName = ForwardingPlanBuilder.RuleName(policy.InstallationId, policy.ListenPort);
        if (!WindowsForwarding.UpdateFirewallLocalAddress(ruleName, policy.InstallationId, newAddress))
            history.Fail("이 도구가 만든 규칙을 하나로 특정하지 못해 바꾸지 않음");
        else history.Done(ruleName);

        // 3. 정책·상태
        history.Begin("정책·상태 갱신");
        var moved = policy with { ListenAddress = newAddress };
        File.WriteAllText(ProtectedPaths.PolicyFile, System.Text.Json.JsonSerializer.Serialize(moved, StateStore.JsonOptions), new UTF8Encoding(false));
        var resources = state.ResourceOwnership.Select(r =>
            r.Kind == ResourceKinds.PortProxy && r.Key == oldKey ? r with { Key = newKey }
            : r.Kind == ResourceKinds.FirewallRule && r.Key == ruleName ? r with { Value = r.Value?.Replace(policy.ListenAddress, newAddress, StringComparison.Ordinal) }
            : r).ToList();
        store.Save(state with { ResourceOwnership = resources });
        history.Done("완료");
        history.Finish("성공");

        log.Write("INFO", $"접속 주소 이동: {oldKey} → {newKey} (같은 네트워크 카드·같은 공유기 범위)");
        return moved;
    }
}
