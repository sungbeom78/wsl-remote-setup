using WslRemoteSetup.Core.State;

namespace WslRemoteSetup.UnitTests;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wrs-tests-" + Guid.NewGuid().ToString("N"), "상태 폴더");
    private string StatePath => Path.Combine(_dir, "install-state.json");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Missing_file_is_not_found()
    {
        Assert.Equal(LoadOutcome.NotFound, new StateStore(StatePath).Load().Outcome);
        Assert.False(Directory.Exists(_dir)); // 조회만으로 폴더를 만들지 않는다
    }

    [Fact]
    public void Round_trip_preserves_fields_in_korean_path()
    {
        var store = new StateStore(StatePath);
        var state = new InstallState
        {
            AppVersion = "0.1.0",
            Phase = InstallPhase.AwaitingOwnerLogin,
            OwnerSid = Fixtures.OwnerSid,
            DistroName = "Ubuntu-24.04",
            StepStates = new Dictionary<string, StepState> { ["wsl-runtime"] = StepState.Verified },
            ResourceOwnership = [new OwnedResource("portproxy", "192.168.1.222:1004", "SSH 포워딩", DateTimeOffset.UnixEpoch)],
            PendingAction = "소유자 로그인 대기",
        };
        var saved = store.Save(state);
        var loaded = store.Load();
        Assert.Equal(LoadOutcome.Loaded, loaded.Outcome);
        Assert.Equal(saved.InstallationId, loaded.State!.InstallationId);
        Assert.Equal(InstallPhase.AwaitingOwnerLogin, loaded.State.Phase);
        Assert.Equal(StepState.Verified, loaded.State.StepStates["wsl-runtime"]);
        Assert.Equal("192.168.1.222:1004", Assert.Single(loaded.State.ResourceOwnership).Key);
    }

    [Fact]
    public void Second_save_keeps_backup_and_leaves_no_temp_files()
    {
        var store = new StateStore(StatePath);
        store.Save(new InstallState { Phase = InstallPhase.Planned });
        store.Save(new InstallState { Phase = InstallPhase.Applying });
        Assert.True(File.Exists(StatePath + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.False(File.Exists(StatePath + ".lock"));
        Assert.Equal(InstallPhase.Applying, store.Load().State!.Phase);
    }

    [Fact]
    public void Corrupt_file_is_reported_and_not_reset()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ not json");
        var r = new StateStore(StatePath).Load();
        Assert.Equal(LoadOutcome.Corrupt, r.Outcome);
        Assert.Equal("{ not json", File.ReadAllText(StatePath)); // T18: 덮어쓰지 않음
    }

    [Fact]
    public void Unsupported_version_is_reported()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "SchemaVersion": 99 }""");
        Assert.Equal(LoadOutcome.UnsupportedVersion, new StateStore(StatePath).Load().Outcome);
    }

    [Fact]
    public void Concurrent_writer_times_out_with_lock_error()
    {
        Directory.CreateDirectory(_dir);
        using var held = new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new StateStore(StatePath);
        Assert.Throws<StateLockedException>(() => store.Save(new InstallState(), TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void Serialized_state_contains_no_password_field()
    {
        var store = new StateStore(StatePath);
        store.Save(new InstallState());
        var json = File.ReadAllText(StatePath);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }
}
