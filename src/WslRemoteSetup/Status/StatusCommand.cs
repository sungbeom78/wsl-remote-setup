using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using WslRemoteSetup.Agent;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Status;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Lifetime;
using WslRemoteSetup.Probing;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Status;

/// <summary>
/// `status`: 서비스·자동 실행·SSH 연결 상태를 쉬운 말로 한눈에 보여 준다(ADR 0006).
/// 읽기 전용이며 WSL을 켜지 않는다. 기술 값은 작업 기록에만 남긴다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StatusCommand
{
    private static readonly string Schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("  상태를 확인하는 중입니다(아무것도 바꾸지 않습니다)...");
        var history = Ui.OpenHistory("상태 확인(읽기 전용)");
        history.Begin("상태 수집");

        var loaded = new StateStore(ProtectedPaths.StateFile).Load();
        if (loaded.Outcome is LoadOutcome.Corrupt or LoadOutcome.UnsupportedVersion)
        {
            Ui.Title("현재 상태");
            Ui.Blocked("설정 기록 파일을 읽을 수 없습니다. 기록을 지우지 않았습니다.");
            history.Fail($"상태 파일 {loaded.Outcome}: {loaded.Error}");
            history.Finish("중단");
            Ui.HistoryHint(history);
            return App.ExitChecksFailed;
        }

        var facts = await CollectAsync(loaded.State, history);
        var report = StatusEvaluator.Evaluate(facts);
        history.Done($"종합 {report.Overall}: {report.Headline}");
        history.Finish("완료");

        Print(report);
        Ui.HistoryHint(history);
        Contact.Show();
        return report.Overall is Health.Bad ? App.ExitChecksFailed : App.ExitOk;
    }

    private static async Task<StatusFacts> CollectAsync(InstallState? state, Core.Reporting.OperationHistory h)
    {
        var now = DateTimeOffset.Now;
        var current = Ui.CurrentUserName();
        var proxyRes = state?.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy);
        if (state is null || proxyRes is null)
        {
            h.Detail("설정 기록", state is null ? "없음" : "연결 통로 기록 없음");
            return new StatusFacts { Installed = false, Now = now, CurrentUser = current };
        }

        var sep = proxyRes.Key.LastIndexOf(':');
        var listen = proxyRes.Key[..sep];
        var port = int.Parse(proxyRes.Key[(sep + 1)..], CultureInfo.InvariantCulture);
        var distro = state.DistroName ?? "Ubuntu-24.04";
        var owner = SidToName(state.OwnerSid);
        h.Detail("설치 ID", state.InstallationId.ToString("D"));
        h.Detail("소유자", $"{owner} ({state.OwnerSid})");
        h.Detail("연결 통로 기록", $"{proxyRes.Key} → {proxyRes.Value}");

        var runner = new CommandRunner([Schtasks, WindowsForwarding.Wsl]);

        var service = QueryService(out var serviceRaw);
        h.Detail("서비스", serviceRaw);

        var taskName = state.ResourceOwnership.FirstOrDefault(r => r.Kind == "scheduled-task")?.Key;
        bool? task = null;
        if (taskName is null) task = false;
        else
        {
            var q = await runner.RunAsync(new CommandSpec(Schtasks, ["/Query", "/TN", taskName]) { Timeout = TimeSpan.FromSeconds(20) });
            task = q.ExitedZero;
            h.Detail("로그온 작업", $"{taskName} exit={q.ExitCode}");
        }

        var isOwner = owner is null || string.Equals(owner, current, StringComparison.OrdinalIgnoreCase);
        bool? agentRunning = null, paused = null;
        DateTimeOffset? lastCheck = null;
        string? lastCode = null;
        var failures = 0;
        if (isOwner)
        {
            agentRunning = AgentProcessRunning();
            paused = new FilePauseSource().IsPaused();
            try
            {
                if (File.Exists(UserPaths.StatusFile))
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(UserPaths.StatusFile));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("updatedAt", out var u) && u.TryGetDateTimeOffset(out var ua)) lastCheck = ua;
                    if (root.TryGetProperty("lastCode", out var lc) && lc.ValueKind == JsonValueKind.String) lastCode = lc.GetString();
                    if (root.TryGetProperty("consecutiveFailures", out var cf) && cf.TryGetInt32(out var n)) failures = n;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { h.Note($"에이전트 상태 파일 읽기 실패: {ex.Message}"); }
            h.Detail("에이전트", $"실행={agentRunning}, 멈춤={paused}, 마지막 점검={lastCheck:yyyy-MM-dd HH:mm:ss}, 결과={lastCode}, 연속 실패={failures}");
        }

        var running = await WslLifetimeKeeper.RunningDistrosAsync(runner);
        bool? wslRunning = running is null ? null : running.Contains(distro, StringComparer.OrdinalIgnoreCase);
        h.Detail("WSL 실행 중", running is null ? "확인 실패" : string.Join(",", running));

        var proxy = WindowsProbe.ProbePortProxy().FirstOrDefault(p => p.ListenAddress == listen && p.ListenPort == port);
        h.Detail("현재 연결 전달 규칙", proxy is null ? "없음" : $"{proxy.ListenAddress}:{proxy.ListenPort} → {proxy.ConnectAddress}:{proxy.ConnectPort}");

        var ruleName = ForwardingPlanBuilder.RuleName(state.InstallationId, port);
        bool? firewall = FirewallRuleExists(ruleName);
        h.Detail("방화벽 규칙", $"{ruleName}: {firewall}");

        // 접속 주소가 지금도 이 PC에 있는지, 그 네트워크가 자동 할당(DHCP)인지
        bool? present = false, dhcp = null;
        string? currentAddress = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var props = ni.GetIPProperties();
            var v4 = props.UnicastAddresses.Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(u => u.Address.ToString()).ToList();
            bool? isDhcp = null;
            try { isDhcp = props.GetIPv4Properties().IsDhcpEnabled; } catch (NetworkInformationException) { }
            if (v4.Contains(listen)) { present = true; dhcp = isDhcp; break; }
            if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) && v4.Count > 0)
                currentAddress ??= v4[0];
        }
        h.Detail("접속 주소 존재", $"{present}, DHCP={dhcp}, 현재 주소={currentAddress}");

        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port && e.Address.ToString() == listen);
        var banner = await WindowsForwarding.ReadBannerAsync(IPAddress.Parse(listen), port, TimeSpan.FromSeconds(3));
        h.Detail("수신", listening.ToString());
        h.Detail("SSH 응답", banner ?? "없음");

        return new StatusFacts
        {
            Installed = true,
            OwnerName = owner,
            CurrentUser = current,
            Distro = distro,
            ListenAddress = listen,
            ListenPort = port,
            Service = service,
            TaskRegistered = task,
            AgentRunning = agentRunning,
            Paused = paused,
            AgentLastCheck = lastCheck,
            AgentLastCode = lastCode,
            AgentFailures = failures,
            WslRunning = wslRunning,
            ForwardRuleExists = proxy is not null,
            FirewallRuleExists = firewall,
            Listening = listening,
            SshAnswers = banner?.StartsWith("SSH-2.0-", StringComparison.Ordinal) == true,
            Services = state.Services.Select(sv => (sv.Name, sv.ListenPort, sv.ConnectPort,
                (bool?)IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == sv.ListenPort && e.Address.ToString() == listen))).ToList(),
            ListenAddressPresent = present,
            AddressIsDhcp = dhcp,
            CurrentAddress = present == true ? null : currentAddress,
            Now = now,
        };
    }

    private static void Print(StatusReport r)
    {
        Ui.Title("현재 상태");
        Write(Mark(r.Overall).Symbol + " " + r.Headline, Mark(r.Overall).Color);
        Console.WriteLine();
        Console.WriteLine();
        var width = r.Lines.Max(l => DisplayWidth(l.Title)) + 2;
        foreach (var l in r.Lines)
        {
            var (symbol, color, label) = Mark(l.Health);
            Console.Write("  ");
            Write($"{symbol} {label}", color);
            Console.Write(new string(' ', Math.Max(1, 10 - DisplayWidth(label))));
            Console.Write(l.Title + new string(' ', width - DisplayWidth(l.Title)));
            Console.WriteLine(l.Text);
            if (l.Hint is not null) Console.WriteLine($"{new string(' ', 14 + width)}→ {l.Hint}");
        }
        if (r.NextSteps.Count > 0)
        {
            Console.WriteLine();
            Ui.Line("이렇게 해 보세요");
            foreach (var s in r.NextSteps) Ui.Line($"• {s}");
        }
    }

    private static (string Symbol, ConsoleColor Color, string Label) Mark(Health h) => h switch
    {
        Health.Good => ("●", ConsoleColor.Green, "정상"),
        Health.Warn => ("▲", ConsoleColor.Yellow, "확인 필요"),
        Health.Bad => ("✕", ConsoleColor.Red, "문제"),
        Health.Off => ("○", ConsoleColor.Gray, "꺼짐"),
        _ => ("?", ConsoleColor.Gray, "확인 못 함"),
    };

    private static int DisplayWidth(string s) => s.Sum(c => c is >= '가' and <= '힣' ? 2 : 1);

    private static void Write(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }

    private static string? SidToName(string? sid)
    {
        if (sid is null) return null;
        try
        {
            var name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            var i = name.IndexOf('\\');
            return i >= 0 ? name[(i + 1)..] : name;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException) { return null; }
    }

    private static ServiceState QueryService(out string raw)
    {
        using var s = new ManagementObjectSearcher($"SELECT State, StartMode FROM Win32_Service WHERE Name='{ProtectedPaths.ServiceName}'");
        foreach (ManagementObject m in s.Get())
        {
            var state = (string)m["State"];
            raw = $"{ProtectedPaths.ServiceName} {state}/{m["StartMode"]}";
            return state switch { "Running" => ServiceState.Running, "Stopped" => ServiceState.Stopped, _ => ServiceState.Other };
        }
        raw = "없음";
        return ServiceState.NotInstalled;
    }

    /// <summary>현재 사용자의 에이전트 프로세스(설치 경로 실행 파일의 agent 모드)가 있는지.</summary>
    private static bool AgentProcessRunning()
    {
        using var s = new ManagementObjectSearcher("SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE Name='WslRemoteSetup.exe'");
        foreach (ManagementObject m in s.Get())
        {
            var path = m["ExecutablePath"] as string;
            var cmd = m["CommandLine"] as string ?? "";
            if (string.Equals(path, ProtectedPaths.InstalledExe, StringComparison.OrdinalIgnoreCase)
                && cmd.Contains(" agent ", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool? FirewallRuleExists(string name)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return null;
            dynamic policy = Activator.CreateInstance(type)!;
            foreach (dynamic r in policy.Rules)
                if ((string)r.Name == name && (bool)r.Enabled) return true;
            return false;
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }
}
