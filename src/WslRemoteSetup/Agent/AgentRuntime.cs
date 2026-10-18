using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using WslRemoteSetup.Core.Agent;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Ipc;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Ipc;
using WslRemoteSetup.Lifetime;

namespace WslRemoteSetup.Agent;

/// <summary>소유자 에이전트용 사용자 경로(비권한). 관리자 코드는 이 파일을 명령으로 해석하지 않는다.</summary>
[SupportedOSPlatform("windows")]
internal static class UserPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslRemoteSetup");
    public static string PauseFile => Path.Combine(Root, "user-state", "agent.json");
    public static string StatusFile => Path.Combine(Root, "user-state", "agent-status.json");
    public static string LogFile => Path.Combine(Root, "logs", "agent.log");

    public static void WriteJsonAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, StateStore.JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

internal sealed record PauseState(bool Paused, DateTimeOffset UpdatedAt);

[SupportedOSPlatform("windows")]
internal sealed class FilePauseSource : IPauseSource
{
    public bool IsPaused()
    {
        try
        {
            if (!File.Exists(UserPaths.PauseFile)) return false;
            return JsonSerializer.Deserialize<PauseState>(File.ReadAllText(UserPaths.PauseFile), StateStore.JsonOptions)?.Paused == true;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return true; // 읽을 수 없으면 사용자 의도를 모르는 것이므로 WSL을 다시 시작하지 않는 쪽을 택한다.
        }
    }

    public static void Set(bool paused) => UserPaths.WriteJsonAtomic(UserPaths.PauseFile, new PauseState(paused, DateTimeOffset.Now));
}

/// <summary>크기 제한(1 MB × 3개 보관) 로컬 로그. 비밀값은 기록하지 않는다.</summary>
[SupportedOSPlatform("windows")]
internal sealed class FileAgentLog : IAgentLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            var path = UserPaths.LogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                for (var i = 2; i >= 1; i--)
                {
                    var src = $"{path}.{i}";
                    if (File.Exists(src)) File.Move(src, $"{path}.{i + 1}", overwrite: true);
                }
                File.Move(path, path + ".1", overwrite: true);
            }
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            File.AppendAllText(path, line, new UTF8Encoding(false));
            if (Environment.UserInteractive && !Console.IsOutputRedirected) Console.Write(line);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class KeeperAdapter(string distro) : IKeeper, IDisposable
{
    private WslLifetimeKeeper? _keeper;
    public bool IsHolding => _keeper?.IsHolding == true;

    public void Start()
    {
        _keeper?.Dispose();
        _keeper = new WslLifetimeKeeper(distro);
        _keeper.Start();
    }

    public void Release() => _keeper?.Release();
    public void Dispose() => _keeper?.Dispose();
}

[SupportedOSPlatform("windows")]
internal sealed class WslAddressReaderAdapter(ICommandRunner runner, string distro) : IWslAddressReader
{
    public async Task<IPAddress?> ReadAsync(CancellationToken ct)
    {
        try { return await WindowsForwarding.ReadWslAddressAsync(runner, distro); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PipeRepairClient(Guid installationId) : IRepairClient
{
    public async Task<RepairResponse> SendAsync(IPAddress observed, CancellationToken ct)
    {
        try
        {
            return await RepairPipeClient.SendAsync(RepairPipe.PipeName(installationId), new RepairRequest(installationId, observed), TimeSpan.FromSeconds(15), ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new RepairResponse(RepairCode.Error, $"관리부 연결 실패: {ex.GetType().Name}");
        }
    }
}

/// <summary>`agent` 명령: 소유자 로그온 후 실행되는 상주 루프(한 인스턴스).</summary>
[SupportedOSPlatform("windows")]
internal static class AgentCommand
{
    public static async Task<int> RunAsync(Guid installationId, string distro, int? maxTicks = null)
    {
        using var mutex = new Mutex(initiallyOwned: true, $"Local\\WslRemoteSetup.Agent.{installationId:N}", out var created);
        if (!created)
        {
            Console.Error.WriteLine("에이전트가 이미 실행 중입니다(한 인스턴스만 허용).");
            return App.ExitOk;
        }

        var log = new FileAgentLog();
        using var keeper = new KeeperAdapter(distro);
        var loop = new AgentLoop(
            keeper,
            new WslAddressReaderAdapter(WindowsForwarding.Runner(), distro),
            new PipeRepairClient(installationId),
            new FilePauseSource(),
            log,
            TimeProvider.System);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        log.Info($"에이전트 시작: 설치 {installationId:D}, 배포판 {distro}");
        var ticks = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var delay = await loop.TickAsync(cts.Token);
                UserPaths.WriteJsonAtomic(UserPaths.StatusFile, new
                {
                    installationId,
                    distro,
                    paused = new FilePauseSource().IsPaused(),
                    keeper = keeper.IsHolding,
                    lastConfirmed = loop.LastConfirmed?.ToString(),
                    lastConfirmedAt = loop.LastConfirmedAt,
                    lastCode = loop.LastCode?.ToString(),
                    consecutiveFailures = loop.ConsecutiveFailures,
                    nextCheck = DateTimeOffset.Now + delay,
                    updatedAt = DateTimeOffset.Now,
                });
                if (maxTicks is { } m && ++ticks >= m) break;
                await Task.Delay(delay, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            keeper.Release();
            log.Info("에이전트 종료(keeper 해제)");
        }
        return App.ExitOk;
    }

    public static int SetPaused(bool paused)
    {
        FilePauseSource.Set(paused);
        Console.WriteLine(paused
            ? "자동 연결 관리를 잠시 멈췄습니다. 1분 안에 WSL이 꺼지고, 다시 켤 때까지 SSH 접속이 되지 않습니다."
            : "자동 연결 관리를 다시 켰습니다. 1분 안에 WSL이 켜지고 SSH 접속이 다시 됩니다.");
        if (paused)
            Console.WriteLine("참고: WSL을 직접 끄는 작업(wsl --shutdown)은 이렇게 멈춘 뒤에 하세요. 멈추지 않으면 자동으로 다시 켜집니다.");
        return App.ExitOk;
    }
}
