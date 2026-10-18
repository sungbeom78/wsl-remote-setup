using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Policy;

namespace WslRemoteSetup.UnitTests;

public class AddressFollowerTests
{
    private const string WifiId = "{00000000-1111-2222-3333-444444444444}";

    private static ForwardingPolicy Policy(bool follow = true) => ForwardingFixtures.Policy() with
    {
        FollowAddressChange = follow,
        ListenInterfaceId = WifiId,
        LanCidr = "192.168.1.0/24",
    };

    private static AdapterView Wifi(string ip, NetworkCategory cat = NetworkCategory.Private, bool up = true) =>
        new(WifiId, up, cat, [new Ipv4AddressInfo(ip, 24)]);

    [Fact]
    public void Same_address_stays() =>
        Assert.Equal(FollowKind.Stay, AddressFollower.Decide(Policy(), [Wifi("192.168.1.222")]).Kind);

    [Fact]
    public void New_address_in_same_lan_on_same_card_moves()
    {
        var d = AddressFollower.Decide(Policy(), [Wifi("192.168.1.37")]);
        Assert.Equal(FollowKind.Move, d.Kind);
        Assert.Equal("192.168.1.37", d.NewAddress);
    }

    [Fact]
    public void Follow_disabled_holds() =>
        Assert.Equal(FollowKind.Hold, AddressFollower.Decide(Policy(follow: false), [Wifi("192.168.1.37")]).Kind);

    [Fact]
    public void Different_lan_holds() // 다른 공유기·카페 와이파이 등
    {
        var d = AddressFollower.Decide(Policy(), [Wifi("10.0.0.15")]);
        Assert.Equal(FollowKind.Hold, d.Kind);
        Assert.Null(d.NewAddress);
    }

    [Fact]
    public void Public_network_holds() => // T12
        Assert.Equal(FollowKind.Hold, AddressFollower.Decide(Policy(), [Wifi("192.168.1.37", NetworkCategory.Public)]).Kind);

    [Fact]
    public void Other_adapter_is_never_used() // FR-05: 다른 어댑터로 확대하지 않음
    {
        var ethernet = new AdapterView("{OTHER}", true, NetworkCategory.Private, [new Ipv4AddressInfo("192.168.1.40", 24)]);
        Assert.Equal(FollowKind.Hold, AddressFollower.Decide(Policy(), [Wifi("192.168.1.222", up: false), ethernet]).Kind);
    }

    [Fact]
    public void Multiple_candidates_hold()
    {
        var two = new AdapterView(WifiId, true, NetworkCategory.Private, [new("192.168.1.37", 24), new("192.168.1.38", 24)]);
        Assert.Equal(FollowKind.Hold, AddressFollower.Decide(Policy(), [two]).Kind);
    }

    [Fact]
    public void Link_local_is_ignored() =>
        Assert.Equal(FollowKind.Hold, AddressFollower.Decide(Policy() with { LanCidr = "169.254.0.0/16" }, [Wifi("169.254.3.4")]).Kind);

    [Fact]
    public void Policy_validation_requires_card_and_narrow_lan()
    {
        Assert.Empty(Policy().Validate());
        Assert.NotEmpty((Policy() with { ListenInterfaceId = null }).Validate());
        Assert.NotEmpty((Policy() with { LanCidr = "10.0.0.0/8" }).Validate());
        Assert.NotEmpty((Policy() with { LanCidr = "10.0.0.0/24" }).Validate()); // 접속 주소가 범위 밖
    }
}
