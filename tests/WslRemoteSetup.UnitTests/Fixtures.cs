using WslRemoteSetup.Core.Model;

namespace WslRemoteSetup.UnitTests;

/// <summary>G2 실측값을 바탕으로 한 스냅샷 픽스처(SID는 테스트용 값).</summary>
internal static class Fixtures
{
    public const string OwnerSid = "S-1-5-21-1-2-3-1002";

    public static EnvironmentSnapshot G2AfterBootstrap() => new()
    {
        CollectedAt = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(9)),
        Os = new OsInfo("Microsoft Windows 11 Pro", "Professional", "23H2", 22631, 6199, true, false, true,
            FeatureState.Enabled, FeatureState.Disabled),
        Identity = new IdentityInfo(@"SERVERPC\ownerAdmin", OwnerSid, false, true, true),
        Accounts =
        [
            new LocalAccountInfo("ownerAdmin", OwnerSid, true, true, true),
            new LocalAccountInfo("consoleuser", "S-1-5-21-1-2-3-1001", true, true, false),
            new LocalAccountInfo("Administrator", "S-1-5-21-1-2-3-500", false, true, false),
        ],
        Interfaces =
        [
            new NetworkInterfaceInfo("Wi-Fi 2", "Realtek 8852BE", true, true, false,
                [new Ipv4AddressInfo("192.168.1.222", 24)], ["192.168.1.1"], ["8.8.8.8"], NetworkCategory.Private),
            new NetworkInterfaceInfo("Ethernet 2", "Realtek GbE", false, false, true, [], [], [], NetworkCategory.Unknown),
        ],
        Rdp = new RdpInfo(true, 7389, true, false),
        Wsl = new WslInfo(true, "3.0.1.0", [new WslDistroInfo("Ubuntu-24.04", 2, 1000, true)]),
        TcpListenPorts = [135, 445, 7389],
        PortProxies = [],
        FirewallRules =
        [
            new FirewallRuleInfo("RDP change port", "RDP change port", true, true, true, FirewallProfiles.Private, "TCP", "7389", "LocalSubnet"),
            new FirewallRuleInfo("RDP-UM-TCP", "Remote Desktop - User Mode (TCP-In)", true, true, true, FirewallProfiles.All, "TCP", "3389", "*"),
            new FirewallRuleInfo("RDP-UM-UDP", "Remote Desktop - User Mode (UDP-In)", true, true, true, FirewallProfiles.All, "UDP", "3389", "*"),
        ],
    };

    public static EnvironmentSnapshot FreshWindows() => G2AfterBootstrap() with
    {
        Os = new OsInfo("Microsoft Windows 11 Pro", "Professional", "23H2", 22631, 6199, true, false, false,
            FeatureState.Disabled, FeatureState.Disabled),
        Wsl = new WslInfo(false, null, []),
    };
}
