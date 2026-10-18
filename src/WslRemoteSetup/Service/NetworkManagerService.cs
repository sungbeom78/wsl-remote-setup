using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WslRemoteSetup.Core.Forwarding;
using WslRemoteSetup.Core.Ipc;
using WslRemoteSetup.Core.Policy;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Forwarding;
using WslRemoteSetup.Ipc;

namespace WslRemoteSetup.Service;

/// <summary>
/// 제한된 네트워크 관리부(ADR 0003). Windows 서비스로 실행되며 보호 경로의 정책만 신뢰한다.
/// 소유자 에이전트의 요청을 파이프로 받아 소유한 portproxy tuple 하나만 갱신한다. WSL은 실행하지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NetworkManagerService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var log = new ServiceLog();
        if (!ProtectedPaths.IsAdminOnlyWritable(ProtectedPaths.PolicyFile, out var why))
        {
            log.Write("ERROR", $"정책 파일을 신뢰할 수 없어 시작하지 않습니다: {why}");
            return;
        }

        ForwardingPolicy policy;
        try { policy = ForwardingPolicy.Parse(await File.ReadAllTextAsync(ProtectedPaths.PolicyFile, stoppingToken)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            log.Write("ERROR", $"정책 파일 오류: {ex.Message}");
            return;
        }

        var store = new StateStore(ProtectedPaths.StateFile);
        var state = store.Load().State;
        var key = string.Create(CultureInfo.InvariantCulture, $"{policy.ListenAddress}:{policy.ListenPort}");
        var recorded = state?.ResourceOwnership.FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy && r.Key == key)?.Value?.Split(':')[0];

        var runner = WindowsForwarding.Runner();
        var coordinator = new RepairCoordinator(policy, new WindowsForwardingHost(policy, runner), TimeProvider.System, recorded);
        var security = RepairPipe.CreateSecurity(new SecurityIdentifier(policy.OwnerSid));
        var server = new RepairPipeServer(RepairPipe.PipeName(policy.InstallationId), security, (caller, request, error) =>
        {
            var before = coordinator.RecordedConnectAddress;
            RepairResponse response;
            try { response = coordinator.Handle(caller, request, error); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                response = new RepairResponse(RepairCode.Error, "적용 실패");
                log.Write("ERROR", ex.Message);
            }
            if (response.Code is not RepairCode.NoChange)
                log.Write("INFO", $"요청 caller={caller} ip={request?.ObservedWslIPv4} → {response.Code}: {response.Message}");
            if (response.Code == RepairCode.Updated && coordinator.RecordedConnectAddress != before)
                PersistTarget(store, key, coordinator.RecordedConnectAddress!, policy.ConnectPort, log);
            return response;
        });

        log.Write("INFO", $"네트워크 관리부 시작: {key} → *:{policy.ConnectPort}, 소유자 {policy.OwnerSid}");

        // 수신 감시: 시작 직후, 네트워크 주소 변경 시, 그리고 60초마다 소유 규칙이 실제로 수신 중인지 확인한다.
        using var wake = new SemaphoreSlim(0);
        void OnAddressChanged(object? s, EventArgs e) => wake.Release();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnAddressChanged;
        var watcher = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(10); // 부팅 직후 주소 준비를 잠시 기다린다.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await wake.WaitAsync(delay, stoppingToken); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(60);
                try
                {
                    if (coordinator.EnsureListening())
                        log.Write("INFO", $"{key} 수신이 끊겨 있어 같은 규칙으로 다시 적용했습니다.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    log.Write("WARN", $"수신 복구 실패: {ex.Message}");
                }
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await server.ServeOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Write("WARN", $"파이프 오류: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        try { await watcher; } catch (OperationCanceledException) { }
        log.Write("INFO", "네트워크 관리부 종료");
    }

    private static void PersistTarget(StateStore store, string key, string address, int port, ServiceLog log)
    {
        try
        {
            var current = store.Load().State;
            if (current is null) return;
            var resources = current.ResourceOwnership
                .Select(r => r.Kind == ResourceKinds.PortProxy && r.Key == key
                    ? r with { Value = string.Create(CultureInfo.InvariantCulture, $"{address}:{port}") }
                    : r)
                .ToList();
            store.Save(current with { ResourceOwnership = resources });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("ERROR", $"상태 저장 실패: {ex.Message}");
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ServiceLog
{
    private readonly Lock _gate = new();

    public void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(ProtectedPaths.LogDirectory);
                var path = Path.Combine(ProtectedPaths.LogDirectory, "network-manager.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}"), new UTF8Encoding(false));
            }
            catch (IOException) { }
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class ServiceCommand
{
    public static async Task<int> RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = ProtectedPaths.ServiceName);
        builder.Services.AddHostedService<NetworkManagerService>();
        await builder.Build().RunAsync();
        return App.ExitOk;
    }
}
