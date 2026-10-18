namespace WslRemoteSetup.Core.State;

public enum InstallPhase
{
    NotStarted,
    Diagnosing,
    Planned,
    AwaitingApproval,
    Applying,
    AwaitingReboot,
    AwaitingOwnerLogin,
    AwaitingUserInput,
    AwaitingAuthenticationSetup,
    Verifying,
    Completed,
    Failed,
    RecoveryPlanned,
}

public enum StepState { NotStarted, Applying, Applied, Verified, Failed, Skipped }

public sealed record ApprovalRecord(DateTimeOffset At, string ApprovedBySid, string PlanHash, IReadOnlyList<string> StepIds);

public sealed record OwnedResource(string Kind, string Key, string Description, DateTimeOffset CreatedAt)
{
    /// <summary>리소스의 현재 적용값(예: portproxy 대상 "172.24.220.111:22"). 외부 변경 판별에 쓴다.</summary>
    public string? Value { get; init; }
}

public static class ResourceKinds
{
    public const string PortProxy = "portproxy";
    public const string FirewallRule = "firewall-rule";
}

public sealed record VerificationRecord(string Id, string Status, string Detail, DateTimeOffset At);

/// <summary>
/// 설치 상태 파일(개발 계획 9.2절). 비밀번호·토큰·개인키·환경변수 덤프를 넣지 않는다.
/// 승인 이력은 제품 내부 증적이며 권한 통제를 대신하지 않는다.
/// </summary>
public sealed record InstallState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid InstallationId { get; init; } = Guid.NewGuid();
    public string AppVersion { get; init; } = "";
    public InstallPhase Phase { get; init; } = InstallPhase.NotStarted;
    public string? OwnerSid { get; init; }
    public string? DistroName { get; init; }
    /// <summary>등록된 WSL SSH 서버 키 지문(공개 정보). 이후 명령에서 사용자가 다시 입력하지 않도록 기록한다.</summary>
    public string? SshHostKey { get; init; }
    /// <summary>SSH 외 추가로 연 서비스 포트(관리자 보호 상태에 기록, 자동 실행 정책의 원본).</summary>
    public IReadOnlyList<Model.ServiceForward> Services { get; init; } = [];
    public string? ApprovedPlanHash { get; init; }
    public IReadOnlyList<ApprovalRecord> ApprovalHistory { get; init; } = [];
    public IReadOnlyDictionary<string, StepState> StepStates { get; init; } = new Dictionary<string, StepState>();
    public IReadOnlyList<OwnedResource> ResourceOwnership { get; init; } = [];
    public IReadOnlyDictionary<string, string> BeforeSnapshots { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<VerificationRecord> VerificationResults { get; init; } = [];
    public string? PendingAction { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
