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
    /// <summary>정책과 그 정책으로 만든 조정기. 주소 이동(ADR 0007) 후 새 정책으로 바꿔 끼운다.</summary>
    private sealed record Context(ForwardingPolicy Policy, RepairCoordinator Coordinator)
    {
        public string Key => string.Create(CultureInfo.InvariantCulture, $"{Policy.ListenAddress}:{Policy.ListenPort}");
    }

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
        var runner = WindowsForwarding.Runner();
        Context Build(ForwardingPolicy p)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{p.ListenAddress}:{p.ListenPort}");
            var recorded = store.Load().State?.ResourceOwnership
                .FirstOrDefault(r => r.Kind == ResourceKinds.PortProxy && r.Key == key)?.Value?.Split(':')[0];
            return new Context(p, new RepairCoordinator(p, new WindowsForwardingHost(p, runner), TimeProvider.System, recorded));
        }
        var ctx = Build(policy);
        var gate = new Lock();

        var security = RepairPipe.CreateSecurity(new SecurityIdentifier(policy.OwnerSid));
        var server = new RepairPipeServer(RepairPipe.PipeName(policy.InstallationId), security, (caller, request, error) =>
        {
            Context c;
            lock (gate) c = ctx;
            var before = c.Coordinator.RecordedConnectAddress;
            RepairResponse response;
            try { response = c.Coordinator.Handle(caller, request, error); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                response = new RepairResponse(RepairCode.Error, "적용 실패");
                log.Write("ERROR", ex.Message);
            }
            if (response.Code is not RepairCode.NoChange)
                log.Write("INFO", $"요청 caller={caller} ip={request?.ObservedWslIPv4} → {response.Code}: {response.Message}");
            if (response.Code == RepairCode.Updated && c.Coordinator.RecordedConnectAddress != before)
                PersistTarget(store, c.Key, c.Coordinator.RecordedConnectAddress!, c.Policy.ConnectPort, log);
            return response;
        });

        log.Write("INFO", $"네트워크 관리부 시작: {ctx.Key} → *:{policy.ConnectPort}, 소유자 {policy.OwnerSid}, 주소 따라가기 {(policy.FollowAddressChange ? "켬" : "끔")}");

        // 감시: 시작 직후, 네트워크 주소 변경 시, 그리고 60초마다
        //  1) 접속 주소가 바뀌었으면(DHCP) 승인된 범위 안에서만 새 주소로 옮긴다(ADR 0007)
        //  2) 소유 규칙이 실제로 수신 중인지 확인하고 끊겼으면 다시 적용한다
        using var wake = new SemaphoreSlim(0);
        void OnAddressChanged(object? s, EventArgs e) => wake.Release();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnAddressChanged;
        string? lastHold = null;
        bool? lastPathOk = null;
        var servicesChecked = false;
        var watcher = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(10); // 부팅 직후 주소 준비를 잠시 기다린다.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await wake.WaitAsync(delay, stoppingToken); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(60);
                Context c;
                lock (gate) c = ctx;
                try
                {
                    var decision = AddressFollower.Decide(c.Policy, AddressMove.ObserveAdapters());
                    if (decision.Kind == FollowKind.Move)
                    {
                        var moved = AddressMove.Apply(c.Policy, decision.NewAddress!, store, runner, log);
                        lock (gate) ctx = Build(moved);
                        lastHold = null;
                        continue;
                    }
                    if (decision.Kind == FollowKind.Hold && decision.Reason != lastHold)
                    {
                        log.Write("WARN", $"접속 주소 {c.Policy.ListenAddress}가 이 PC에 없습니다: {decision.Reason}");
                        lastHold = decision.Reason;
                    }
                    if (decision.Kind == FollowKind.Stay) lastHold = null;

                    if (c.Coordinator.EnsureListening())
                        log.Write("INFO", $"{c.Key} 수신이 끊겨 있어 같은 규칙으로 다시 적용했습니다.");

                    // 실제 경로 확인: 이 PC의 접속 주소로 들어가 WSL SSH가 응답하는지(설정 존재와 별개). 상태가 바뀔 때만 기록한다.
                    var banner = await WindowsForwarding.ReadBannerAsync(System.Net.IPAddress.Parse(c.Policy.ListenAddress), c.Policy.ListenPort, TimeSpan.FromSeconds(5));
                    var answers = banner?.StartsWith("SSH-2.0-", StringComparison.Ordinal) == true;
                    if (answers != lastPathOk)
                    {
                        log.Write(answers ? "INFO" : "WARN", answers
                            ? $"{c.Key} 경유 SSH 응답 정상"
                            : $"{c.Key} 경유 SSH 응답 없음(수신={(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == c.Policy.ListenPort) ? "있음" : "없음")})");
                        lastPathOk = answers;
                    }
                    // 시작 후 SSH 경로가 처음 살아났을 때 한 번, 추가 서비스가 WSL 안에서 응답하는지 기록만 한다(통로는 그대로 유지, ADR 0011).
                    if (answers && !servicesChecked && c.Coordinator.RecordedConnectAddress is { } rec)
                    {
                        foreach (var s in c.Policy.Services)
                        {
                            var up = await ServicePortsCommand.ProbeAsync(System.Net.IPAddress.Parse(rec), s.ConnectPort, TimeSpan.FromSeconds(2));
                            log.Write("INFO", $"추가 서비스 {s.Name}({c.Policy.ListenAddress}:{s.ListenPort} → WSL {s.ConnectPort}): WSL 서비스 {(up ? "응답 있음" : "꺼짐(통로는 유지)")}");
                        }
                        servicesChecked = true;
                    }
                    if (!answers) delay = TimeSpan.FromSeconds(15); // 응답이 없을 때는 더 자주 확인
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    log.Write("WARN", $"감시 작업 실패: {ex.Message}");
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
