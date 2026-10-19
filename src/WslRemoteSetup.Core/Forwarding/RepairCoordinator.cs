using System.Net;
using WslRemoteSetup.Core.Ipc;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Policy;

namespace WslRemoteSetup.Core.Forwarding;

/// <summary>네트워크 관리부가 직접 관찰·변경하는 Windows 측 기능. 실제 구현은 서비스 모드(M4/M5)에서 제공한다.</summary>
public interface IForwardingHost
{
    /// <summary>WSL 호스트 가상 어댑터(vEthernet WSL)의 IPv4 대역. 찾지 못하면 null.</summary>
    Ipv4Cidr? GetHostWslCidr();
    IReadOnlyList<PortProxyRule> GetPortProxies();
    /// <summary>대상 endpoint 확인(TCP 연결, M4부터 SSH host key 지문 비교 포함).</summary>
    bool CheckEndpoint(IPAddress address, int port);
    /// <summary>Add/Update만 전달된다. 실패하면 예외를 던진다.</summary>
    void Apply(ReconcileAction action);
    /// <summary>해당 주소·포트에서 실제로 TCP 연결을 받고 있는지(설정 존재와 별개).</summary>
    bool IsListening(string address, int port);
    /// <summary>같은 tuple을 지우고 다시 추가해 수신을 되살린다. 이 도구가 소유한 tuple에만 호출된다.</summary>
    void Reapply(PortProxyRule rule);
}

