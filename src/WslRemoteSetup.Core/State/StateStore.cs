using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslRemoteSetup.Core.State;

public enum LoadOutcome { NotFound, Loaded, Corrupt, UnsupportedVersion }

public sealed record LoadResult(LoadOutcome Outcome, InstallState? State, string? Error);

/// <summary>
/// 상태 파일 저장소. 잠금 파일로 동시 기록을 막고, 임시 파일 기록 후 원자적 교체를 한다.
/// 손상·미지원 버전은 복구 진입 사유이며 초기화하지 않는다.
/// </summary>
public sealed class StateStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly TimeProvider _time;

    public StateStore(string path, TimeProvider? time = null)
    {
        _path = Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
    }

    public string FilePath => _path;
    private string LockPath => _path + ".lock";
    private string BackupPath => _path + ".bak";

    public LoadResult Load()
    {
        if (!File.Exists(_path)) return new(LoadOutcome.NotFound, null, null);
        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (IOException ex)
        {
            return new(LoadOutcome.Corrupt, null, ex.Message);
        }

        int version;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(nameof(InstallState.SchemaVersion), out var v) || !v.TryGetInt32(out version))
                return new(LoadOutcome.Corrupt, null, "SchemaVersion이 없습니다.");
        }
        catch (JsonException ex)
        {
            return new(LoadOutcome.Corrupt, null, ex.Message);
        }

        if (version != InstallState.CurrentSchemaVersion)
            return new(LoadOutcome.UnsupportedVersion, null, $"지원하지 않는 상태 파일 버전 {version}");

        try
        {
            var state = JsonSerializer.Deserialize<InstallState>(json, JsonOptions);
            return state is null
                ? new(LoadOutcome.Corrupt, null, "빈 상태 파일")
                : new(LoadOutcome.Loaded, state, null);
        }
        catch (JsonException ex)
        {
            return new(LoadOutcome.Corrupt, null, ex.Message);
        }
    }

    /// <summary>상태를 원자적으로 저장하고 저장된 값(UpdatedAt 갱신)을 돌려준다.</summary>
    public InstallState Save(InstallState state, TimeSpan? lockTimeout = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var saved = state with { SchemaVersion = InstallState.CurrentSchemaVersion, UpdatedAt = _time.GetUtcNow() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, JsonOptions);

        using var lockHandle = AcquireLock(lockTimeout ?? TimeSpan.FromSeconds(10));
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temp, _path, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(temp, _path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return saved;
    }

    private FileStream AcquireLock(TimeSpan timeout)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (_time.GetUtcNow() < deadline)
            {
                Thread.Sleep(100);
            }
            catch (IOException ex)
            {
                throw new StateLockedException($"상태 파일이 다른 프로세스에 의해 잠겨 있습니다: {LockPath}", ex);
            }
        }
    }
}

public sealed class StateLockedException(string message, Exception inner) : IOException(message, inner);
