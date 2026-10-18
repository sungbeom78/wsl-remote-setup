using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Ssh;

namespace WslRemoteSetup.Forwarding;

/// <summary>포워딩 적용에 필요한 Windows 측 관찰·변경. 변경 함수는 승격 프로세스에서 승인 후에만 호출한다.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsForwarding
{
    private static readonly string Sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
    public static readonly string Netsh = Path.Combine(Sys, "netsh.exe");
    public static readonly string Ssh = Path.Combine(Sys, "OpenSSH", "ssh.exe");
    public static readonly string Wsl = Path.Combine(Sys, "wsl.exe");

    public static CommandRunner Runner() => new([Netsh, Ssh, Wsl]);

    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WslRemoteSetup", "state");

    public static string StatePath => Path.Combine(StateDirectory, "install-state.json");

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>WSL NAT 호스트 측 가상 어댑터(vEthernet (WSL…))의 IPv4 대역.</summary>
    public static Ipv4Cidr? GetWslHostCidr()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !ni.Name.StartsWith("vEthernet (WSL", StringComparison.Ordinal)) continue;
            var a = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (a is not null) return Ipv4Cidr.FromAddress(a.Address, a.PrefixLength);
        }
        return null;
    }

    public static async Task<string?> ReadBannerAsync(IPAddress address, int port, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(address, port, cts.Token);
            var buffer = new byte[256];
            var n = await client.GetStream().ReadAsync(buffer, cts.Token);
            var line = Encoding.ASCII.GetString(buffer, 0, n);
            var end = line.IndexOfAny(['\r', '\n']);
            return end >= 0 ? line[..end] : line;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 대상 SSH 서버의 ED25519 host key 지문을 읽는다. 인증은 시도하지 않는다(PreferredAuthentications=none,
    /// 존재하지 않는 사용자, BatchMode). known_hosts·사용자 설정은 쓰거나 읽지 않는다. 종료코드는 인증 거부로 255가 정상이다.
    /// </summary>
    public static async Task<IReadOnlyList<HostKey>> ProbeHostKeyAsync(ICommandRunner runner, IPAddress address, int port)
    {
        var r = await runner.RunAsync(new CommandSpec(Ssh,
        [
            "-v", "-F", "NUL",
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=NUL",
            "-o", "GlobalKnownHostsFile=NUL",
            "-o", "HostKeyAlgorithms=ssh-ed25519",
            "-o", "KexAlgorithms=curve25519-sha256",
            "-o", "PreferredAuthentications=none",
            "-o", "ConnectTimeout=5",
            "-p", port.ToString(CultureInfo.InvariantCulture),
            "-l", "wrs-hostkey-probe",
            address.ToString(),
        ])
        { Timeout = TimeSpan.FromSeconds(20) });
        return r.TimedOut ? [] : HostKeyFingerprint.ParseSshVerbose(r.StandardError + "\n" + r.StandardOutput);
    }

    /// <summary>소유자 세션에서 WSL 주소를 읽는다(비승격 plan 전용). 승격 적용은 명시 인수로 받는다.</summary>
    public static async Task<IPAddress?> ReadWslAddressAsync(ICommandRunner runner, string distro)
    {
        var addr = await runner.RunAsync(new CommandSpec(Wsl, ["--distribution", distro, "--exec", "/usr/bin/ip", "-j", "-4", "addr", "show"]) { Timeout = TimeSpan.FromSeconds(60) });
        var route = await runner.RunAsync(new CommandSpec(Wsl, ["--distribution", distro, "--exec", "/usr/bin/ip", "-j", "route", "show", "default"]) { Timeout = TimeSpan.FromSeconds(30) });
        if (!addr.ExitedZero || !route.ExitedZero) return null;
        return WslAddressSelector.Select(
            WslAddressSelector.ParseIpAddrJson(addr.StandardOutput),
            WslAddressSelector.ParseDefaultRouteDevice(route.StandardOutput)).Address;
    }

    public static async Task ApplyPortProxyAsync(ICommandRunner runner, ReconcileAction action)
    {
        var d = action.Desired!;
        var verb = action.Kind == ReconcileKind.Update ? "set" : "add";
        var r = await runner.RunAsync(new CommandSpec(Netsh,
        [
            "interface", "portproxy", verb, "v4tov4",
            $"listenaddress={d.ListenAddress}",
            string.Create(CultureInfo.InvariantCulture, $"listenport={d.ListenPort}"),
            $"connectaddress={d.ConnectAddress}",
            string.Create(CultureInfo.InvariantCulture, $"connectport={d.ConnectPort}"),
        ]));
        if (!r.ExitedZero) throw new InvalidOperationException($"portproxy {verb} 실패(exit {r.ExitCode}): {r.StandardOutput.Trim()} {r.StandardError.Trim()}");
    }

    public static async Task DeletePortProxyAsync(ICommandRunner runner, string listenAddress, int listenPort)
    {
        var r = await runner.RunAsync(new CommandSpec(Netsh,
        [
            "interface", "portproxy", "delete", "v4tov4",
            $"listenaddress={listenAddress}",
            string.Create(CultureInfo.InvariantCulture, $"listenport={listenPort}"),
        ]));
        if (!r.ExitedZero) throw new InvalidOperationException($"portproxy delete 실패(exit {r.ExitCode}): {r.StandardOutput.Trim()}");
    }

    public static void CreateFirewallRule(FirewallRuleSpec spec)
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("방화벽 COM 없음");
        var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule") ?? throw new InvalidOperationException("방화벽 규칙 COM 없음");
        dynamic policy = Activator.CreateInstance(policyType)!;
        dynamic rule = Activator.CreateInstance(ruleType)!;
        rule.Name = spec.Name;
        rule.Description = spec.Description;
        rule.Grouping = "WslRemoteSetup";
        rule.Protocol = 6; // TCP
        rule.LocalPorts = spec.Port.ToString(CultureInfo.InvariantCulture);
        rule.LocalAddresses = spec.LocalAddress;
        rule.RemoteAddresses = spec.RemoteCidr;
        rule.Direction = 1; // in
        rule.Action = 1; // allow
        rule.Profiles = (int)spec.Profile;
        rule.Enabled = true;
        policy.Rules.Add(rule);
    }

    /// <summary>이름과 설명(설치 ID)이 모두 일치하는 규칙이 정확히 하나일 때만 로컬 주소를 바꾼다(ADR 0007).</summary>
    public static bool UpdateFirewallLocalAddress(string name, Guid installationId, string newAddress)
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("방화벽 COM 없음");
        dynamic policy = Activator.CreateInstance(policyType)!;
        dynamic? match = null;
        var count = 0;
        foreach (dynamic r in policy.Rules)
        {
            if ((string)r.Name != name) continue;
            count++;
            if (((string?)r.Description ?? "").Contains(installationId.ToString("D"), StringComparison.OrdinalIgnoreCase)) match = r;
        }
        if (count != 1 || match is null) return false;
        match.LocalAddresses = newAddress;
        return true;
    }

    /// <summary>이름과 설명(설치 ID)이 모두 일치하는 규칙이 정확히 하나일 때만 제거한다.</summary>
    public static bool RemoveFirewallRule(string name, Guid installationId)
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("방화벽 COM 없음");
        dynamic policy = Activator.CreateInstance(policyType)!;
        var matches = 0;
        var foreign = 0;
        foreach (dynamic r in policy.Rules)
        {
            if ((string)r.Name != name) continue;
            if (((string?)r.Description ?? "").Contains(installationId.ToString("D"), StringComparison.OrdinalIgnoreCase)) matches++;
            else foreign++;
        }
        if (matches != 1 || foreign != 0) return false;
        policy.Rules.Remove(name);
        return true;
    }

    /// <summary>상태 폴더: SYSTEM·Administrators 전체, Users 읽기. 상속 차단.</summary>
    public static void EnsureProtectedStateDirectory()
    {
        var dir = Directory.CreateDirectory(StateDirectory);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(sec);
        var parent = dir.Parent!;
        var psec = new DirectorySecurity();
        psec.SetAccessRuleProtection(true, false);
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier))) psec.AddAccessRule(rule);
        parent.SetAccessControl(psec);
    }
}
