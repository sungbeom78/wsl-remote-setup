using System.Diagnostics;
using System.Runtime.Versioning;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Validation;
using WslRemoteSetup.Core.Wsl;

namespace WslRemoteSetup.Lifetime;

/// <summary>
/// 소유자 세션 안에서 대상 배포판 인스턴스를 유지한다(ADR 0003, FR-07).
/// 배포판 안에서 셸 없이 `sleep infinity`를 foreground로 실행하는 wsl.exe 클라이언트를 하나 보유하는 방식이다.
/// systemd 서비스만으로는 인스턴스 수명이 보장되지 않는다는 가정을 실기로 확인하기 위한 구성요소다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslLifetimeKeeper : IDisposable
{
    private static readonly string WslExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    private readonly string _distro;
    private Process? _process;

    public WslLifetimeKeeper(string distro)
    {
        var v = InputValidator.DistroName(distro);
        if (!v.IsValid) throw new ArgumentException(v.Error, nameof(distro));
        _distro = distro;
    }

    public bool IsHolding => _process is { HasExited: false };
    public int? ProcessId => IsHolding ? _process!.Id : null;
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    public void Start()
    {
        if (IsHolding) return; // 한 인스턴스만
        var psi = new ProcessStartInfo(WslExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "--distribution", _distro, "--exec", "/bin/sleep", "infinity" })
            psi.ArgumentList.Add(a);
        _process = Process.Start(psi) ?? throw new InvalidOperationException("wsl.exe를 시작하지 못했습니다.");
        ChildProcessJob.Assign(_process); // 에이전트가 강제 종료돼도 keeper가 남지 않게 한다.
        // 출력은 읽어 버려서 파이프가 차지 않게 한다. stdin은 열어 둔다(닫으면 즉시 끝나는 클라이언트가 있을 수 있음).
        _ = _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }

    /// <summary>이 keeper가 띄운 wsl.exe 클라이언트만 끝낸다. 배포판 terminate/shutdown은 하지 않는다.</summary>
    public void Release()
    {
        if (_process is null) return;
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.WaitForExit(10_000);
        }
    }

    public void Dispose()
    {
        Release();
        _process?.Dispose();
    }

    /// <summary>현재 실행 중인 배포판 목록. 조회 실패 시 null(“중지됨”과 구분).</summary>
    public static async Task<IReadOnlyList<string>?> RunningDistrosAsync(ICommandRunner runner, CancellationToken ct = default)
    {
        var r = await runner.RunAsync(new CommandSpec(WslExe, ["--list", "--running", "--quiet"]) { Timeout = TimeSpan.FromSeconds(20) }, ct)
            .ConfigureAwait(false);
        return r.ExitedZero ? WslOutputParser.ParseQuietList(r.StandardOutput) : null;
    }
}
