using System.Diagnostics;
using System.Runtime.Versioning;
using WslRemoteSetup.Core;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Network;

namespace WslRemoteSetup.Lifetime;

/// <summary>
/// M3 실기 POC: WSL 인스턴스 수명 실험.
/// A) 일회성 명령 후 유휴 종료까지 시간, B) keeper 보유 중 유지 여부와 주소 안정성, C) keeper 해제 후 유휴 종료.
/// 배포판을 기동하지만 terminate/shutdown/unregister는 호출하지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LifetimePoc(string distro, TimeSpan hold, TimeSpan idleMax)
{
    private static readonly string WslExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
    private readonly CommandRunner _runner = CommandRunner.ForWindowsSystem();
    private readonly Stopwatch _clock = new();
    public List<string> Timeline { get; } = [];
    public List<CheckResult> Results { get; } = [];

    public async Task RunAsync(CancellationToken ct)
    {
        _clock.Start();
        var initial = await RunningAsync(ct);
        Log($"시작 상태: {Describe(initial)}");

        // A. 기준선
        Log("A. 일회성 명령 실행(/bin/true)");
        var once = await _runner.RunAsync(new CommandSpec(WslExe, ["--distribution", distro, "--exec", "/bin/true"]) { Timeout = TimeSpan.FromSeconds(60) }, ct);
        Log($"   종료코드 {once.ExitCode}, {once.Duration.TotalSeconds:F1}s");
        var ipA = await ReadAddressAsync(ct);
        Log($"   eth0 주소: {ipA ?? "확인 불가"}");
        var idleA = await WaitForStopAsync(ct);
        Results.Add(idleA is { } a
            ? new("POC_A_IDLE", "A. 클라이언트 없음 → 유휴 종료", CheckStatus.Pass, $"{a.TotalSeconds:F0}초 후 Stopped (systemd 사용 중에도 인스턴스가 유지되지 않음)")
            : new("POC_A_IDLE", "A. 클라이언트 없음 → 유휴 종료", CheckStatus.Warn, $"{idleMax.TotalSeconds:F0}초 안에 종료되지 않음"));

        // B. keeper 보유
        Log("B. keeper 시작(wsl.exe --exec /bin/sleep infinity)");
        using var keeper = new WslLifetimeKeeper(distro);
        keeper.Start();
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        var ipB1 = await ReadAddressAsync(ct);
        Log($"   keeper PID {keeper.ProcessId}, eth0 {ipB1 ?? "확인 불가"}");
        var deadline = _clock.Elapsed + hold;
        var allRunning = true;
        while (_clock.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            var running = await RunningAsync(ct);
            var on = running?.Contains(distro, StringComparer.OrdinalIgnoreCase) == true;
            allRunning &= on && keeper.IsHolding;
            Log($"   {(on ? "Running" : running is null ? "조회 실패" : "Stopped")}, keeper {(keeper.IsHolding ? "보유" : $"종료({keeper.ExitCode})")}");
        }
        var ipB2 = await ReadAddressAsync(ct);
        Log($"   보유 종료 시점 eth0 {ipB2 ?? "확인 불가"}");
        Results.Add(new("POC_B_HOLD", $"B. keeper 보유 {hold.TotalSeconds:F0}초 동안 유지", allRunning ? CheckStatus.Pass : CheckStatus.Fail,
            allRunning ? "모든 표본에서 Running" : "보유 중 Stopped 또는 keeper 종료 관찰"));
        Results.Add(new("POC_B_IP", "B. 보유 중 WSL 주소 안정", ipB1 is not null && ipB1 == ipB2 ? CheckStatus.Pass : CheckStatus.Warn,
            $"{ipB1 ?? "?"} → {ipB2 ?? "?"} (A 단계 {ipA ?? "?"})"));

        // C. 해제
        Log("C. keeper 해제(이 도구가 띄운 wsl.exe 클라이언트만 종료)");
        keeper.Release();
        var idleC = await WaitForStopAsync(ct);
        Results.Add(idleC is { } c
            ? new("POC_C_RELEASE", "C. 해제 후 유휴 종료", CheckStatus.Pass, $"{c.TotalSeconds:F0}초 후 Stopped. 이후 재기동 없음 = 자동 복구가 없을 때 pause 의도 유지")
            : new("POC_C_RELEASE", "C. 해제 후 유휴 종료", CheckStatus.Warn, $"{idleMax.TotalSeconds:F0}초 안에 종료되지 않음"));

        Results.Add(new("POC_LOGOFF", "로그아웃·재부팅 시 유지", CheckStatus.NotTested, "세션 단절 시험은 별도 승인 대상"));
    }

    private async Task<TimeSpan?> WaitForStopAsync(CancellationToken ct)
    {
        var start = _clock.Elapsed;
        while (_clock.Elapsed - start < idleMax)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var running = await RunningAsync(ct);
            if (running is not null && !running.Contains(distro, StringComparer.OrdinalIgnoreCase))
            {
                var t = _clock.Elapsed - start;
                Log($"   Stopped 관찰 (+{t.TotalSeconds:F0}s)");
                return t;
            }
        }
        Log("   제한 시간 내 Stopped 관찰 안 됨");
        return null;
    }

    private Task<IReadOnlyList<string>?> RunningAsync(CancellationToken ct) => WslLifetimeKeeper.RunningDistrosAsync(_runner, ct);

    private async Task<string?> ReadAddressAsync(CancellationToken ct)
    {
        var addr = await _runner.RunAsync(new CommandSpec(WslExe, ["--distribution", distro, "--exec", "/usr/bin/ip", "-j", "-4", "addr", "show"]) { Timeout = TimeSpan.FromSeconds(30) }, ct);
        var route = await _runner.RunAsync(new CommandSpec(WslExe, ["--distribution", distro, "--exec", "/usr/bin/ip", "-j", "route", "show", "default"]) { Timeout = TimeSpan.FromSeconds(30) }, ct);
        if (!addr.ExitedZero || !route.ExitedZero) return null;
        try
        {
            var sel = WslAddressSelector.Select(WslAddressSelector.ParseIpAddrJson(addr.StandardOutput), WslAddressSelector.ParseDefaultRouteDevice(route.StandardOutput));
            return sel.Address?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Describe(IReadOnlyList<string>? running) =>
        running is null ? "조회 실패" : running.Count == 0 ? "실행 중인 배포판 없음" : "실행 중: " + string.Join(", ", running);

    private void Log(string line)
    {
        var entry = $"[{_clock.Elapsed:mm\\:ss}] {line}";
        Timeline.Add(entry);
        Console.WriteLine(entry);
    }
}
