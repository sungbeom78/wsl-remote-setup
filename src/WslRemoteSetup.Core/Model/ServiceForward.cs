using System.Text.RegularExpressions;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Model;

/// <summary>
/// SSH 외에 추가로 여는 서비스 포트(예: 웹 8080 → WSL 80). SSH와 같은 WSL 주소로 전달되며,
/// 같은 LAN 한정 방화벽·자동 갱신·수신 자가 복구를 받는다. TCP만 지원한다.
/// </summary>
public sealed partial record ServiceForward(string Name, int ListenPort, int ConnectPort)
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    public static IReadOnlyList<string> Validate(IReadOnlyList<ServiceForward> services, int sshListenPort)
    {
        var e = new List<string>();
        foreach (var s in services)
        {
            if (!NameRegex().IsMatch(s.Name)) e.Add($"서비스 이름 '{s.Name}'은 영문 소문자·숫자·-만 쓸 수 있습니다(16자 이하).");
            if (s.Name == "ssh") e.Add("'ssh'는 기본 SSH 연결이 쓰는 이름입니다.");
            if (!InputValidator.Port(s.ListenPort).IsValid || !InputValidator.Port(s.ConnectPort).IsValid) e.Add($"서비스 '{s.Name}'의 포트가 올바르지 않습니다.");
            if (s.ListenPort == sshListenPort) e.Add($"서비스 '{s.Name}'의 포트 {s.ListenPort}번은 SSH가 쓰고 있습니다.");
        }
        if (services.GroupBy(s => s.ListenPort).Any(g => g.Count() > 1)) e.Add("같은 외부 포트를 두 서비스가 쓸 수 없습니다.");
        if (services.GroupBy(s => s.Name).Any(g => g.Count() > 1)) e.Add("서비스 이름이 겹칩니다.");
        if (services.Count > 10) e.Add("추가 서비스는 10개까지 열 수 있습니다.");
        return e;
    }
}
