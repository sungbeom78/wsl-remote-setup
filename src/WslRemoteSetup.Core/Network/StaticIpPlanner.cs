using System.Buffers.Binary;
using System.Net;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Network;

/// <summary>고정 IP 설정 대상 네트워크 카드의 현재 값(읽기 전용으로 수집).</summary>
public sealed record AdapterAddressing(
    int InterfaceIndex,
    string Name,
    bool Dhcp,
    string Address,
    int PrefixLength,
    string? Gateway,
    IReadOnlyList<string> Dns);

public sealed record StaticIpPlan(
    AdapterAddressing Adapter,
    string NewAddress,
    string Mask,
    string Gateway,
    IReadOnlyList<string> Dns,
    bool AddressChanges,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings)
{
    public bool CanApply => Blockers.Count == 0;
}

/// <summary>
/// 고정 IP 설정 계획(후속 범위 "고급 마법사"). 기본은 지금 주소를 그대로 고정해 접속이 끊기지 않게 한다.
/// 다른 주소는 같은 LAN 범위·게이트웨이/네트워크/브로드캐스트 아님·사용 중 아님을 확인한다. 게이트웨이와 DNS는 지금 값을 유지한다.
/// </summary>
public static class StaticIpPlanner
{
    public static StaticIpPlan Plan(AdapterAddressing a, string? requestedAddress, bool requestedInUse)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var newAddress = string.IsNullOrWhiteSpace(requestedAddress) ? a.Address : requestedAddress.Trim();

        if (!a.Dhcp) blockers.Add("이미 고정 주소입니다. 바꿀 필요가 없습니다.");
        if (a.Gateway is null) blockers.Add("공유기(게이트웨이) 주소를 찾지 못했습니다. 인터넷에 연결된 네트워크에서만 진행합니다.");
        if (a.Dns.Count == 0) warnings.Add("DNS 서버 정보가 없어 공유기 주소를 DNS로 씁니다.");
        if (a.PrefixLength is < 8 or > 30) blockers.Add($"네트워크 범위(/{a.PrefixLength})가 일반적이지 않아 자동으로 고정하지 않습니다.");

        if (!InputValidator.TryParseIPv4(newAddress, out var ip)) blockers.Add("주소 형식이 올바르지 않습니다(예: 192.168.1.30).");
        else if (InputValidator.TryParseIPv4(a.Address, out var cur))
        {
            var lan = Ipv4Cidr.FromAddress(cur, a.PrefixLength);
            var v = Ipv4Cidr.ToUInt32(ip);
            var broadcast = lan.Network | (a.PrefixLength == 32 ? 0u : uint.MaxValue >> a.PrefixLength);
            if (!lan.Contains(ip)) blockers.Add($"지금 공유기 범위({lan}) 밖의 주소입니다.");
            else if (v == lan.Network || v == broadcast) blockers.Add("범위의 첫 주소나 마지막 주소는 쓸 수 없습니다.");
            else if (newAddress == a.Gateway) blockers.Add("공유기(게이트웨이) 주소와 같습니다.");
            else if (newAddress != a.Address && requestedInUse) blockers.Add("이미 다른 기기가 쓰고 있는 주소입니다(응답이 있었음).");
        }

        var changes = newAddress != a.Address;
        if (changes)
            warnings.Add("주소가 바뀌면 지금 원격 연결이 끊깁니다. 새 주소로 다시 접속해 5분 안에 확인하지 않으면 원래대로 돌아갑니다.");
        warnings.Add("공유기가 같은 주소를 다른 기기에 나눠 줄 수 있습니다. 공유기의 자동 할당 범위 밖 주소를 쓰거나, 공유기에서 이 PC에 주소를 예약하는 것이 가장 안전합니다.");

        return new StaticIpPlan(a, newAddress, MaskOf(a.PrefixLength), a.Gateway ?? "", a.Dns.Count > 0 ? a.Dns : a.Gateway is null ? [] : [a.Gateway],
            changes, blockers, warnings);
    }

    public static string MaskOf(int prefix)
    {
        var m = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, m);
        return new IPAddress(b).ToString();
    }
}
