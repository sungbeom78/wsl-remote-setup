namespace WslRemoteSetup.Core.Status;

public enum Health { Good, Warn, Bad, Off, Unknown }

public enum ServiceState { NotInstalled, Running, Stopped, Other }

/// <summary>status 화면에 필요한 관찰값(읽기 전용으로 수집). 조회하지 못한 값은 null.</summary>
public sealed record StatusFacts
{
    public bool Installed { get; init; }
    public string? OwnerName { get; init; }
    public string? CurrentUser { get; init; }
    public string? Distro { get; init; }
    public string? ListenAddress { get; init; }
    public int? ListenPort { get; init; }

    public ServiceState Service { get; init; }
    public bool? TaskRegistered { get; init; }
    public bool? AgentRunning { get; init; }
    public bool? Paused { get; init; }
    public DateTimeOffset? AgentLastCheck { get; init; }
    public string? AgentLastCode { get; init; }
    public int AgentFailures { get; init; }

    public bool? WslRunning { get; init; }
    public bool? ForwardRuleExists { get; init; }
    public bool? FirewallRuleExists { get; init; }
    public bool? Listening { get; init; }
    public bool? SshAnswers { get; init; }
    /// <summary>접속 주소가 지금 이 PC에 있는지(주소가 바뀌었으면 false).</summary>
    public bool? ListenAddressPresent { get; init; }
    /// <summary>접속 주소를 가진 네트워크가 자동 할당(DHCP)인지.</summary>
    public bool? AddressIsDhcp { get; init; }
    public string? CurrentAddress { get; init; }
    /// <summary>추가 서비스: 이름·외부 포트·WSL 포트·수신 여부.</summary>
    public IReadOnlyList<(string Name, int ListenPort, int ConnectPort, bool? Listening)> Services { get; init; } = [];
    public DateTimeOffset Now { get; init; }
}

public sealed record StatusLine(string Title, Health Health, string Text, string? Hint = null);

public sealed record StatusReport(Health Overall, string Headline, IReadOnlyList<StatusLine> Lines, IReadOnlyList<string> NextSteps);

/// <summary>
/// 관찰값을 쉬운 말의 상태 화면으로 바꾼다(ADR 0006). 기술 값(SID, 설치 ID, 프로세스 번호)은 넣지 않는다.
/// 시스템을 조회하거나 바꾸지 않는 순수 함수다.
/// </summary>
public static class StatusEvaluator
{
    private static readonly TimeSpan StaleCheck = TimeSpan.FromMinutes(5);

