using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Network;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Diagnostics;

/// <summary>진단 스냅샷을 항목별 결과로 평가한다. 시스템을 조회하지 않는 순수 함수다.</summary>
public static class DoctorEvaluator
{
    public static IReadOnlyList<CheckResult> Evaluate(EnvironmentSnapshot s, DesiredConfiguration desired)
    {
        var results = new List<CheckResult>();
        EvaluateOs(s, results);
        EvaluateIdentity(s, desired, results);
        EvaluateVirtualization(s, results);
        EvaluateWsl(s, desired, results);
        EvaluateNetwork(s, results);
        EvaluateRdp(s, results);
        EvaluateListenPort(s, desired, results);

        results.Add(new("SSH_LAN_AUTH", "다른 LAN PC의 실제 SSH 인증", CheckStatus.NotTested, "doctor는 외부 인증을 검사하지 않습니다."));
        results.Add(new("RDP_LAN_AUTH", "다른 LAN PC의 실제 RDP 인증", CheckStatus.NotTested, "doctor는 외부 인증을 검사하지 않습니다."));
        results.Add(new("PRE_LOGIN", "로그인 전 무인 동작", CheckStatus.NotApplicable, "1.0 범위가 아닙니다."));

        foreach (var e in s.Errors)
            results.Add(new($"PROBE_{e.Area.ToUpperInvariant()}", $"조회 실패: {e.Area}", CheckStatus.NotTested, e.Message));

        return results;
    }

    private static void EvaluateOs(EnvironmentSnapshot s, List<CheckResult> r)
    {
        if (s.Os is not { } os) return;
        var label = $"{os.Caption} {os.DisplayVersion} ({os.Build}.{os.Ubr})";
        r.Add(os.IsHomeEdition
            ? new("OS_EDITION", "Windows 에디션", CheckStatus.Warn, $"{label}: Home은 RDP 호스트가 없어 SSH 전용으로 진행합니다.")
            : new("OS_EDITION", "Windows 에디션", CheckStatus.Pass, label));
        if (!os.Is64Bit)
            r.Add(new("OS_ARCH", "아키텍처", CheckStatus.Fail, "64비트 Windows만 지원합니다."));
        else if (os.IsArm64)
            r.Add(new("OS_ARCH", "아키텍처", CheckStatus.Warn, "ARM64 Windows: 노트북 접속 도우미(client)는 지원하지만, 서버 설치는 아직 시험하지 않았습니다."));
        if (os.PendingReboot == true)
            r.Add(new("OS_REBOOT", "재부팅 대기", CheckStatus.Warn, "재부팅 대기 중인 변경이 있습니다. 재부팅은 사용자 승인 후 진행합니다."));
    }

    private static void EvaluateIdentity(EnvironmentSnapshot s, DesiredConfiguration desired, List<CheckResult> r)
    {
        if (s.Identity is not { } id) return;
        r.Add(new("RUN_CONTEXT", "현재 실행 계정", CheckStatus.Pass,
            $"{ShortName(id.UserName)}{(id.IsElevated ? ", 관리자 권한으로 실행 중" : ", 일반 권한으로 실행 중")}{(id.IsRemoteSession ? ", 원격 데스크톱으로 접속 중" : "")}"));

        var ownerName = desired.OwnerAccountName ?? ShortName(id.UserName);
        var owner = s.Accounts?.FirstOrDefault(a => string.Equals(a.Name, ownerName, StringComparison.OrdinalIgnoreCase));
        if (s.Accounts is null)
            r.Add(new("OWNER_ACCOUNT", "WSL 소유 계정", CheckStatus.NotTested, "로컬 계정을 조회하지 못했습니다."));
        else if (owner is null)
            r.Add(new("OWNER_ACCOUNT", "WSL 소유 계정", CheckStatus.Pending, $"'{ownerName}' 계정이 없습니다. 생성은 승인 대상입니다."));
        else if (!owner.Enabled)
            r.Add(new("OWNER_ACCOUNT", "WSL 소유 계정", CheckStatus.Fail, $"'{owner.Name}' 계정이 비활성화되어 있습니다."));
        else
            r.Add(new("OWNER_ACCOUNT", "WSL 소유 계정", CheckStatus.Pass,
                $"{owner.Name}{(owner.IsAdministrator ? ", 관리자 그룹" : "")}{(owner.IsRemoteDesktopUser ? ", 원격 데스크톱 허용" : "")}"));

        if (id.IsElevated)
            r.Add(new("RUN_ELEVATED", "승격 실행", CheckStatus.Warn,
                "승격 세션에서는 WSL 배포판 결과가 소유자 일반 세션과 다를 수 있습니다. 배포판 등록·Linux 작업은 소유자 일반 세션에서 합니다."));
    }

