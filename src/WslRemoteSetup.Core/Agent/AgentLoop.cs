using System.Net;
using WslRemoteSetup.Core.Ipc;

namespace WslRemoteSetup.Core.Agent;

/// <summary>대상 배포판 인스턴스를 유지하는 keeper(소유자 세션의 foreground wsl.exe).</summary>
public interface IKeeper
{
    bool IsHolding { get; }
    void Start();
    void Release();
}

/// <summary>소유자 세션에서 대상 배포판의 포워딩 대상 IPv4를 읽는다. 실패 시 null.</summary>
public interface IWslAddressReader
{
    Task<IPAddress?> ReadAsync(CancellationToken ct);
}

/// <summary>네트워크 관리부에 갱신을 요청한다. 연결 실패 등은 예외 대신 Error 응답으로 돌려준다.</summary>
public interface IRepairClient
{
    Task<RepairResponse> SendAsync(IPAddress observed, CancellationToken ct);
}

/// <summary>사용자 pause 의도. 소유자 사용자 상태에 저장한다(비권한).</summary>
public interface IPauseSource
{
    bool IsPaused();
}

public interface IAgentLog
{
    void Info(string message);
    void Warn(string message);
}

/// <summary>실패 시 지수 backoff, 성공 시 초기화. 상한을 둔다.</summary>
public sealed class Backoff(TimeSpan initial, TimeSpan max)
{
    public int ConsecutiveFailures { get; private set; }

    public TimeSpan NextDelay()
    {
        ConsecutiveFailures++;
        var ticks = initial.Ticks * Math.Pow(2, Math.Min(ConsecutiveFailures - 1, 16));
        return TimeSpan.FromTicks((long)Math.Min(ticks, max.Ticks));
    }

    public void Reset() => ConsecutiveFailures = 0;
}

public sealed record AgentOptions
{
    /// <summary>정상 상태 점검 주기(FR-07 기본 60초).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>주소가 같아도 관리부에 재확인을 보내는 주기(외부 변경 탐지).</summary>
    public TimeSpan Revalidate { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan BackoffInitial { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>이 횟수만큼 연속 실패하면 경고를 남긴다(무한 반복 수리 대신 알림).</summary>
    public int WarnAfterFailures { get; init; } = 5;
    /// <summary>keeper가 이 시간 안에 이 횟수 이상 다시 시작되면 재시작을 멈추고 backoff한다.</summary>
    public int KeeperRestartLimit { get; init; } = 3;
    public TimeSpan KeeperRestartWindow { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// 소유자 에이전트의 한 주기(FR-07):
/// pause면 keeper를 해제하고 아무것도 다시 시작하지 않는다 → keeper 유지 → WSL 주소 관찰 →
/// 바뀌었거나 재확인 주기면 관리부에 요청 → 결과에 따라 다음 지연(정상 주기 또는 backoff)을 돌려준다.
/// </summary>
public sealed class AgentLoop(
    IKeeper keeper,
    IWslAddressReader reader,
    IRepairClient client,
    IPauseSource pause,
    IAgentLog log,
    TimeProvider time,
    AgentOptions? options = null)
{
    private readonly AgentOptions _o = options ?? new AgentOptions();
    private readonly Backoff _backoff = new((options ?? new AgentOptions()).BackoffInitial, (options ?? new AgentOptions()).BackoffMax);
    private readonly Queue<DateTimeOffset> _keeperStarts = new();
    private bool _pausedLogged;
    private bool _warned;

    public IPAddress? LastConfirmed { get; private set; }
    public DateTimeOffset? LastConfirmedAt { get; private set; }
    public RepairCode? LastCode { get; private set; }
    public int ConsecutiveFailures => _backoff.ConsecutiveFailures;

    public async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        if (pause.IsPaused())
        {
            if (keeper.IsHolding) keeper.Release();
            if (!_pausedLogged) log.Info("일시중지 상태: keeper를 해제했고 WSL을 다시 시작하지 않습니다.");
            _pausedLogged = true;
            LastConfirmed = null; // 재개 시 즉시 재확인
            return _o.Interval;
        }
        if (_pausedLogged) log.Info("모니터링을 재개합니다.");
        _pausedLogged = false;

        if (!keeper.IsHolding)
        {
            while (_keeperStarts.Count > 0 && now - _keeperStarts.Peek() > _o.KeeperRestartWindow) _keeperStarts.Dequeue();
            if (_keeperStarts.Count >= _o.KeeperRestartLimit)
                return Fail($"keeper가 {_o.KeeperRestartWindow.TotalMinutes:F0}분 안에 {_keeperStarts.Count}회 종료되었습니다. 재시작을 늦춥니다.");
            keeper.Start();
            _keeperStarts.Enqueue(now);
            log.Info("keeper 시작");
        }

        var ip = await reader.ReadAsync(ct).ConfigureAwait(false);
        if (ip is null) return Fail("WSL 주소를 읽지 못했습니다.");

        var due = LastConfirmed is null || !ip.Equals(LastConfirmed)
            || LastConfirmedAt is null || now - LastConfirmedAt >= _o.Revalidate;
        if (!due) return Succeeded();

        var response = await client.SendAsync(ip, ct).ConfigureAwait(false);
        LastCode = response.Code;
        switch (response.Code)
        {
            case RepairCode.Updated or RepairCode.NoChange:
                if (response.Code == RepairCode.Updated || !ip.Equals(LastConfirmed))
                    log.Info($"포워딩 확인: {ip} ({response.Code})");
                LastConfirmed = ip;
                LastConfirmedAt = now;
                return Succeeded();
            case RepairCode.RateLimited:
                return _o.Interval; // 실패가 아니다. 다음 주기에 다시 보낸다.
            default:
                return Fail($"관리부 응답 {response.Code}: {response.Message}");
        }
    }

    private TimeSpan Succeeded()
    {
        if (_warned) log.Info("자동 복구가 정상으로 돌아왔습니다.");
        _warned = false;
        _backoff.Reset();
        return _o.Interval;
    }

    private TimeSpan Fail(string reason)
    {
        var delay = _backoff.NextDelay();
        log.Info($"{reason} {delay.TotalSeconds:F0}초 뒤 재시도(연속 실패 {_backoff.ConsecutiveFailures}).");
        if (_backoff.ConsecutiveFailures >= _o.WarnAfterFailures && !_warned)
        {
            log.Warn($"자동 복구가 {_backoff.ConsecutiveFailures}회 연속 실패했습니다. 'status'로 원인을 확인하세요.");
            _warned = true;
        }
        return delay;
    }
}
