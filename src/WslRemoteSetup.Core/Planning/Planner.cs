using WslRemoteSetup.Core.Model;

namespace WslRemoteSetup.Core.Planning;

/// <summary>스냅샷과 목표 구성에서 단계별 계획을 만든다. 시스템을 변경하지 않는다.</summary>
public static class Planner
{
    public static InstallationPlan Create(EnvironmentSnapshot s, DesiredConfiguration d, DateTimeOffset now)
    {
        var errors = d.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(d));

        var steps = new List<PlanStep>();
        var ownerName = d.OwnerAccountName ?? ShortName(s.Identity?.UserName);
        var owner = s.Accounts?.FirstOrDefault(a => string.Equals(a.Name, ownerName, StringComparison.OrdinalIgnoreCase));
        var runningAsOwner = owner is not null && s.Identity is not null && owner.Sid == s.Identity.Sid;

        // 1. WSL 런타임 (전역 Windows 기능)
        var runtime = s.Wsl?.RuntimeInstalled;
        var hypervisorReady = s.Os is { VirtualMachinePlatform: FeatureState.Enabled, HypervisorPresent: true };
        steps.Add(new PlanStep
        {
            Id = "wsl-runtime",
            Title = "WSL 2 런타임 설치",
            Current = runtime switch
            {
                true when hypervisorReady => "설치됨, 하이퍼바이저 동작",
                true => "설치됨, 가상 머신 플랫폼 재부팅 대기 가능",
                false => "미설치",
                null => "조회 실패",
            },
            Target = "WSL 2 런타임 설치·가상 머신 플랫폼 사용",
            Change = runtime switch { true when hypervisorReady => ChangeNeed.NotRequired, null => ChangeNeed.Unknown, _ => ChangeNeed.Required },
            Executor = StepExecutor.Administrator,
            RequiresApproval = true,
            MayRequireReboot = true,
            Revert = Reversibility.ManualOnly,
        });

        // 2. 소유 Windows 계정
        steps.Add(new PlanStep
        {
            Id = "owner-account",
            Title = "WSL 소유 Windows 계정",
            Current = s.Accounts is null ? "조회 실패" : owner is null ? $"{ownerName} 없음" : $"{owner.Name} ({owner.Sid})",
            Target = $"{ownerName} 계정 존재·활성(SID로 식별)",
            Change = s.Accounts is null ? ChangeNeed.Unknown : owner is { Enabled: true } ? ChangeNeed.NotRequired : ChangeNeed.Required,
            Executor = StepExecutor.Administrator,
            RequiresApproval = true,
            Revert = Reversibility.ManualOnly,
            NotImplementedNote = owner is null ? "계정 생성은 M4에서 구현합니다. 비밀번호는 설치 중 사용자가 직접 입력합니다." : null,
        });

        // 3. 배포판 등록 (소유자 세션)
        var distro = runningAsOwner
            ? s.Wsl?.DistrosForCurrentUser.FirstOrDefault(x => string.Equals(x.Name, d.DistroName, StringComparison.OrdinalIgnoreCase))
            : null;
        steps.Add(new PlanStep
        {
            Id = "distro-register",
            Title = $"{d.DistroName} 등록",
            Current = !runningAsOwner ? "소유자 세션이 아니어서 확인 불가" : distro is null ? "없음" : $"있음 (WSL {distro.Version})",
            Target = $"{ownerName} 소유 {d.DistroName}, WSL 2",
            Change = !runningAsOwner ? ChangeNeed.Unknown : distro is { Version: 2 } ? ChangeNeed.NotRequired : ChangeNeed.Required,
            Executor = StepExecutor.OwnerSession,
            RequiresApproval = true,
            DependsOn = ["wsl-runtime", "owner-account"],
            Revert = Reversibility.NotReversible, // 사용자 데이터 보존. unregister는 사용하지 않는다.
        });

        // 4. Linux 기본 사용자
        steps.Add(new PlanStep
        {
            Id = "linux-user",
            Title = $"Linux 사용자 {d.LinuxUser}",
            Current = distro is null ? "배포판 미확인" : distro.DefaultUid == 0 ? "기본 사용자 root" : $"기본 uid {distro.DefaultUid}",
            Target = $"{d.LinuxUser}, sudo 가능, 비밀번호는 사용자가 직접 입력",
            Change = distro is null ? ChangeNeed.Unknown : distro.DefaultUid == 0 ? ChangeNeed.Required : ChangeNeed.NotRequired,
            Executor = StepExecutor.Person,
            RequiresApproval = true,
            DependsOn = ["distro-register"],
            Revert = Reversibility.ManualOnly,
        });

