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

public sealed record OwnedResource(string Kind, string Key, string Description, DateTimeOffset CreatedAt);

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
