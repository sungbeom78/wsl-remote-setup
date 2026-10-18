using WslRemoteSetup.Core.Network;

namespace WslRemoteSetup.UnitTests;

public class StaticIpPlannerTests
{
    private static AdapterAddressing Dhcp(string ip = "192.168.1.37") =>
        new(12, "Wi-Fi 2", true, ip, 24, "192.168.1.1", ["192.168.1.1"]);

    [Fact]
    public void Default_pins_current_address_without_disconnect()
    {
        var p = StaticIpPlanner.Plan(Dhcp(), null, requestedInUse: false);
        Assert.True(p.CanApply);
        Assert.Equal("192.168.1.37", p.NewAddress);
        Assert.False(p.AddressChanges);
        Assert.Equal("255.255.255.0", p.Mask);
        Assert.Equal("192.168.1.1", p.Gateway);
        Assert.DoesNotContain(p.Warnings, w => w.Contains("끊깁니다"));
    }

    [Fact]
    public void Different_address_warns_about_disconnect_and_rollback()
    {
        var p = StaticIpPlanner.Plan(Dhcp(), "192.168.1.200", false);
        Assert.True(p.CanApply);
        Assert.True(p.AddressChanges);
        Assert.Contains(p.Warnings, w => w.Contains("5분"));
    }

    [Theory]
    [InlineData("192.168.51.10", "범위")]
    [InlineData("192.168.1.0", "첫 주소")]
    [InlineData("192.168.1.255", "마지막")]
    [InlineData("192.168.1.1", "게이트웨이")]
    [InlineData("10.1", "형식")]
    public void Invalid_targets_are_blocked(string ip, string reason)
    {
        var p = StaticIpPlanner.Plan(Dhcp(), ip, false);
        Assert.False(p.CanApply);
        Assert.Contains(p.Blockers, b => b.Contains(reason));
    }

    [Fact]
    public void Address_in_use_is_blocked() =>
        Assert.Contains(StaticIpPlanner.Plan(Dhcp(), "192.168.1.80", requestedInUse: true).Blockers, b => b.Contains("다른 기기"));

    [Fact]
    public void Already_static_is_not_applicable() => // G2
        Assert.False(StaticIpPlanner.Plan(Dhcp("192.168.1.222") with { Dhcp = false }, null, false).CanApply);

    [Fact]
    public void Missing_gateway_blocks_and_dns_falls_back_to_gateway()
    {
        Assert.False(StaticIpPlanner.Plan(Dhcp() with { Gateway = null }, null, false).CanApply);
        Assert.Equal(["192.168.1.1"], StaticIpPlanner.Plan(Dhcp() with { Dns = [] }, null, false).Dns);
    }

    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(20, "255.255.240.0")]
    [InlineData(16, "255.255.0.0")]
    public void Mask(int prefix, string mask) => Assert.Equal(mask, StaticIpPlanner.MaskOf(prefix));
}
