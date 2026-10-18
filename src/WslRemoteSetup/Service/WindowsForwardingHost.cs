using System.Net;
using System.Runtime.Versioning;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Probing;

namespace WslRemoteSetup.Service;

/// <summary>네트워크 관리부가 직접 관찰·변경하는 Windows 측 구현. 요청 본문이 아니라 이 관찰값으로 판단한다.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsForwardingHost(ForwardingPolicy policy, ICommandRunner runner) : IForwardingHost
{
    public Ipv4Cidr? GetHostWslCidr() => WindowsForwarding.GetWslHostCidr();

    public IReadOnlyList<PortProxyRule> GetPortProxies() => WindowsProbe.ProbePortProxy();

    public bool CheckEndpoint(IPAddress address, int port)
    {
        var banner = WindowsForwarding.ReadBannerAsync(address, port, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (banner?.StartsWith("SSH-2.0-", StringComparison.Ordinal) != true) return false;
        if (policy.SshHostKeyFingerprints.Count == 0) return false; // 등록된 host key 없이 대상 변경을 허용하지 않는다.
        var keys = WindowsForwarding.ProbeHostKeyAsync(runner, address, port).GetAwaiter().GetResult();
        return keys.Any(k => policy.SshHostKeyFingerprints.Contains(k.Fingerprint, StringComparer.Ordinal));
    }

    public void Apply(ReconcileAction action) => WindowsForwarding.ApplyPortProxyAsync(runner, action).GetAwaiter().GetResult();

    public bool IsListening(string address, int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(e => e.Port == port && e.Address.ToString() == address);

    /// <summary>같은 tuple 삭제 후 추가. IP Helper가 수신을 다시 연다. 수신 주소가 아직 없으면 다음 점검 때 다시 시도된다.</summary>
    public void Reapply(PortProxyRule rule)
    {
        WindowsForwarding.DeletePortProxyAsync(runner, rule.ListenAddress, rule.ListenPort).GetAwaiter().GetResult();
        WindowsForwarding.ApplyPortProxyAsync(runner, new ReconcileAction(ReconcileKind.Add, rule, null, "수신 복구")).GetAwaiter().GetResult();
    }
}
