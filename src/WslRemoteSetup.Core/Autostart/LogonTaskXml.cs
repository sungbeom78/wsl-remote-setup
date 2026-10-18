using System.Security;
using System.Text.RegularExpressions;

namespace WslRemoteSetup.Core.Autostart;

/// <summary>
/// 소유자 작업(작업 스케줄러 XML, FR-07, S05).
/// - Build: 소유자 로그온 트리거 + 대화형 토큰(InteractiveToken). 비밀번호를 저장하지 않는다.
/// - BuildBoot: 시스템 시작 트리거 + S4U(로그온 여부와 관계없이 실행, 암호 저장 안 함) — 로그인 전 무인 실행 POC.
/// 공통: 권한 수준 LeastPrivilege(관리자 그룹 구성원이어도 비승격), 한 인스턴스만, 실행 시간 제한 없음, 실패 시 재시작.
/// </summary>
public static partial class LogonTaskXml
{
    [GeneratedRegex(@"^S-1-5-21-\d+-\d+-\d+-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex UserSid();

    public static string Build(string ownerSid, string command, string arguments, string description) =>
        Compose(ownerSid, command, arguments, description,
            $"""
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(ownerSid)}</UserId>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
            """,
            "InteractiveToken");

    public static string BuildBoot(string ownerSid, string command, string arguments, string description) =>
        Compose(ownerSid, command, arguments, description,
            """
                <BootTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT30S</Delay>
                </BootTrigger>
            """,
            "S4U");

    private static string Compose(string ownerSid, string command, string arguments, string description, string trigger, string logonType)
    {
        if (!UserSid().IsMatch(ownerSid)) throw new ArgumentException("로컬/도메인 사용자 SID가 아닙니다.", nameof(ownerSid));
        if (!Path.IsPathFullyQualified(command)) throw new ArgumentException("실행 파일은 절대 경로여야 합니다.", nameof(command));

        static string X(string s) => SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>WslRemoteSetup</Author>
                <Description>{X(description)}</Description>
              </RegistrationInfo>
              <Triggers>
            {trigger}
              </Triggers>
              <Principals>
                <Principal id="Owner">
                  <UserId>{X(ownerSid)}</UserId>
                  <LogonType>{logonType}</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Owner">
                <Exec>
                  <Command>{X(command)}</Command>
                  <Arguments>{X(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
