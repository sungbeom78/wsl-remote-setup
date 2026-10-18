using System.Globalization;
using WslRemoteSetup.Core.Status;

namespace WslRemoteSetup.Core.Power;

/// <summary>
/// 현재 전원 계획의 절전 설정(ADR 0009). 시간은 초, 0은 "사용 안 함", null은 조회 실패.
/// 서버 PC가 잠들면 노트북에서 접속할 수 없으므로 status에서 쉬운 말로 알린다.
/// </summary>
public sealed record SleepInfo(uint? AcStandbySeconds, uint? AcHibernateSeconds, bool HasBattery)
{
    public bool AcNeverSleeps => AcStandbySeconds == 0 && AcHibernateSeconds is 0 or null;
}

/// <summary>"전원 연결 시 절전 안 함" 적용 전 값(되돌리기용). 사용자 상태 파일에 저장한다.</summary>
public sealed record SleepBackup(uint AcStandbySeconds, uint AcHibernateSeconds, DateTimeOffset SavedAt);

public static class SleepAdvisor
{
    public static StatusLine Line(SleepInfo? s)
    {
        if (s is null || s.AcStandbySeconds is null)
            return new("절전", Health.Unknown, "확인하지 못했습니다.");
        if (s.AcNeverSleeps)
            return new("절전", Health.Good, "전원에 연결되어 있으면 잠들지 않습니다." + (s.HasBattery ? " (배터리 사용 중 설정은 그대로)" : ""));

        // 절전·최대 절전 중 먼저 오는 시간
        var after = new[] { s.AcStandbySeconds, s.AcHibernateSeconds }.Where(v => v is > 0).Min() ?? 0;
        return new("절전", Health.Warn,
            $"전원에 연결되어 있어도 {Duration(after)} 동안 쓰지 않으면 잠듭니다. 잠든 동안에는 다른 기기에서 접속할 수 없습니다.",
            "'power keep-awake'로 전원 연결 시 절전 안 함으로 바꿀 수 있습니다(되돌리기: 'power restore').");
    }

    public static string Duration(uint seconds)
    {
        if (seconds < 60) return string.Create(CultureInfo.InvariantCulture, $"{seconds}초");
        var m = seconds / 60;
        if (m < 60) return string.Create(CultureInfo.InvariantCulture, $"{m}분");
        var h = m / 60;
        return m % 60 == 0 ? string.Create(CultureInfo.InvariantCulture, $"{h}시간") : string.Create(CultureInfo.InvariantCulture, $"{h}시간 {m % 60}분");
    }
}