        // 5~8. M4 이후 구현 대상: 현재 상태 확인이 필요하므로 Unknown 또는 Required로 둔다.
        steps.Add(new PlanStep
        {
            Id = "ssh-server",
            Title = "WSL OpenSSH 서버",
            Current = "doctor는 배포판을 깨우지 않으므로 미확인",
            Target = $"openssh-server, TCP {d.WslSshPort}, root 로그인 금지, {d.LinuxUser}만 허용, 설정 sshd -t/-T 검증",
            Change = ChangeNeed.Unknown,
            Executor = StepExecutor.OwnerSession,
            RequiresApproval = true,
            DependsOn = ["linux-user"],
            Revert = Reversibility.ToolCanRevert,
            NotImplementedNote = "M4",
        });

        var listenPortBusy = s.TcpListenPorts?.Contains(d.ListenPort) == true
            || s.PortProxies?.Any(p => p.ListenPort == d.ListenPort) == true;
        steps.Add(new PlanStep
        {
            Id = "portproxy",
            Title = $"Windows TCP {d.ListenPort} → WSL TCP {d.WslSshPort} 포워딩",
            Current = listenPortBusy ? $"TCP {d.ListenPort} 사용 중(충돌)" : "규칙 없음",
            Target = $"활성 어댑터의 기존 IPv4:{d.ListenPort} → 검증된 WSL IPv4:{d.WslSshPort}",
            Change = listenPortBusy ? ChangeNeed.Unknown : ChangeNeed.Required,
            Executor = StepExecutor.Administrator,
            RequiresApproval = true,
            DependsOn = ["ssh-server"],
            Revert = Reversibility.ToolCanRevert,
            NotImplementedNote = listenPortBusy ? "[PORT_CONFLICT] 다른 포트 선택 필요. M4" : "M4",
        });

        steps.Add(new PlanStep
        {
            Id = "firewall-ssh",
            Title = $"방화벽 TCP {d.ListenPort} 허용(LAN 한정)",
            Current = "도구 규칙 없음",
            Target = "현재 Private 프로필·수신 IPv4·실제 LAN CIDR로 제한한 inbound 규칙",
            Change = ChangeNeed.Required,
            Executor = StepExecutor.Administrator,
            RequiresApproval = true,
            DependsOn = ["portproxy"],
            Revert = Reversibility.ToolCanRevert,
            NotImplementedNote = "M4",
        });

        var home = s.Os?.IsHomeEdition == true;
        steps.Add(new PlanStep
        {
            Id = "rdp",
            Title = "Windows RDP",
            Current = home ? "Home 에디션: 미지원" : s.Rdp is null ? "조회 실패" : s.Rdp.Enabled ? $"사용, 포트 {s.Rdp.Port}" : "사용 안 함",
            Target = home ? "SSH 전용" : s.Rdp is { Enabled: true } ? $"기존 포트 {s.Rdp.Port}·NLA 유지" : "RDP 사용, NLA 유지, 허용 사용자 지정",
            Change = home || !d.ConfigureRdp ? ChangeNeed.NotRequired
                : s.Rdp is null ? ChangeNeed.Unknown
                : s.Rdp.Enabled ? ChangeNeed.NotRequired : ChangeNeed.Required,
            Executor = StepExecutor.Administrator,
            RequiresApproval = true,
            MayDisconnect = false,
            Revert = Reversibility.ToolCanRevert,
        });

        steps.Add(new PlanStep
        {
            Id = "autostart",
            Title = "소유자 로그인 후 자동 실행·연결 복구",
            Current = "미등록",
            Target = $"{ownerName} 로그온 작업 + 제한된 네트워크 관리부",
            Change = ChangeNeed.Required,
            Executor = StepExecutor.Administrator,
            RequiresApproval = true,
            DependsOn = ["portproxy", "firewall-ssh"],
            Revert = Reversibility.ToolCanRevert,
            NotImplementedNote = "M3 권한 분리 POC 후 M5",
        });

        return new InstallationPlan(now, steps);
    }

    private static string ShortName(string? domainUser)
    {
        if (string.IsNullOrEmpty(domainUser)) return DesiredConfiguration.DefaultNewWindowsAccount;
        var i = domainUser.IndexOf('\\');
        return i >= 0 ? domainUser[(i + 1)..] : domainUser;
    }
}