    private static void EvaluateVirtualization(EnvironmentSnapshot s, List<CheckResult> r)
    {
        if (s.Os is not { } os) return;
        var status = (os.VirtualMachinePlatform, os.HypervisorPresent) switch
        {
            (FeatureState.Enabled, true) => CheckStatus.Pass,
            (FeatureState.Enabled, false) or (FeatureState.EnablePending, _) => CheckStatus.Pending,
            (FeatureState.Unknown, _) => CheckStatus.NotTested,
            _ => CheckStatus.Pending,
        };
        var detail = status switch
        {
            CheckStatus.Pass => "가상 머신 플랫폼 사용, 하이퍼바이저 동작 중",
            CheckStatus.NotTested => "기능 상태를 조회하지 못했습니다.",
            _ when os.VirtualMachinePlatform is FeatureState.Enabled or FeatureState.EnablePending => "기능은 켜졌지만 재부팅 후 적용됩니다.",
            _ => "가상 머신 플랫폼이 꺼져 있습니다. WSL 런타임 설치 단계에서 활성화합니다.",
        };
        r.Add(new("VIRTUALIZATION", "가상 머신 플랫폼", status, detail));
    }

    private static void EvaluateWsl(EnvironmentSnapshot s, DesiredConfiguration desired, List<CheckResult> r)
    {
        if (s.Wsl is not { } wsl) return;
        r.Add(wsl.RuntimeInstalled
            ? new("WSL_RUNTIME", "WSL 런타임", CheckStatus.Pass, $"WSL {wsl.RuntimeVersion ?? "(버전 미확인)"}")
            : new("WSL_RUNTIME", "WSL 런타임", CheckStatus.Pending, "WSL 런타임이 설치되지 않았습니다."));

        var distro = wsl.DistrosForCurrentUser.FirstOrDefault(d => string.Equals(d.Name, desired.DistroName, StringComparison.OrdinalIgnoreCase));
        const string scope = "(현재 실행 계정 기준. 다른 계정의 배포판은 검사하지 않음)";
        if (distro is null)
            r.Add(new("WSL_DISTRO", "대상 배포판", CheckStatus.Pending, $"{desired.DistroName} 없음 {scope}"));
        else if (distro.Version != 2)
            r.Add(new("WSL_DISTRO", "대상 배포판", CheckStatus.Fail, $"{distro.Name}이(가) WSL {distro.Version}입니다. 변환은 별도 승인 대상입니다."));
        else
            r.Add(new("WSL_DISTRO", "대상 배포판", CheckStatus.Pass, $"{distro.Name}, WSL 2 {scope}"));

        if (distro is { Version: 2 })
            r.Add(distro.DefaultUid == 0
                ? new("WSL_LINUX_USER", "Linux 기본 사용자", CheckStatus.Pending, "기본 사용자가 root(uid 0)입니다. Linux 사용자 초기화가 필요합니다.")
                : new("WSL_LINUX_USER", "Linux 기본 사용자", CheckStatus.Pass, $"기본 uid {distro.DefaultUid}"));
    }

