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
                // 간격 제한 중이라도 이미 같은 대상이면 정상 응답한다.
                var same = PortProxyReconciler.Decide(policy, host.GetPortProxies(), RecordedConnectAddress, request.ObservedWslIPv4);
                return same.Kind == ReconcileKind.NoChange ? new(RepairCode.NoChange, same.Reason) : new(decision.Code, decision.Reason);
            }
            if (!decision.Allowed) return new(decision.Code, decision.Reason);

            var action = PortProxyReconciler.Decide(policy, host.GetPortProxies(), RecordedConnectAddress, request.ObservedWslIPv4);
            switch (action.Kind)
            {
                case ReconcileKind.NoChange:
                    RecordedConnectAddress = action.Desired!.ConnectAddress;
                    return new(RepairCode.NoChange, action.Reason);
                case ReconcileKind.Conflict:
                    return new(RepairCode.Conflict, action.Reason);
            }

            if (!host.CheckEndpoint(request.ObservedWslIPv4, policy.ConnectPort))
                return new(RepairCode.EndpointUnreachable, "대상 SSH endpoint를 확인하지 못했습니다.");

            host.Apply(action);
            RecordedConnectAddress = action.Desired!.ConnectAddress;
            LastUpdateAt = now;
            return new(RepairCode.Updated, action.Kind == ReconcileKind.Add ? "포워딩 추가" : "포워딩 대상 갱신");
        }
    }
}
