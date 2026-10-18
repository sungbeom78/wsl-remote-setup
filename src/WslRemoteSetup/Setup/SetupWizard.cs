using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using WslRemoteSetup.Agent;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Setup;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Setup;

/// <summary>사용자가 고른 설치 값(비밀값 없음). 설치 마법사가 이어서 사용한다.</summary>
internal sealed record SetupChoices(
    string OwnerAccount,
    string LinuxUser,
    int ListenPort,
    SshAuthentication SshAuth,
    string Distro,
    DateTimeOffset ChosenAt,
    IReadOnlyList<ServiceForward>? Services = null);

/// <summary>
/// 설치 질문(Enter = 기본값). 포트·계정·Linux 사용자·로그인 방식만 묻고, IP·LAN 범위·RDP 포트는 PC 설정을 그대로 쓴다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SetupWizard
{
    public static string ChoicesFile => Path.Combine(UserPaths.Root, "user-state", "choices.json");

    /// <summary>"설정 변경": 저장된 선택값을 지워 다음 설치 실행 때 다시 묻게 한다(시스템 설정은 바꾸지 않음).</summary>
    public static void ForgetChoices()
    {
        if (File.Exists(ChoicesFile)) File.Delete(ChoicesFile);
    }

    public static SetupChoices? LoadChoices()
    {
        try
        {
            return File.Exists(ChoicesFile)
                ? JsonSerializer.Deserialize<SetupChoices>(File.ReadAllText(ChoicesFile), StateStore.JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public static async Task<SetupChoices?> AskAsync(EnvironmentSnapshot s)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("질문에 답할 수 없는 환경이라 진행하지 않습니다.");
            return null;
        }

        var previous = LoadChoices();
        var current = Ui.CurrentUserName();
        Ui.Title("설치 질문 (Enter를 누르면 [ ] 안의 기본값을 씁니다)");

        // 1. 외부 SSH 포트
        var excluded = await ReadExcludedRangesAsync();
        var listening = s.TcpListenPorts ?? [];
        var proxies = (s.PortProxies ?? []).Select(p => p.ListenPort).ToList();
        var ownedPort = OwnedPort();
        var defaultPort = previous?.ListenPort ?? ownedPort ?? DesiredConfiguration.DefaultListenPort;
        var check = PortAdvisor.Check(defaultPort, listening, proxies, excluded, s.Rdp?.Port, ownedPort);
        if (!check.Usable)
        {
            var suggested = PortAdvisor.Suggest(defaultPort + 1, listening, proxies, excluded, s.Rdp?.Port);
            Ui.Note($"기본 포트 {defaultPort}번은 {check.Reason} 대신 {suggested}번을 추천합니다.");
            defaultPort = suggested ?? defaultPort;
        }
        int port = 0;
        if (!Ask($"1. 다른 PC에서 SSH로 접속할 포트 번호", defaultPort.ToString(CultureInfo.InvariantCulture), input =>
            {
                var a = SetupQuestions.Port(input, defaultPort);
                if (!a.Ok) return a.Error;
                var c = PortAdvisor.Check(a.Value, listening, proxies, excluded, s.Rdp?.Port, ownedPort);
                if (!c.Usable)
                {
                    var alt = PortAdvisor.Suggest(a.Value + 1, listening, proxies, excluded, s.Rdp?.Port);
                    return $"{a.Value}번은 {c.Reason}{(alt is { } x ? $" {x}번은 어떨까요?" : "")}";
                }
                port = a.Value;
                return null;
            })) return null;

        // 2. WSL을 쓸 Windows 계정
        var accounts = (s.Accounts ?? []).Where(a => a.Enabled).Select(a => a.Name).ToList();
        var defaultOwner = previous?.OwnerAccount ?? current;
        string owner = defaultOwner;
        if (!Ask("2. WSL을 쓸 Windows 계정", defaultOwner, input =>
            {
                var a = SetupQuestions.WindowsAccount(input, defaultOwner, accounts);
                if (!a.Ok) return a.Error;
                owner = a.Value!;
                return null;
            })) return null;
        if (!string.Equals(owner, current, StringComparison.OrdinalIgnoreCase))
            Ui.Note($"WSL 설치와 Linux 설정은 {owner} 계정으로 로그인한 상태에서 진행해야 합니다. 설치 중에 안내합니다.");

        // 3. Linux 사용자
        var defaultLinux = previous?.LinuxUser ?? DesiredConfiguration.DefaultLinuxUser;
        string linuxUser = defaultLinux;
        if (!Ask("3. WSL(Ubuntu) 안에서 쓸 사용자 이름", defaultLinux, input =>
            {
                var a = SetupQuestions.LinuxUser(input, defaultLinux);
                if (!a.Ok) return a.Error;
                linuxUser = a.Value!;
                return null;
            })) return null;

        // 4. SSH 로그인 방식
        Console.WriteLine();
        Ui.Line("4. SSH 로그인 방식");
        Ui.Line("   1) 비밀번호 또는 키로 로그인 (기본, mRemoteNG 같은 도구에서 쓰기 쉬움)");
        Ui.Line("   2) 키로만 로그인 (더 안전, 접속하는 PC에 키 준비 필요)");
        var auth = SshAuthentication.PasswordAndPublicKey;
        if (!Ask("   선택", "1", input =>
            {
                var a = SetupQuestions.SshAuth(input);
                if (!a.Ok) return a.Error;
                auth = a.Value;
                return null;
            })) return null;


        // 5. (선택) 추가 서비스 포트
        var services = new List<ServiceForward>(previous?.Services ?? []);
        Console.WriteLine();
        Console.Write($"  5. SSH 말고 더 열 포트가 있나요? (예: 웹 서버) [y/N] ");
        if (Confirmation.ParseDefaultNo(Console.ReadLine()) == ConfirmAnswer.Yes)
        {
            for (var i = 0; i < 10; i++)
            {
                Console.Write("     서비스 이름(영문 소문자, 예: web) [끝내려면 Enter]: ");
                var name = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(name)) break;
                var wslPort = 0;
                if (!Ask("     WSL 안 프로그램의 포트", "80", input => { var a = SetupQuestions.Port(input, 80); if (!a.Ok) return a.Error; wslPort = a.Value; return null; })) break;
                var taken = listening.Concat(services.Select(x => x.ListenPort)).Append(port).ToList();
                var suggest = PortAdvisor.Suggest(wslPort < 1024 ? 8000 + wslPort : wslPort, taken, proxies, excluded, s.Rdp?.Port) ?? 8080;
                var extPort = 0;
                if (!Ask("     다른 PC에서 접속할 포트", suggest.ToString(CultureInfo.InvariantCulture), input =>
                    {
                        var a = SetupQuestions.Port(input, suggest);
                        if (!a.Ok) return a.Error;
                        var c = PortAdvisor.Check(a.Value, taken, proxies, excluded, s.Rdp?.Port);
                        if (!c.Usable || a.Value == port) return $"{a.Value}번은 {(a.Value == port ? "SSH가 쓰는 포트입니다." : c.Reason)}";
                        extPort = a.Value;
                        return null;
                    })) break;
                var candidate = new ServiceForward(name, extPort, wslPort);
                var errors = ServiceForward.Validate([.. services.Where(x => x.Name != name), candidate], port);
                if (errors.Count > 0) { Console.WriteLine($"    → {errors[0]}"); continue; }
                services.RemoveAll(x => x.Name == name);
                services.Add(candidate);
                Console.WriteLine($"     추가: {name} — 외부 {extPort}번 → WSL {wslPort}번");
            }
        }
        var choices = new SetupChoices(owner, linuxUser, port, auth, previous?.Distro ?? DesiredConfiguration.DefaultDistro, DateTimeOffset.Now, services);
        UserPaths.WriteJsonAtomic(ChoicesFile, choices);

        Console.WriteLine();
        Ui.Line("선택한 값");
        Ui.Line($"  포트 {choices.ListenPort}번 · 계정 {choices.OwnerAccount} · Linux 사용자 {choices.LinuxUser} · " +
                (choices.SshAuth == SshAuthentication.PublicKeyOnly ? "키로만 로그인" : "비밀번호 또는 키"));
        foreach (var x in services) Ui.Line($"  추가 포트: {x.Name} — 외부 {x.ListenPort}번 → WSL {x.ConnectPort}번");
        Ui.Line("  이 PC의 IP 주소·공유기 범위·원격 데스크톱 포트는 지금 설정을 그대로 씁니다.");
        return choices;
    }

    public static DesiredConfiguration ToDesired(SetupChoices c) => new()
    {
        OwnerAccountName = c.OwnerAccount,
        LinuxUser = c.LinuxUser,
        ListenPort = c.ListenPort,
        SshAuth = c.SshAuth,
        DistroName = c.Distro,
    };

    /// <summary>질문 한 개. validate가 null을 돌려주면 통과, 문자열이면 그 안내를 보여 주고 다시 묻는다(최대 5회).</summary>
    private static bool Ask(string question, string defaultText, Func<string?, string?> validate)
    {
        for (var i = 0; i < 5; i++)
        {
            Console.Write($"  {question} [{defaultText}]: ");
            var error = validate(Console.ReadLine());
            if (error is null) return true;
            Console.WriteLine($"    → {error}");
        }
        Console.WriteLine("  입력이 여러 번 올바르지 않아 중단합니다. 아무것도 바꾸지 않았습니다.");
        return false;
    }

    private static async Task<IReadOnlyList<(int Start, int End)>> ReadExcludedRangesAsync()
    {
        var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        var r = await new CommandRunner([netsh]).RunAsync(new CommandSpec(netsh, ["interface", "ipv4", "show", "excludedportrange", "protocol=tcp"]) { Timeout = TimeSpan.FromSeconds(20) });
        return r.ExitedZero ? PortAdvisor.ParseExcludedRanges(r.StandardOutput) : [];
    }

    private static int? OwnedPort()
    {
        var state = new StateStore(ProtectedPaths.StateFile).Load().State;
        var key = state?.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy)?.Key;
        return key is null ? null : int.Parse(key[(key.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }
}
