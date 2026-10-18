using System.Net;
using WslRemoteSetup.Core.Network;

namespace WslRemoteSetup.UnitTests;

public class Ipv4CidrTests
{
    [Fact]
    public void FromAddress_computes_network_from_actual_prefix()
    {
        var cidr = Ipv4Cidr.FromAddress(IPAddress.Parse("192.168.1.222"), 24);
        Assert.Equal("192.168.1.0/24", cidr.ToString());
        Assert.True(cidr.Contains(IPAddress.Parse("192.168.1.10")));
        Assert.False(cidr.Contains(IPAddress.Parse("192.168.51.10")));
    }

    [Fact]
    public void Non_24_prefix_is_respected()
    {
        var cidr = Ipv4Cidr.FromAddress(IPAddress.Parse("172.24.220.111"), 20);
        Assert.Equal("172.24.208.0/20", cidr.ToString());
        Assert.True(cidr.Contains(IPAddress.Parse("172.24.223.255")));
        Assert.False(cidr.Contains(IPAddress.Parse("172.24.224.1")));
    }

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("10.0.0/8", false)]
    [InlineData("10.0.0.0", false)]
    public void TryParse(string text, bool ok) => Assert.Equal(ok, Ipv4Cidr.TryParse(text, out _));
}

public class WslAddressSelectorTests
{
    // G2 실측(2026-10-18) 형태: lo에 DNS 터널링 주소 10.255.255.254/32 scope global.
    private const string G2IpAddr = """
        [{"ifindex":1,"ifname":"lo","flags":["LOOPBACK","UP","LOWER_UP"],"addr_info":[
           {"family":"inet","local":"127.0.0.1","prefixlen":8,"scope":"host","label":"lo"},
           {"family":"inet","local":"10.255.255.254","prefixlen":32,"scope":"global","label":"lo"}]},
         {"ifindex":2,"ifname":"eth0","flags":["BROADCAST","MULTICAST","UP","LOWER_UP"],"addr_info":[
           {"family":"inet","local":"172.24.220.111","prefixlen":20,"scope":"global","label":"eth0"}]}]
        """;

    private const string G2Route = """[{"dst":"default","gateway":"172.24.208.1","dev":"eth0","protocol":"kernel","flags":["onlink"]}]""";

    [Fact]
    public void Selects_default_route_interface_and_ignores_lo_global_address()
    {
        var addrs = WslAddressSelector.ParseIpAddrJson(G2IpAddr);
        var dev = WslAddressSelector.ParseDefaultRouteDevice(G2Route);
        var sel = WslAddressSelector.Select(addrs, dev);
        Assert.True(sel.Success);
        Assert.Equal("172.24.220.111", sel.Address!.ToString());
        Assert.Equal("eth0", sel.InterfaceName);
    }

    [Fact]
    public void First_token_would_be_wrong_but_selector_is_not()
    {
        var addrs = WslAddressSelector.ParseIpAddrJson(G2IpAddr);
        Assert.Equal("127.0.0.1", addrs[0].Address.ToString()); // 첫 토큰 방식이면 잘못 고른다
        Assert.DoesNotContain(addrs, a => a.InterfaceName == "eth0" && a.IsLoopbackInterface);
    }

    [Fact]
    public void Docker_default_route_is_rejected()
    {
        var json = """[{"ifname":"docker0","flags":["UP"],"addr_info":[{"family":"inet","local":"172.17.0.1","prefixlen":16}]}]""";
        var sel = WslAddressSelector.Select(WslAddressSelector.ParseIpAddrJson(json), "docker0");
        Assert.False(sel.Success);
    }

    [Fact]
    public void Multiple_default_routes_are_ambiguous()
    {
        var route = """[{"dst":"default","dev":"eth0"},{"dst":"default","dev":"eth1"}]""";
        Assert.Null(WslAddressSelector.ParseDefaultRouteDevice(route));
        Assert.False(WslAddressSelector.Select([], null).Success);
    }

    [Fact]
    public void Multiple_addresses_on_interface_are_not_auto_selected()
    {
        var json = """
            [{"ifname":"eth0","flags":["UP"],"addr_info":[
              {"family":"inet","local":"172.24.220.111","prefixlen":20},
              {"family":"inet","local":"172.24.220.112","prefixlen":20}]}]
            """;
        Assert.False(WslAddressSelector.Select(WslAddressSelector.ParseIpAddrJson(json), "eth0").Success);
    }
}
