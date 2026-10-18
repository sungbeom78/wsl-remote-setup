using System.Net;
using Microsoft.Extensions.Time.Testing;
using WslRemoteSetup.Core.Agent;
using WslRemoteSetup.Core.Ipc;

namespace WslRemoteSetup.UnitTests;

public class AgentLoopTests
{
    private sealed class FakeKeeper : IKeeper
    {
        public bool IsHolding { get; set; }
        public int Starts { get; private set; }
        public int Releases { get; private set; }
        public void Start() { Starts++; IsHolding = true; }
        public void Release() { Releases++; IsHolding = false; }
    }

    private sealed class FakeReader : IWslAddressReader
    {
        public IPAddress? Address { get; set; } = IPAddress.Parse("172.24.220.111");
        public int Reads { get; private set; }
        public Task<IPAddress?> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(Address); }
    }

    private sealed class FakeClient : IRepairClient
    {
        public Queue<RepairCode> Responses { get; } = new();
        public List<IPAddress> Sent { get; } = [];
        public Task<RepairResponse> SendAsync(IPAddress observed, CancellationToken ct)
        {
            Sent.Add(observed);
            return Task.FromResult(new RepairResponse(Responses.Count > 0 ? Responses.Dequeue() : RepairCode.NoChange, ""));
        }
    }

    private sealed class FakePause : IPauseSource
    {
        public bool Paused { get; set; }
        public bool IsPaused() => Paused;
    }

    private sealed class ListLog : IAgentLog
    {
        public List<string> Warnings { get; } = [];
        public List<string> Infos { get; } = [];
        public void Info(string message) => Infos.Add(message);
        public void Warn(string message) => Warnings.Add(message);
    }

    private sealed record Rig(AgentLoop Loop, FakeKeeper Keeper, FakeReader Reader, FakeClient Client, FakePause Pause, ListLog Log, FakeTimeProvider Time);

    private static Rig Create()
    {
        var k = new FakeKeeper(); var r = new FakeReader(); var c = new FakeClient(); var p = new FakePause(); var l = new ListLog();
        var t = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        return new Rig(new AgentLoop(k, r, c, p, l, t), k, r, c, p, l, t);
    }

    [Fact]
    public async Task First_tick_starts_keeper_and_confirms_forwarding()
    {
        var x = Create();
        x.Client.Responses.Enqueue(RepairCode.Updated);
        var delay = await x.Loop.TickAsync(CancellationToken.None);
        Assert.Equal(1, x.Keeper.Starts);
        Assert.Single(x.Client.Sent);
        Assert.Equal(TimeSpan.FromSeconds(60), delay);
        Assert.Equal("172.24.220.111", x.Loop.LastConfirmed!.ToString());
    }

    [Fact]
    public async Task Unchanged_address_is_not_resent_until_revalidate() // 매번 재작성하지 않음
    {
        var x = Create();
        await x.Loop.TickAsync(CancellationToken.None);
        x.Time.Advance(TimeSpan.FromMinutes(1));
        await x.Loop.TickAsync(CancellationToken.None);
        Assert.Single(x.Client.Sent);
        x.Time.Advance(TimeSpan.FromMinutes(10));
        await x.Loop.TickAsync(CancellationToken.None);
        Assert.Equal(2, x.Client.Sent.Count);
    }

    [Fact]
    public async Task Address_change_is_sent_immediately() // T11
    {
        var x = Create();
        await x.Loop.TickAsync(CancellationToken.None);
        x.Reader.Address = IPAddress.Parse("172.24.210.5");
        x.Client.Responses.Enqueue(RepairCode.Updated);
        await x.Loop.TickAsync(CancellationToken.None);
        Assert.Equal("172.24.210.5", x.Client.Sent[^1].ToString());
    }

    [Fact]
    public async Task Pause_releases_keeper_and_never_restarts_wsl() // T16
    {
        var x = Create();
        await x.Loop.TickAsync(CancellationToken.None);
        x.Pause.Paused = true;
        for (var i = 0; i < 5; i++) { x.Time.Advance(TimeSpan.FromMinutes(1)); await x.Loop.TickAsync(CancellationToken.None); }
        Assert.Equal(1, x.Keeper.Starts);
        Assert.Equal(1, x.Keeper.Releases);
        Assert.False(x.Keeper.IsHolding);
        Assert.Equal(1, x.Reader.Reads); // pause 중에는 WSL을 조회(기동)하지 않는다
    }

    [Fact]
    public async Task Resume_restarts_keeper_and_reconfirms()
    {
        var x = Create();
        await x.Loop.TickAsync(CancellationToken.None);
        x.Pause.Paused = true;
        await x.Loop.TickAsync(CancellationToken.None);
        x.Pause.Paused = false;
        await x.Loop.TickAsync(CancellationToken.None);
        Assert.Equal(2, x.Keeper.Starts);
        Assert.Equal(2, x.Client.Sent.Count);
    }

    [Fact]
    public async Task Failures_back_off_exponentially_with_cap_and_warn_once() // T13
    {
        var x = Create();
        x.Reader.Address = null;
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 8; i++) delays.Add(await x.Loop.TickAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(120), delays[2]);
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(10), delays[^1]);
        Assert.Single(x.Log.Warnings);

        x.Reader.Address = IPAddress.Parse("172.24.220.111");
        Assert.Equal(TimeSpan.FromSeconds(60), await x.Loop.TickAsync(CancellationToken.None));
        Assert.Equal(0, x.Loop.ConsecutiveFailures);
    }

    [Fact]
    public async Task Denied_or_conflict_is_failure_not_success()
    {
        var x = Create();
        x.Client.Responses.Enqueue(RepairCode.Conflict);
        var d = await x.Loop.TickAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(30), d);
        Assert.Null(x.Loop.LastConfirmed);
    }

    [Fact]
    public async Task Rate_limited_is_retried_without_backoff()
    {
        var x = Create();
        x.Client.Responses.Enqueue(RepairCode.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(60), await x.Loop.TickAsync(CancellationToken.None));
        Assert.Equal(0, x.Loop.ConsecutiveFailures);
        await x.Loop.TickAsync(CancellationToken.None);
        Assert.Equal(2, x.Client.Sent.Count);
    }

    [Fact]
    public async Task Keeper_crash_loop_is_throttled()
    {
        var x = Create();
        for (var i = 0; i < 6; i++)
        {
            await x.Loop.TickAsync(CancellationToken.None);
            x.Keeper.IsHolding = false; // 매번 즉시 종료되는 keeper
            x.Time.Advance(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(3, x.Keeper.Starts);
    }
}
