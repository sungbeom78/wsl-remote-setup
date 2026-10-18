using System.Net;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Policy;

namespace WslRemoteSetup.Core.Forwarding;

public enum ReconcileKind { NoChange, Add, Update, Conflict }

public sealed record ReconcileAction(ReconcileKind Kind, PortProxyRule? Desired, PortProxyRule? Existing, string Reason);

/// <summary>
/// 정책이 소유한 portproxy tuple(listenAddress:listenPort) 하나만 조정한다.
/// 다른 tuple은 결과에 포함하지 않으며, 외부에서 바뀐 규칙은 덮어쓰지 않고 Conflict로 멈춘다.
/// </summary>
public static class PortProxyReconciler
{
    /// <param name="recordedConnectAddress">이 도구가 마지막으로 적용해 상태에 기록한 대상 주소. 최초 적용 전이면 null.</param>
    public static ReconcileAction Decide(
        ForwardingPolicy policy,
        IReadOnlyList<PortProxyRule> current,
        string? recordedConnectAddress,
        IPAddress target)
    {
        var desired = new PortProxyRule(policy.ListenAddress, policy.ListenPort, target.ToString(), policy.ConnectPort);
        var matches = current
            .Where(r => r.ListenPort == policy.ListenPort && SameAddress(r.ListenAddress, policy.ListenAddress))
            .ToList();

        // 같은 포트를 모든 주소(0.0.0.0/*)에서 받는 규칙이 있으면 우리 tuple과 겹치므로 정지한다.
        var wildcard = current.FirstOrDefault(r => r.ListenPort == policy.ListenPort && IsWildcard(r.ListenAddress));
        if (wildcard is not null)
            return new(ReconcileKind.Conflict, desired, wildcard, "같은 포트에 모든 주소 수신 portproxy가 있습니다.");

        if (matches.Count > 1)
            return new(ReconcileKind.Conflict, desired, matches[0], "같은 수신 tuple 규칙이 여러 개입니다.");

        if (matches.Count == 0)
        {
            return recordedConnectAddress is null
                ? new(ReconcileKind.Add, desired, null, "규칙 없음")
                : new(ReconcileKind.Add, desired, null, "이전에 적용한 규칙이 사라져 다시 추가합니다.");
        }

        var existing = matches[0];
        if (existing.ConnectPort == desired.ConnectPort && SameAddress(existing.ConnectAddress, desired.ConnectAddress))
            return new(ReconcileKind.NoChange, desired, existing, "이미 같은 대상");

        if (recordedConnectAddress is not null
            && existing.ConnectPort == policy.ConnectPort
            && SameAddress(existing.ConnectAddress, recordedConnectAddress))
            return new(ReconcileKind.Update, desired, existing, "WSL 주소 변경");

        return new(ReconcileKind.Conflict, desired, existing, "이 도구가 기록하지 않은 대상으로 바뀐 규칙입니다. 덮어쓰지 않습니다.");
    }

    private static bool SameAddress(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsWildcard(string address) => address is "0.0.0.0" or "*";
}