/// <summary>
/// 요청 1건 처리: 검증 → endpoint 확인 → 조정 → 적용. 정책·관찰값 외의 입력으로 동작을 바꾸지 않는다.
/// 마지막 적용 대상은 소유권 기록(resourceOwnership)으로 영속화해야 하며, 여기서는 생성자로 받는다.
/// </summary>
public sealed class RepairCoordinator(
    ForwardingPolicy policy,
    IForwardingHost host,
    TimeProvider time,
    string? recordedConnectAddress = null,
    DateTimeOffset? lastUpdateAt = null)
{
    private readonly Lock _gate = new();

    public string? RecordedConnectAddress { get; private set; } = recordedConnectAddress;
    public DateTimeOffset? LastUpdateAt { get; private set; } = lastUpdateAt;

    public RepairResponse Handle(string? callerSid, RepairRequest? request, string parseError)
    {
        if (request is null) return new(RepairCode.Denied, $"요청 거부: {parseError}");

        lock (_gate)
        {
            var now = time.GetUtcNow();
            var decision = RepairRequestValidator.Evaluate(policy, request, callerSid, host.GetHostWslCidr(), now, LastUpdateAt);
            if (decision.Code == RepairCode.RateLimited)
            {
                // 간격 제한 중이라도 이미 같은 대상이면 정상 응답한다(수신 상태는 확인).
                var same = PortProxyReconciler.Decide(policy, host.GetPortProxies(), RecordedConnectAddress, request.ObservedWslIPv4);
                if (same.Kind != ReconcileKind.NoChange) return new(decision.Code, decision.Reason);
                // SSH 대상이 같으면 추가 서비스도 같은 대상으로 맞춘다('지금 연결'이 간격 제한에 걸려도 빠진 포트를 채움, ADR 0011).
                var limited = ListeningOrRepaired(same.Existing!, same.Reason);
                var synced = SyncServices(request.ObservedWslIPv4, RecordedConnectAddress);
                return synced > 0 && limited.Code == RepairCode.NoChange ? new(RepairCode.Updated, $"추가 서비스 {synced}개 갱신") : limited;
            }
            if (!decision.Allowed) return new(decision.Code, decision.Reason);

            var action = PortProxyReconciler.Decide(policy, host.GetPortProxies(), RecordedConnectAddress, request.ObservedWslIPv4);
            switch (action.Kind)
            {
                case ReconcileKind.NoChange:
                {
                    RecordedConnectAddress = action.Desired!.ConnectAddress;
                    var r = ListeningOrRepaired(action.Existing!, action.Reason);
                    var changed = SyncServices(request.ObservedWslIPv4, RecordedConnectAddress);
                    return changed > 0 && r.Code == RepairCode.NoChange ? new(RepairCode.Updated, $"추가 서비스 {changed}개 갱신") : r;
                }
                case ReconcileKind.Conflict:
                    return new(RepairCode.Conflict, action.Reason);
            }

            if (!host.CheckEndpoint(request.ObservedWslIPv4, policy.ConnectPort))
                return new(RepairCode.EndpointUnreachable, "대상 SSH endpoint를 확인하지 못했습니다.");

            var before = RecordedConnectAddress;
            host.Apply(action);
            RecordedConnectAddress = action.Desired!.ConnectAddress;
            LastUpdateAt = now;
            // SSH 서버 키로 대상 WSL을 확인했으므로, 같은 주소로 추가 서비스도 옮긴다.
            SyncServices(request.ObservedWslIPv4, before);
            return new(RepairCode.Updated, action.Kind == ReconcileKind.Add ? "포워딩 추가" : "포워딩 대상 갱신");
        }
    }

    /// <summary>
    /// 추가 서비스 규칙을 SSH와 같은 대상으로 맞춘다. 남이 바꾼 규칙(Conflict)은 건드리지 않고 건너뛴다(SSH는 막지 않음).
    /// 돌려주는 값은 바꾼 규칙 수.
    /// </summary>
    private int SyncServices(System.Net.IPAddress target, string? recordedBefore)
    {
        var changed = 0;
        foreach (var s in policy.Services)
        {
            var sp = policy.ForService(s);
            var a = PortProxyReconciler.Decide(sp, host.GetPortProxies(), recordedBefore, target);
            switch (a.Kind)
            {
                case ReconcileKind.Add or ReconcileKind.Update:
                    host.Apply(a);
                    changed++;
                    break;
                case ReconcileKind.NoChange when !host.IsListening(sp.ListenAddress, sp.ListenPort):
                    host.Reapply(a.Existing!);
                    changed++;
                    break;
            }
        }
        return changed;
    }

    /// <summary>
    /// 요청과 무관하게 소유한 규칙이 실제로 수신 중인지 확인하고, 끊겨 있으면 같은 값으로 다시 적용한다.
    /// 부팅 시 IP Helper가 수신 주소가 준비되기 전에 바인딩에 실패하면 스스로 재시도하지 않기 때문이다(G2 T15 실측).
    /// 규칙이 없거나, 기록과 다르게 바뀌었거나, 기록이 없으면 아무것도 하지 않는다.
    /// </summary>
    public bool EnsureListening()
    {
        lock (_gate)
        {
            if (RecordedConnectAddress is null) return false;
            var existing = host.GetPortProxies().FirstOrDefault(r => r.ListenPort == policy.ListenPort
                && string.Equals(r.ListenAddress, policy.ListenAddress, StringComparison.OrdinalIgnoreCase));
            if (existing is null || existing.ConnectPort != policy.ConnectPort
                || !string.Equals(existing.ConnectAddress, RecordedConnectAddress, StringComparison.OrdinalIgnoreCase))
                return false;
            var repaired = false;
            if (!host.IsListening(policy.ListenAddress, policy.ListenPort))
            {
                host.Reapply(existing);
                repaired = true;
            }
            // 추가 서비스: 기록과 같은 대상으로 있는 규칙만 수신 복구한다.
            foreach (var s in policy.Services)
            {
                var r = host.GetPortProxies().FirstOrDefault(p => p.ListenPort == s.ListenPort
                    && string.Equals(p.ListenAddress, policy.ListenAddress, StringComparison.OrdinalIgnoreCase));
                if (r is null || r.ConnectPort != s.ConnectPort
                    || !string.Equals(r.ConnectAddress, RecordedConnectAddress, StringComparison.OrdinalIgnoreCase)) continue;
                if (host.IsListening(policy.ListenAddress, s.ListenPort)) continue;
                host.Reapply(r);
                repaired = true;
            }
            return repaired;
        }
    }

    private RepairResponse ListeningOrRepaired(PortProxyRule existing, string reason)
    {
        if (host.IsListening(policy.ListenAddress, policy.ListenPort)) return new(RepairCode.NoChange, reason);
        host.Reapply(existing);
        return new(RepairCode.Updated, "수신이 끊겨 있어 같은 규칙으로 다시 적용");
    }
}