    public static StatusReport Evaluate(StatusFacts f)
    {
        var lines = new List<StatusLine>();
        var next = new List<string>();

        if (!f.Installed)
        {
            return new StatusReport(Health.Off, "아직 설정하지 않았습니다.",
                [new("설정 기록", Health.Off, "이 도구로 설정한 기록이 없습니다.")],
                ["처음 설정은 'plan'으로 계획을 확인한 뒤 진행합니다."]);
        }

        var address = f.ListenAddress is null ? "?" : $"{f.ListenAddress}, {f.ListenPort}번";

        // 1. SSH 연결(가장 중요한 결과)
        lines.Add(f.SshAnswers switch
        {
            true => new("SSH 접속", Health.Good, $"다른 PC에서 접속할 수 있습니다 ({address})"),
            false when f.Paused == true => new("SSH 접속", Health.Off, "자동 연결 관리를 멈춰 두어 지금은 접속할 수 없습니다."),
            false when f.WslRunning == false => new("SSH 접속", Health.Bad, "WSL이 꺼져 있어 접속할 수 없습니다."),
            false when f.Listening == false => new("SSH 접속", Health.Bad, $"{address}으로 들어오는 연결 통로가 열려 있지 않습니다."),
            false => new("SSH 접속", Health.Bad, "접속 주소는 열려 있지만 WSL SSH가 응답하지 않습니다."),
            null => new("SSH 접속", Health.Unknown, "확인하지 못했습니다."),
        });

        // 2. WSL
        lines.Add(f.WslRunning switch
        {
            true => new("WSL", Health.Good, $"켜져 있음 ({f.Distro})"),
            false when f.Paused == true => new("WSL", Health.Off, $"꺼져 있음 ({f.Distro}) — 멈춤 상태라 정상입니다"),
            false => new("WSL", Health.Bad, $"꺼져 있음 ({f.Distro})"),
            null => new("WSL", Health.Unknown, "확인하지 못했습니다."),
        });

        // 3. 자동 연결 관리(로그인 후 동작하는 도우미)
        var isOwner = f.OwnerName is null || f.CurrentUser is null
            || string.Equals(f.OwnerName, f.CurrentUser, StringComparison.OrdinalIgnoreCase);
        if (f.Paused == true)
            lines.Add(new("자동 연결 관리", Health.Off, "멈춤 상태입니다(WSL을 켜지 않습니다).", "다시 켜려면 'resume-monitoring'"));
        else if (f.AgentRunning == true)
        {
            var stale = f.AgentLastCheck is { } t && f.Now - t > StaleCheck;
            var problem = f.AgentFailures > 0 || f.AgentLastCode is not (null or "NoChange" or "Updated" or "RateLimited");
            lines.Add(stale
                ? new("자동 연결 관리", Health.Warn, $"실행 중이지만 마지막 점검이 {Ago(f.Now - f.AgentLastCheck!.Value)}입니다.")
                : problem
                    ? new("자동 연결 관리", Health.Warn, $"실행 중, 최근 점검에서 문제가 {Math.Max(1, f.AgentFailures)}번 있었습니다.", "자세한 내용은 기록 파일을 확인하세요.")
                    : new("자동 연결 관리", Health.Good, $"실행 중, 1분마다 점검 (마지막 점검: {(f.AgentLastCheck is { } c ? Ago(f.Now - c) : "아직 없음")})"));
        }
        else if (!isOwner)
            lines.Add(new("자동 연결 관리", Health.Unknown, $"{f.OwnerName} 계정에서 동작합니다. 지금 계정({f.CurrentUser})에서는 상태를 볼 수 없습니다."));
        else
        {
            lines.Add(new("자동 연결 관리", Health.Bad, "실행되고 있지 않습니다."));
            next.Add($"{f.OwnerName ?? "설정한 계정"}으로 로그아웃 후 다시 로그인하면 자동으로 시작됩니다.");
        }

        // 4. 백그라운드 연결 관리 프로그램(서비스)
        lines.Add(f.Service switch
        {
            ServiceState.Running => new("백그라운드 연결 관리 프로그램", Health.Good, "실행 중 (컴퓨터를 켜면 자동 시작)"),
            ServiceState.Stopped => new("백그라운드 연결 관리 프로그램", Health.Bad, "멈춰 있습니다. WSL 주소가 바뀌면 연결이 끊길 수 있습니다."),
            ServiceState.NotInstalled => new("백그라운드 연결 관리 프로그램", Health.Off, "설치되지 않았습니다."),
            _ => new("백그라운드 연결 관리 프로그램", Health.Warn, "상태가 정상적이지 않습니다."),
        });
        if (f.Service == ServiceState.Stopped) next.Add("관리자 창에서 'autostart install'을 다시 실행하면 복구됩니다.");
        if (f.Service == ServiceState.NotInstalled) next.Add("자동 실행을 쓰려면 관리자 창에서 'autostart install'을 실행하세요.");

        // 5. 로그인 시 자동 실행
        lines.Add(f.TaskRegistered switch
        {
            true => new("로그인 시 자동 실행", Health.Good, $"등록됨 ({f.OwnerName}으로 로그인하면 시작)"),
            false => new("로그인 시 자동 실행", Health.Off, "등록되지 않았습니다."),
            null => new("로그인 시 자동 실행", Health.Unknown, "확인하지 못했습니다."),
        });

        // 6. 연결 통로·방화벽
        var routeOk = f.ForwardRuleExists == true && f.Listening == true;
        lines.Add(new("연결 통로", routeOk ? Health.Good : f.ForwardRuleExists == false ? Health.Bad : Health.Warn,
            f.ForwardRuleExists == false ? "연결 전달 규칙이 없습니다."
            : f.Listening == false ? "규칙은 있지만 아직 열리지 않았습니다(보통 1분 안에 자동 복구)."
            : $"열려 있음 (외부 {f.ListenPort}번 → WSL SSH)"));
        if (f.ForwardRuleExists == false) next.Add("관리자 창에서 'forward apply'로 연결 통로를 다시 만드세요.");
        lines.Add(f.FirewallRuleExists switch
        {
            true => new("방화벽", Health.Good, "같은 공유기에 연결된 기기만 허용"),
            false => new("방화벽", Health.Bad, "허용 규칙이 없어 다른 PC에서 접속이 막힙니다."),
            null => new("방화벽", Health.Unknown, "확인하지 못했습니다."),
        });

        foreach (var svc in f.Services)
            lines.Add(new($"추가 서비스 {svc.Name}", svc.Listening == true ? Health.Good : svc.Listening == false ? Health.Warn : Health.Unknown,
                svc.Listening == true ? $"열려 있음 (외부 {svc.ListenPort}번 → WSL {svc.ConnectPort}번, WSL 안 프로그램이 실행 중이어야 접속됨)"
                : svc.Listening == false ? $"외부 {svc.ListenPort}번 통로가 열려 있지 않습니다(보통 1분 안에 자동 복구)." : "확인하지 못했습니다."));

        // 7. 이 PC 주소(DHCP 여부)
        if (f.ListenAddressPresent == false)
        {
            lines.Add(new("이 PC 주소", Health.Bad,
                $"접속 주소 {f.ListenAddress}가 이 PC에 없습니다{(f.CurrentAddress is { } now ? $"(지금 주소: {now})" : "")}. 주소가 바뀐 것 같습니다."));
            next.Add("공유기에서 이 PC 주소를 고정 배정(DHCP 예약)하거나, 'forward plan'으로 새 주소에 맞춰 다시 설정하세요.");
        }
        else if (f.AddressIsDhcp == true)
            lines.Add(new("이 PC 주소", Health.Warn, $"{f.ListenAddress} (공유기가 자동으로 정한 주소라 바뀔 수 있음)",
                "공유기에서 이 PC 주소를 고정 배정(DHCP 예약)하면 안전합니다."));
        else if (f.AddressIsDhcp == false)
            lines.Add(new("이 PC 주소", Health.Good, $"{f.ListenAddress} (고정 주소)"));

        if (f.SshAnswers == false && f.Paused != true && f.WslRunning == true && f.Listening == true)
            next.Add("WSL 안의 SSH 서버 상태를 확인해야 합니다.");

        var overall = f.SshAnswers == true
            ? (lines.Any(l => l.Health is Health.Bad or Health.Warn) ? Health.Warn : Health.Good)
            : f.Paused == true ? Health.Off : Health.Bad;
        var headline = overall switch
        {
            Health.Good => "모두 정상입니다. 다른 PC에서 SSH로 접속할 수 있습니다.",
            Health.Warn => "SSH 접속은 되지만 확인이 필요한 항목이 있습니다.",
            Health.Off => "멈춤 상태입니다. 'resume-monitoring'으로 다시 켤 수 있습니다.",
            _ => "지금은 다른 PC에서 SSH로 접속할 수 없습니다.",
        };
        return new StatusReport(overall, headline, lines, next.Distinct().ToList());
    }

    private static string Ago(TimeSpan d) =>
        d.TotalSeconds < 60 ? "방금" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}분 전" : d.TotalHours < 24 ? $"{(int)d.TotalHours}시간 전" : $"{(int)d.TotalDays}일 전";
}
