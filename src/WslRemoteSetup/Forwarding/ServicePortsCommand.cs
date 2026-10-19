using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WslRemoteSetup.Agent;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Ipc;
using WslRemoteSetup.Core.Model;
using WslRemoteSetup.Core.Setup;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Ipc;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;
using WslRemoteSetup.Setup;

namespace WslRemoteSetup.Forwarding;

/// <summary>
/// 서비스 포트 관리(ADR 0011): WSL 안 서비스를 다른 기기에서 쓰도록 연 포트의 목록·추가·삭제·지금 연결.
/// 열어 둔 포트는 재부팅 후에도 백그라운드 연결 관리 프로그램이 유지한다. WSL 안 서비스가 꺼져 있어도 통로는 그대로 둔다.
/// 추가·삭제는 관리자 창(UAC)에서, 목록·지금 연결은 일반 권한으로 한다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServicePortsCommand
{
    private sealed record Ctx(InstallState State, string Listen, int SshPort, string? Target, string Distro);

    public static async Task<int> RunAsync(string sub) => sub switch
    {
        "" => await MenuAsync(),
        "list" => await ListAsync(),
        "connect" => await ConnectNowAsync(),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("사용: ports | ports list | ports connect");
        return App.ExitUsage;
    }

    private static Ctx? Load()
    {
        var state = new StateStore(ProtectedPaths.StateFile).Load().State;
        var ssh = state?.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy && (r.Description?.Contains("SSH", StringComparison.Ordinal) ?? false));
        if (state is null || ssh is null)
        {
            Ui.Blocked("SSH 연결 통로가 아직 없습니다. 먼저 메뉴 1(새로 설치)을 마쳐 주세요.");
            return null;
        }
        var sep = ssh.Key.LastIndexOf(':');
        var target = ssh.Value is { } v ? v[..v.LastIndexOf(':')] : null;
        return new Ctx(state, ssh.Key[..sep], int.Parse(ssh.Key[(sep + 1)..], CultureInfo.InvariantCulture), target, state.DistroName ?? "Ubuntu-24.04");
    }

    private static async Task<int> MenuAsync()
    {
        if (Console.IsInputRedirected) { Console.Error.WriteLine("질문에 답할 수 있는 창에서 실행해 주세요."); return App.ExitUserInputRequired; }
        while (true)
        {
            var ctx = Load();
            if (ctx is null) return App.ExitChecksFailed;
            await PrintTableAsync(ctx);
            Console.WriteLine();
            Ui.Line("1. 포트 추가   2. 포트 삭제   3. 지금 연결   0. 돌아가기");
            Console.Write("  선택 [0]: ");
            switch ((Console.ReadLine() ?? "").Trim())
            {
                case "" or "0": return App.ExitOk;
                case "1": await AddAsync(ctx); break;
                case "2": Remove(ctx); break;
                case "3": await ConnectNowAsync(); break;
                default: Ui.Line("잘못된 선택입니다."); break;
            }
        }
    }

    private static async Task<int> ListAsync()
    {
        var ctx = Load();
        if (ctx is null) return App.ExitChecksFailed;
        await PrintTableAsync(ctx);
        return App.ExitOk;
    }

    /// <summary>열어 둔 포트 표: 통로(이 PC 수신)와 WSL 안 서비스 응답을 따로 보여 준다.</summary>
    private static async Task PrintTableAsync(Ctx ctx)
    {
        Ui.Title("서비스 포트 관리");
        Ui.Line($"다른 기기에서 '{ctx.Listen}:외부 포트'로 접속하면 WSL 안 서비스로 연결됩니다.");
        Ui.Line("열어 둔 포트는 컴퓨터나 WSL을 다시 시작해도 자동으로 유지됩니다.");
        Console.WriteLine();
        if (ctx.State.Services.Count == 0)
        {
            Ui.Line("아직 열어 둔 서비스 포트가 없습니다. (SSH는 기본으로 열려 있습니다)");
            return;
        }
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        Ui.Line($"{"번호",-4} {"이름",-14} {"외부 포트",-10} {"WSL 포트",-9} {"통로",-6} WSL 서비스");
        var i = 0;
        foreach (var s in ctx.State.Services)
        {
            var open = listeners.Any(e => e.Port == s.ListenPort && e.Address.ToString() == ctx.Listen);
            var active = ctx.Target is { } t && await ProbeAsync(IPAddress.Parse(t), s.ConnectPort, TimeSpan.FromSeconds(1.5));
            Ui.Line($"{++i,-6} {s.Name,-14} {s.ListenPort,-12} {s.ConnectPort,-10} {(open ? "열림" : "닫힘"),-6} {(active ? "응답 있음" : "꺼짐(켜면 바로 연결)")}");
        }
    }

    public static async Task<bool> ProbeAsync(IPAddress address, int port, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var c = new TcpClient(AddressFamily.InterNetwork);
            await c.ConnectAsync(address, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException) { return false; }
    }

    private static async Task AddAsync(Ctx ctx)
    {
        Console.WriteLine();
        Ui.Line("WSL 안에서 지금 열려 있는 포트를 찾는 중입니다...");
        var runner = new CommandRunner([WindowsForwarding.Wsl]);
        var r = await runner.RunAsync(new CommandSpec(WindowsForwarding.Wsl, ["--distribution", ctx.Distro, "--user", "root", "--exec", "/usr/bin/ss", "-ltnpH"]) { Timeout = TimeSpan.FromSeconds(60) });
        var found = r.ExitedZero ? WslListeners.Parse(r.StandardOutput) : [];
        var taken = ctx.State.Services.Select(s => s.ConnectPort).Append(22).ToHashSet();
        var candidates = found.Where(l => !taken.Contains(l.Port) && l.Port != 53).ToList(); // 53: WSL 내부 DNS

        if (candidates.Count == 0) Ui.Line("새로 열 수 있는 포트를 찾지 못했습니다. WSL 포트 번호를 직접 입력할 수 있습니다.");
        for (var i = 0; i < candidates.Count; i++)
        {
            var l = candidates[i];
            Ui.Line($"  {i + 1}) {l.Port,-6} {l.Process ?? "(이름 모름)",-16}{(l.LoopbackOnly ? " ⚠ WSL 안에서만 열림(127.0.0.1)" : "")}");
        }
        Console.Write("  번호를 고르거나 WSL 포트 번호를 입력하세요 (Enter = 취소): ");
        var pick = (Console.ReadLine() ?? "").Trim();
        if (pick.Length == 0) { Ui.Line("취소했습니다."); return; }
        WslListener? chosen = null;
        if (int.TryParse(pick, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            chosen = n >= 1 && n <= candidates.Count ? candidates[n - 1]
                : InputValidator.Port(n).IsValid ? found.FirstOrDefault(l => l.Port == n) ?? new WslListener(n, false, null) : null;
        if (chosen is null) { Ui.Line("올바른 번호나 포트가 아닙니다."); return; }
        if (taken.Contains(chosen.Port)) { Ui.Line($"WSL {chosen.Port}번은 이미 열려 있거나 SSH가 쓰는 포트입니다."); return; }

        if (chosen.LoopbackOnly)
        {
            Ui.Note($"WSL {chosen.Port}번 서비스는 WSL 안(127.0.0.1)에서만 열려 있어, 통로를 만들어도 다른 기기에서 접속되지 않습니다.");
            Ui.Line("  서비스 설정에서 받는 주소를 0.0.0.0으로 바꾸면 접속됩니다(예: --host 0.0.0.0).");
            Console.Write("  그래도 통로를 만들어 둘까요? [y/N] ");
            if (Confirmation.ParseDefaultNo(Console.ReadLine()) != ConfirmAnswer.Yes) { Ui.Line("취소했습니다."); return; }
        }
        if (WslListeners.Sensitive.TryGetValue(chosen.Port, out var db))
        {
            Ui.Note($"{chosen.Port}번은 보통 {db} 같은 데이터 저장 프로그램이 씁니다. 같은 공유기의 기기만 허용되지만, 비밀번호가 약하면 위험할 수 있습니다.");
            Console.Write("  계속할까요? [y/N] ");
            if (Confirmation.ParseDefaultNo(Console.ReadLine()) != ConfirmAnswer.Yes) { Ui.Line("취소했습니다."); return; }
        }

        var names = ctx.State.Services.Select(s => s.Name).ToHashSet();
        var name = WslListeners.SuggestName(chosen, names);
        Console.Write($"  서비스 이름 [{name}]: ");
        var typedName = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (typedName.Length > 0) name = typedName;

        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToList();
        var used = listening.Concat(ctx.State.Services.Select(s => s.ListenPort)).Append(ctx.SshPort).ToList();
        var suggest = PortAdvisor.Suggest(chosen.Port < 1024 ? 8000 + chosen.Port : chosen.Port, used, [], [], null) ?? 8080;
        Console.Write($"  다른 기기에서 접속할 포트 [{suggest}]: ");
        var ext = SetupQuestions.Port(Console.ReadLine(), suggest);
        if (!ext.Ok) { Ui.Line(ext.Error!); return; }

        var candidate = new ServiceForward(name, ext.Value, chosen.Port);
        var errors = ServiceForward.Validate([.. ctx.State.Services, candidate], ctx.SshPort);
        if (errors.Count > 0) { foreach (var e in errors) Ui.Blocked(e); return; }

        Console.WriteLine();
        Ui.Line($"'{name}': 다른 기기 → {ctx.Listen}:{ext.Value} → WSL {chosen.Port}번");
        Ui.Line("  - 같은 공유기에 연결된 기기만 허용합니다(방화벽 규칙 1개 추가).");
        Ui.Line("  - 컴퓨터나 WSL을 다시 시작해도 자동으로 유지됩니다.");
        Ui.Line("  - 관리자 창에서 한 번 더 확인합니다.");
        if (!ConfirmPrompt.Ask(out _)) return;
        var res = Elevation.RunSelfElevated("forward", "add-service", "--service",
            string.Create(CultureInfo.InvariantCulture, $"{name}:{ext.Value}:{chosen.Port}"), "--pause-at-end");
        Report(res, "포트를 열었습니다.");
    }

    private static void Remove(Ctx ctx)
    {
        if (ctx.State.Services.Count == 0) { Ui.Line("삭제할 서비스 포트가 없습니다."); return; }
        Console.Write("  삭제할 번호 (Enter = 취소): ");
        var pick = (Console.ReadLine() ?? "").Trim();
        if (!int.TryParse(pick, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > ctx.State.Services.Count) { Ui.Line("취소했습니다."); return; }
        var s = ctx.State.Services[n - 1];
        Ui.Line($"'{s.Name}'(외부 {s.ListenPort}번 → WSL {s.ConnectPort}번)의 통로와 방화벽 허용 규칙을 지웁니다.");
        Ui.Line("  - 다른 기기에서 이 서비스에 접속할 수 없게 됩니다. WSL 안 서비스는 그대로입니다.");
        Ui.Line("  - 관리자 창에서 한 번 더 확인합니다.");
        if (!ConfirmPrompt.Ask(out _)) return;
        Report(Elevation.RunSelfElevated("forward", "remove-service", "--name", s.Name, "--pause-at-end"), "포트를 닫았습니다.");
    }

    private static void Report(Elevation.Result r, string ok)
    {
        if (!r.Started) Ui.Blocked(r.Error!);
        else if (r.ExitCode == App.ExitOk) Ui.Line($"✔ {ok}");
        else Ui.Blocked("완료하지 못했거나 취소되었습니다. 관리자 창의 내용을 확인해 주세요.");
    }

    /// <summary>
    /// 지금 연결: WSL을 켜고 현재 주소를 백그라운드 연결 관리 프로그램에 알려, 열어 둔 모든 포트를 즉시 맞춘다(관리자 권한 불필요).
    /// </summary>
    public static async Task<int> ConnectNowAsync()
    {
        var ctx = Load();
        if (ctx is null) return App.ExitChecksFailed;
        Console.WriteLine();
        Ui.Line("열어 둔 포트를 지금 연결합니다...");
        if (new FilePauseSource().IsPaused())
            Ui.Note("자동 연결 관리가 멈춤 상태입니다. 지금 한 번만 연결하며, 다시 켜려면 메뉴 5를 쓰세요.");
        var runner = new CommandRunner([WindowsForwarding.Wsl]);
        var ip = await WindowsForwarding.ReadWslAddressAsync(runner, ctx.Distro); // WSL이 꺼져 있으면 이때 켜진다
        if (ip is null) { Ui.Blocked("WSL 주소를 확인하지 못했습니다. WSL이 켜지는지 확인해 주세요."); return App.ExitChecksFailed; }
        RepairResponse response;
        try
        {
            response = await RepairPipeClient.SendAsync(RepairPipe.PipeName(ctx.State.InstallationId), new RepairRequest(ctx.State.InstallationId, ip), TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            Ui.Blocked("백그라운드 연결 관리 프로그램에 연결하지 못했습니다. 메뉴 3(연결 복구)을 실행해 주세요.");
            var h = Ui.OpenHistory("지금 연결");
            h.Fail($"{ex.GetType().Name}: {ex.Message}");
            h.Finish("실패");
            return App.ExitChecksFailed;
        }
        var history = Ui.OpenHistory("지금 연결");
        history.Detail("WSL 주소", ip.ToString());
        history.Detail("응답", $"{response.Code}: {response.Message}");
        history.Finish("완료");
        Ui.Line(response.Code switch
        {
            RepairCode.NoChange => "✔ 모두 이미 연결되어 있습니다.",
            RepairCode.Updated => "✔ 연결을 맞췄습니다.",
            RepairCode.EndpointUnreachable => "WSL의 SSH가 응답하지 않아 연결하지 못했습니다. 잠시 뒤 다시 시도해 주세요.",
            RepairCode.RateLimited => "방금 연결을 바꿔서 잠시 기다려야 합니다. 30초 뒤 다시 시도해 주세요.",
            _ => "연결하지 못했습니다. 메뉴 3(연결 복구)을 실행해 주세요.",
        });
        await PrintTableAsync(Load() ?? ctx);
        return response.Code is RepairCode.NoChange or RepairCode.Updated ? App.ExitOk : App.ExitChecksFailed;
    }
}
