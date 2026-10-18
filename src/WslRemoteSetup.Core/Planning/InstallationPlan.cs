using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslRemoteSetup.Core.Planning;

/// <summary>단계를 실행할 주체. 권한 경계를 계획 단계에서 드러낸다.</summary>
public enum StepExecutor
{
    /// <summary>현재 일반 사용자(비승격).</summary>
    CurrentUser,
    /// <summary>사용자 승인 후 UAC 승격된 관리자 작업.</summary>
    Administrator,
    /// <summary>대상 배포판 소유 Windows 계정의 일반 세션.</summary>
    OwnerSession,
    /// <summary>사람이 직접 입력·조작해야 하는 단계(비밀번호, 로그인 등).</summary>
    Person,
}

public enum ChangeNeed { NotRequired, Required, Unknown }

public enum Reversibility { NotApplicable, ToolCanRevert, ManualOnly, NotReversible }

public sealed record PlanStep
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Current { get; init; }
    public required string Target { get; init; }
    public required ChangeNeed Change { get; init; }
    public required StepExecutor Executor { get; init; }
    public bool RequiresApproval { get; init; }
    public bool MayRequireReboot { get; init; }
    public bool MayDisconnect { get; init; }
    public Reversibility Revert { get; init; } = Reversibility.NotApplicable;
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    /// <summary>이 버전에서 아직 적용 기능이 구현되지 않은 단계(M4 이후 등).</summary>
    public string? NotImplementedNote { get; init; }
}

public sealed record InstallationPlan(DateTimeOffset CreatedAt, IReadOnlyList<PlanStep> Steps)
{
    private static readonly JsonSerializerOptions HashOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 승인 범위 식별용 해시. 생성 시각은 제외하고 단계 내용만 반영한다.
    /// 승인 후 계획이 바뀌면 해시가 달라져 재승인이 필요하다.
    /// </summary>
    public string ComputeHash()
    {
        var json = JsonSerializer.Serialize(Steps, HashOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public IEnumerable<PlanStep> PendingSteps => Steps.Where(s => s.Change != ChangeNeed.NotRequired);
}
