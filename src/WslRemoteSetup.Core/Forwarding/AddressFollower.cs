using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Forwarding;

/// <summary>주소 따라가기 판단에 필요한 네트워크 카드 정보.</summary>
public sealed record AdapterView(string Id, bool IsUp, NetworkCategory Category, IReadOnlyList<Ipv4AddressInfo> Ipv4);

public enum FollowKind { Stay, Move, Hold }

public sealed record FollowDecision(FollowKind Kind, string? NewAddress, string Reason);

/// <summary>
/// DHCP 주소 변경 따라가기 판단(ADR 0007). 같은 카드·같은 LAN 범위·Private에서 범위 안 주소가 정확히 하나일 때만 이동한다.
/// 다른 어댑터·전체 인터페이스·넓은 범위로 확대하지 않는다(FR-05).
/// </summary>
public static class AddressFollower
{
    public static FollowDecision Decide(ForwardingPolicy policy, IReadOnlyList<AdapterView> adapters)
    {
        if (adapters.Any(a => a.IsUp && a.Ipv4.Any(x => x.Address == policy.ListenAddress)))
            return new(FollowKind.Stay, null, "접속 주소가 그대로 있습니다.");
        if (!policy.FollowAddressChange)
            return new(FollowKind.Hold, null, "주소가 바뀌었지만 따라가기가 꺼져 있습니다.");
        if (policy.ListenInterfaceId is null || !Ipv4Cidr.TryParse(policy.LanCidr, out var lan))
            return new(FollowKind.Hold, null, "따라가기에 필요한 기록(네트워크 카드·LAN 범위)이 없습니다.");

        var adapter = adapters.FirstOrDefault(a => string.Equals(a.Id, policy.ListenInterfaceId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || !adapter.IsUp)
            return new(FollowKind.Hold, null, "처음 승인한 네트워크 카드가 연결되어 있지 않습니다.");
        if (adapter.Category != NetworkCategory.Private)
            return new(FollowKind.Hold, null, "네트워크가 '개인 네트워크'가 아니어서 옮기지 않습니다.");

        var inRange = adapter.Ipv4
            .Where(x => InputValidator.TryParseIPv4(x.Address, out var ip) && !Ipv4Cidr.IsLoopbackOrLinkLocal(ip) && lan.Contains(ip))
            .Select(x => x.Address)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return inRange.Count switch
        {
            1 => new(FollowKind.Move, inRange[0], $"같은 공유기 범위({lan}) 안에서 주소가 {policy.ListenAddress} → {inRange[0]}(으)로 바뀌었습니다."),
            0 => new(FollowKind.Hold, null, $"같은 공유기 범위({lan}) 안의 주소가 없습니다(다른 네트워크에 연결된 것 같습니다)."),
            _ => new(FollowKind.Hold, null, "범위 안 주소가 여러 개라 자동으로 고르지 않습니다."),
        };
    }
}
