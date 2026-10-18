namespace WslRemoteSetup.Core;

/// <summary>검사·검증 결과 구분. "모든 설정 완료" 대신 항목별로 보고한다.</summary>
public enum CheckStatus
{
    Pass,
    Fail,
    Warn,
    Pending,
    NotTested,
    NotApplicable,
}

public sealed record CheckResult(string Id, string Title, CheckStatus Status, string Detail)
{
    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Fail => "FAIL",
        CheckStatus.Warn => "WARN",
        CheckStatus.Pending => "PENDING",
        CheckStatus.NotTested => "NOT_TESTED",
        CheckStatus.NotApplicable => "NOT_APPLICABLE",
        _ => status.ToString(),
    };
}
