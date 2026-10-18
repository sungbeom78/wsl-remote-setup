using System.Net;
using System.Text.Json;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Network;

public sealed record LinuxInterfaceAddress(string InterfaceName, IPAddress Address, int PrefixLength, bool IsLoopbackInterface);

public sealed record WslAddressSelection(IPAddress? Address, string InterfaceName, string Reason)
{
    public bool Success => Address is not null;
}

/// <summary>
/// 대상 WSL의 포워딩 대상 IPv4를 고른다. 출력의 첫 토큰을 쓰지 않고
/// `ip -j -4 addr`와 `ip -j route show default`의 구조화 결과에서 기본 경로 인터페이스의 주소를 선택한다.
/// WSL은 lo에 DNS 터널링용 10.255.255.254/32(scope global)를 두므로 loopback 인터페이스는 항상 제외한다.
/// </summary>
public static class WslAddressSelector
{
    private static readonly string[] ExcludedPrefixes = ["lo", "docker", "br-", "veth", "virbr", "cni", "flannel", "tailscale", "zt"];

    public static IReadOnlyList<LinuxInterfaceAddress> ParseIpAddrJson(string json)
    {
        var result = new List<LinuxInterfaceAddress>();
        using var doc = JsonDocument.Parse(json);
        foreach (var iface in doc.RootElement.EnumerateArray())
        {
            var name = iface.TryGetProperty("ifname", out var n) ? n.GetString() ?? "" : "";
            var isLoopback = iface.TryGetProperty("flags", out var flags)
                && flags.ValueKind == JsonValueKind.Array
                && flags.EnumerateArray().Any(f => f.GetString() == "LOOPBACK");
            if (!iface.TryGetProperty("addr_info", out var addrs) || addrs.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in addrs.EnumerateArray())
            {
                if (a.TryGetProperty("family", out var fam) && fam.GetString() != "inet") continue;
                if (!a.TryGetProperty("local", out var local) || !a.TryGetProperty("prefixlen", out var plen)) continue;
                if (!InputValidator.TryParseIPv4(local.GetString(), out var ip)) continue;
                result.Add(new LinuxInterfaceAddress(name, ip, plen.GetInt32(), isLoopback));
            }
        }
        return result;
    }

    /// <summary>기본 경로의 장치 이름. 여러 개면 null(모호함)을 돌려준다.</summary>
    public static string? ParseDefaultRouteDevice(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var devices = doc.RootElement.EnumerateArray()
            .Where(r => r.TryGetProperty("dst", out var d) && d.GetString() == "default")
            .Select(r => r.TryGetProperty("dev", out var dev) ? dev.GetString() : null)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return devices.Count == 1 ? devices[0] : null;
    }

    public static WslAddressSelection Select(IReadOnlyList<LinuxInterfaceAddress> addresses, string? defaultRouteDevice)
    {
        if (string.IsNullOrEmpty(defaultRouteDevice))
            return new(null, "", "기본 경로 인터페이스가 없거나 여러 개라서 대상 주소를 확정할 수 없습니다.");
        if (IsExcludedInterface(defaultRouteDevice))
            return new(null, defaultRouteDevice, $"기본 경로가 제외 대상 인터페이스({defaultRouteDevice})를 가리킵니다.");

        var candidates = addresses
            .Where(a => a.InterfaceName == defaultRouteDevice && !a.IsLoopbackInterface)
            .Where(a => !Ipv4Cidr.IsLoopbackOrLinkLocal(a.Address) && a.PrefixLength < 32)
            .ToList();

        return candidates.Count switch
        {
            0 => new(null, defaultRouteDevice, $"{defaultRouteDevice}에 사용할 수 있는 IPv4가 없습니다."),
            1 => new(candidates[0].Address, defaultRouteDevice, $"기본 경로 인터페이스 {defaultRouteDevice}의 유일한 IPv4"),
            _ => new(null, defaultRouteDevice, $"{defaultRouteDevice}에 IPv4가 여러 개라서 자동 선택하지 않습니다."),
        };
    }

    private static bool IsExcludedInterface(string name) =>
        ExcludedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
}