    private static NetworkInterfaceInfo? EvaluateNetwork(EnvironmentSnapshot s, List<CheckResult> r)
    {
        if (s.Interfaces is null) return null;
        var routed = s.Interfaces.Where(i => i.IsUp && i.Gateways.Count > 0 && i.Ipv4.Count > 0).ToList();
        if (routed.Count == 0)
        {
            r.Add(new("NET_ADAPTER", "활성 네트워크", CheckStatus.Fail, "기본 게이트웨이가 있는 활성 IPv4 어댑터가 없습니다."));
            return null;
        }
        if (routed.Count > 1)
        {
            r.Add(new("NET_ADAPTER", "활성 네트워크", CheckStatus.Warn,
                $"기본 게이트웨이가 있는 어댑터가 여러 개입니다({string.Join(", ", routed.Select(i => i.Name))}). 수신 어댑터를 사용자가 선택해야 합니다."));
            return null;
        }

        var a = routed[0];
        var ip = a.Ipv4[0];
        var cidr = InputValidator.TryParseIPv4(ip.Address, out var parsed)
            ? Ipv4Cidr.FromAddress(parsed, ip.PrefixLength).ToString()
            : "?";
        r.Add(new("NET_ADAPTER", "활성 네트워크", CheckStatus.Pass,
            $"{a.Name}{(a.IsWireless ? " (Wi-Fi)" : "")}: {ip.Address}/{ip.PrefixLength}, LAN {cidr}, GW {string.Join(",", a.Gateways)}, DNS {string.Join(",", a.DnsServers)}"));

        r.Add(a.DhcpEnabled switch
        {
            false => new("NET_STATIC_IP", "Windows 주소 고정", CheckStatus.Pass, "수동(고정) IPv4입니다. 이 도구는 기존 주소를 변경하지 않습니다."),
            true => new("NET_STATIC_IP", "Windows 주소 고정", CheckStatus.Warn, "DHCP 주소입니다. 주소 고정: 미완료. 접속 주소가 바뀔 수 있습니다."),
            null => new("NET_STATIC_IP", "Windows 주소 고정", CheckStatus.NotTested, "DHCP 여부를 확인하지 못했습니다."),
        });

        r.Add(a.Category switch
        {
            NetworkCategory.Private or NetworkCategory.DomainAuthenticated =>
                new("NET_PROFILE", "네트워크 프로필", CheckStatus.Pass, a.Category.ToString()),
            NetworkCategory.Public =>
                new("NET_PROFILE", "네트워크 프로필", CheckStatus.Warn, "Public 프로필입니다. 신뢰 여부를 확인하기 전에는 방화벽 규칙을 만들지 않습니다."),
            _ => new("NET_PROFILE", "네트워크 프로필", CheckStatus.NotTested, "프로필을 확인하지 못했습니다."),
        });
        return a;
    }

