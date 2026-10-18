using System.Diagnostics;

namespace WslRemoteSetup.Core.Execution;

/// <summary>실행할 외부 명령. 인수는 배열로만 받아 셸 문자열 결합을 하지 않는다.</summary>
public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>출력·오류에서 가릴 값. 비밀값은 인수로 전달하지 않는 것이 원칙이며 이 목록은 방어용이다.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

public sealed record CommandResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Canceled,
    TimeSpan Duration)
{
    /// <summary>프로세스가 0으로 끝났는지. 명령 성공이 곧 상태 성공은 아니다.</summary>
    public bool ExitedZero => ExitCode == 0 && !TimedOut && !Canceled;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default);
}

public sealed class CommandNotAllowedException(string message) : InvalidOperationException(message);

/// <summary>
/// 공통 명령 실행 계층: 실행 파일 allowlist(절대 경로), 인수 배열, timeout, 취소, 종료코드, 출력 디코딩, 비밀 마스킹.
/// stdin은 즉시 닫아 대화형 프롬프트에서 무한 대기하지 않게 한다.
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    private readonly HashSet<string> _allowed;

    public CommandRunner(IEnumerable<string> allowedExecutables)
    {
        _allowed = new HashSet<string>(allowedExecutables.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
    }

    public static CommandRunner ForWindowsSystem()
    {
        var sys = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        return new CommandRunner([
            Path.Combine(sys, "wsl.exe"),
            Path.Combine(sys, "netsh.exe"),
        ]);
    }

    public void EnsureAllowed(CommandSpec spec)
    {
        if (!Path.IsPathFullyQualified(spec.FileName))
            throw new CommandNotAllowedException($"실행 파일은 절대 경로여야 합니다: {spec.FileName}");
        if (!_allowed.Contains(Path.GetFullPath(spec.FileName)))
            throw new CommandNotAllowedException($"허용 목록에 없는 실행 파일입니다: {spec.FileName}");
        if (spec.Timeout <= TimeSpan.Zero)
            throw new ArgumentException("timeout은 0보다 커야 합니다.", nameof(spec));
        foreach (var arg in spec.Arguments)
            if (arg.Contains('\0'))
                throw new CommandNotAllowedException("인수에 NUL 문자가 있습니다.");
    }

    public async Task<CommandResult> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(spec);

        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();

        var stdoutTask = ReadAllAsync(process.StandardOutput.BaseStream);
        var stderrTask = ReadAllAsync(process.StandardError.BaseStream);

        using var timeoutCts = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        bool timedOut = false, canceled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            canceled = cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var stdout = OutputDecoder.Decode(await stdoutTask.ConfigureAwait(false));
        var stderr = OutputDecoder.Decode(await stderrTask.ConfigureAwait(false));
        return new CommandResult(
            timedOut || canceled ? null : process.ExitCode,
            SecretMasker.Apply(stdout, spec.Secrets),
            SecretMasker.Apply(stderr, spec.Secrets),
            timedOut,
            canceled,
            sw.Elapsed);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }
}
