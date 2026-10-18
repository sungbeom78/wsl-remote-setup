using System.Globalization;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Core.Setup;

public sealed record Answer<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
}

public static class Answer
{
    public static Answer<T> Accept<T>(T value) => new(value, null);
    public static Answer<T> Reject<T>(string error) => new(default, error);
}

public enum PortVerdict { Available, InUse, ProxyInUse, Excluded, Reserved, Invalid }

public sealed record PortCheck(int Port, PortVerdict Verdict, string Reason)
{
    public bool Usable => Verdict == PortVerdict.Available;
}

/// <summary>
/// 외부 SSH 포트 확인·추천. 이미 쓰이는 포트, Windows가 예약한 범위(Hyper-V 등), 잘 알려진 Windows 서비스 포트를 피한다.
/// 기존 서비스는 바꾸지 않고, 겹치면 다른 번호를 제안한다(FR-05, T04).
/// </summary>
public static class PortAdvisor
{
    /// <summary>원격 데스크톱·파일 공유·원격 관리 등 다른 용도로 흔히 쓰이는 포트.</summary>
    private static readonly HashSet<int> WellKnownWindows = [135, 137, 138, 139, 445, 3389, 5357, 5985, 5986];

    /// <summary>`netsh interface ipv4 show excludedportrange protocol=tcp` 출력에서 숫자 두 개가 있는 줄만 읽는다(현지화 무관).</summary>
    public static IReadOnlyList<(int Start, int End)> ParseExcludedRanges(string output)
    {
        var list = new List<(int, int)>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var nums = line.Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
                .ToList();
            if (nums.Count == 2 && nums.All(n => n is >= 1 and <= 65535) && nums[0] <= nums[1])
                list.Add((nums[0], nums[1]));
        }
        return list;
    }

    public static PortCheck Check(
        int port,
        IReadOnlyCollection<int> listening,
        IReadOnlyCollection<int> proxyPorts,
        IReadOnlyList<(int Start, int End)> excluded,
        int? rdpPort,
        int? ownedPort = null)
    {
        if (!InputValidator.Port(port).IsValid) return new(port, PortVerdict.Invalid, "1~65535 사이의 숫자여야 합니다.");
        if (port == rdpPort) return new(port, PortVerdict.Reserved, "원격 데스크톱이 쓰는 포트입니다.");
        if (WellKnownWindows.Contains(port)) return new(port, PortVerdict.Reserved, "Windows의 다른 기능이 쓰는 포트입니다.");
        if (port == ownedPort) return new(port, PortVerdict.Available, "이 도구가 이미 쓰고 있는 포트입니다.");
        if (proxyPorts.Contains(port)) return new(port, PortVerdict.ProxyInUse, "다른 연결 전달 규칙이 이미 쓰고 있습니다.");
        if (listening.Contains(port)) return new(port, PortVerdict.InUse, "다른 프로그램이 이미 쓰고 있습니다.");
        if (excluded.Any(r => port >= r.Start && port <= r.End)) return new(port, PortVerdict.Excluded, "Windows가 다른 용도로 예약해 둔 범위입니다.");
        return new(port, PortVerdict.Available, "사용할 수 있습니다.");
    }

    /// <summary>요청한 포트부터 위로 올라가며 처음 쓸 수 있는 포트를 찾는다(최대 2000개 탐색).</summary>
    public static int? Suggest(int from, IReadOnlyCollection<int> listening, IReadOnlyCollection<int> proxyPorts,
        IReadOnlyList<(int Start, int End)> excluded, int? rdpPort)
    {
        for (var p = Math.Max(1, from); p <= Math.Min(65535, from + 2000); p++)
            if (Check(p, listening, proxyPorts, excluded, rdpPort).Usable) return p;
        return null;
    }
}

/// <summary>설치 질문의 입력 해석(빈 입력 = 기본값). 화면 입출력과 분리해 단위 테스트한다.</summary>
public static class SetupQuestions
{
    public static Answer<int> Port(string? input, int defaultPort)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return Answer.Accept(defaultPort);
        return int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && InputValidator.Port(p).IsValid
            ? Answer.Accept(p)
            : Answer.Reject<int>("1~65535 사이의 숫자로 입력해 주세요.");
    }

    public static Answer<string> LinuxUser(string? input, string defaultUser)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) t = defaultUser;
        var v = InputValidator.LinuxUserName(t);
        return v.IsValid ? Answer.Accept(t) : Answer.Reject<string>(v.Error!);
    }

    /// <summary>WSL을 쓸 Windows 계정. 존재 여부는 호출자가 계정 목록으로 확인한다.</summary>
    public static Answer<string> WindowsAccount(string? input, string defaultAccount, IReadOnlyCollection<string> existing)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) t = defaultAccount;
        var v = InputValidator.WindowsAccountName(t);
        if (!v.IsValid) return Answer.Reject<string>(v.Error!);
        var match = existing.FirstOrDefault(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? Answer.Accept(match)
            : Answer.Reject<string>($"'{t}' 계정이 이 PC에 없습니다. 새 계정 만들기는 다음 버전에서 지원합니다.");
    }

    /// <summary>1 = 비밀번호+공개키(기본), 2 = 공개키만.</summary>
    public static Answer<SshAuthentication> SshAuth(string? input)
    {
        var t = (input ?? "").Trim();
        return t switch
        {
            "" or "1" => Answer.Accept(SshAuthentication.PasswordAndPublicKey),
            "2" => Answer.Accept(SshAuthentication.PublicKeyOnly),
            _ => Answer.Reject<SshAuthentication>("1 또는 2로 입력해 주세요."),
        };
    }
}