    private static void EvaluateRdp(EnvironmentSnapshot s, List<CheckResult> r)
    {
        if (s.Os is { IsHomeEdition: true })
        {
            r.Add(new("RDP", "Windows RDP", CheckStatus.NotApplicable, "Home 에디션: RDP 우회 없이 SSH 전용으로 안내합니다."));
            return;
        }
        if (s.Rdp is not { } rdp) return;
        if (!rdp.Enabled)
        {
            r.Add(new("RDP", "Windows RDP", CheckStatus.Pending, $"RDP가 꺼져 있습니다(설정된 포트 {rdp.Port}). 활성화는 승인 대상입니다."));
            return;
        }

        var listening = s.TcpListenPorts?.Contains(rdp.Port);
        r.Add(new("RDP", "Windows RDP", listening == false ? CheckStatus.Warn : CheckStatus.Pass,
            $"사용, 포트 {rdp.Port}{(listening == false ? " (리스너 미확인)" : "")}{(rdp.PolicyOverride ? ", 그룹 정책 적용" : "")}. 기존 포트를 보존합니다."));
        r.Add(rdp.NlaRequired
            ? new("RDP_NLA", "RDP 네트워크 수준 인증", CheckStatus.Pass, "NLA 사용")
            : new("RDP_NLA", "RDP 네트워크 수준 인증", CheckStatus.Warn, "NLA가 꺼져 있습니다."));

        if (s.FirewallRules is null) return;
        var active = s.FirewallRules.Where(f => f is { Enabled: true, Inbound: true, Allow: true }).ToList();
        var allowRdp = active.Where(f => f.ExplicitlyAllowsTcpPort(rdp.Port)).ToList();
        var broad = allowRdp.Where(f => f.IsRemoteUnrestricted || f.Profiles.HasFlag(FirewallProfiles.Public)).ToList();
        if (allowRdp.Count == 0)
            r.Add(new("RDP_FIREWALL", "RDP 방화벽", CheckStatus.Warn, $"TCP {rdp.Port}를 명시적으로 허용하는 활성 inbound 규칙을 찾지 못했습니다."));
        else if (broad.Count > 0)
            r.Add(new("RDP_FIREWALL", "RDP 방화벽", CheckStatus.Warn,
                $"원격 주소 제한이 없거나 Public 프로필을 포함하는 허용 규칙이 있습니다: {Names(broad)}"));
        else
            r.Add(new("RDP_FIREWALL", "RDP 방화벽", CheckStatus.Pass,
                $"허용 규칙: {string.Join(", ", allowRdp.Select(f => $"{f.DisplayName}[{f.Profiles}/{f.RemoteAddresses}]").Distinct())}"));

        var allPorts = active.Where(f => f.IsUnscopedAllPorts).ToList();
        if (allPorts.Count > 0)
            r.Add(new("FIREWALL_ALL_PORTS", "모든 포트 허용 규칙", CheckStatus.Warn,
                $"프로그램 제한 없이 모든 TCP 포트를 여는 규칙이 있습니다: {Names(allPorts)}. LAN 제한 완료로 보지 않습니다."));

        if (rdp.Port != 3389)
        {
            var stale = s.FirewallRules.Where(f => f is { Enabled: true, Inbound: true, Allow: true }
                && f.Protocol is "TCP" or "UDP" && f.LocalPorts == "3389" && f.IsRemoteUnrestricted).ToList();
            if (stale.Count > 0)
                r.Add(new("RDP_FIREWALL_DEFAULT", "기본 RDP 3389 규칙", CheckStatus.Warn,
                    $"RDP는 {rdp.Port}를 쓰지만 3389를 모든 원격 주소에 허용하는 기본 규칙이 켜져 있습니다({stale.Count}개). 현재 리스너가 없어 실효는 없으나 LAN 제한 완료로 보지 않습니다."));
        }
    }

    private static void EvaluateListenPort(EnvironmentSnapshot s, DesiredConfiguration desired, List<CheckResult> r)
    {
        var port = desired.ListenPort;
        var proxy = s.PortProxies?.Where(p => p.ListenPort == port).ToList();
        var listening = s.TcpListenPorts?.Contains(port);
        if (proxy is { Count: > 0 })
            r.Add(new("LISTEN_PORT", $"Windows SSH 수신 포트 {port}", CheckStatus.Warn,
                $"기존 portproxy가 있습니다: {string.Join(", ", proxy.Select(p => $"{p.ListenAddress}:{p.ListenPort}→{p.ConnectAddress}:{p.ConnectPort}"))}. 소유권 확인 전에는 변경하지 않습니다."));
        else if (listening == true)
            r.Add(new("LISTEN_PORT", $"Windows SSH 수신 포트 {port}", CheckStatus.Fail,
                $"[PORT_CONFLICT] TCP {port}를 다른 프로세스가 사용 중입니다. 기존 서비스는 변경하지 않습니다."));
        else if (listening is null)
            r.Add(new("LISTEN_PORT", $"Windows SSH 수신 포트 {port}", CheckStatus.NotTested, "리스너를 조회하지 못했습니다."));
        else
            r.Add(new("LISTEN_PORT", $"Windows SSH 수신 포트 {port}", CheckStatus.Pass, "사용 가능(리스너·portproxy 없음)"));

        r.Add(new("SSH_FORWARD", "Windows 포워딩 경유 SSH", CheckStatus.Pending, "M4에서 구성합니다."));
    }

    private static string Names(IEnumerable<FirewallRuleInfo> rules, int max = 5)
    {
        var names = rules.Select(f => f.DisplayName).Distinct(StringComparer.Ordinal).ToList();
        return names.Count <= max
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(max))} 외 {names.Count - max}개";
    }

    private static string ShortName(string domainUser)
    {
        var i = domainUser.IndexOf('\\');
        return i >= 0 ? domainUser[(i + 1)..] : domainUser;
    }
}
