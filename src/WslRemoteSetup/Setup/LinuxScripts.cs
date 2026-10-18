using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using WslRemoteSetup.Agent;
using WslRemoteSetup.Core.Validation;

namespace WslRemoteSetup.Setup;

/// <summary>
/// EXE에 포함된 Linux 스크립트를 소유자 사용자 폴더에 LF로 풀어 놓고, 소유자 세션에서 `wsl -u root`로 실행한다.
/// 소유자는 자기 배포판의 root이므로 sudo 비밀번호를 묻지 않는다(같은 신뢰 경계). 인수는 배열로만 넘긴다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LinuxScripts
{
    private static readonly string Wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static string Extract(string name)
    {
        using var s = typeof(LinuxScripts).Assembly.GetManifestResourceStream($"linux/{name}")
            ?? throw new InvalidOperationException($"내장 스크립트 없음: {name}");
        using var reader = new StreamReader(s, Encoding.UTF8);
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var dir = Path.Combine(UserPaths.Root, "scripts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>C:\Users\x\… → /mnt/c/Users/x/…</summary>
    public static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length < 3 || full[1] != ':') throw new ArgumentException("드라이브 경로가 아닙니다.", nameof(windowsPath));
        return $"/mnt/{char.ToLowerInvariant(full[0])}/{full[3..].Replace('\\', '/')}";
    }

    /// <summary>출력을 화면에 그대로 보여 주며 실행(사용자가 진행 상황을 봄). 종료코드를 돌려준다.</summary>
    public static int RunVisible(string distro, string user, string script, params string[] args) =>
        RunVisibleCore(distro, user, ["--exec", "/bin/bash", ToWslPath(Extract(script)), .. args]);

    /// <summary>출력을 받아 오는 실행(결과 줄 해석용).</summary>
    public static (int Exit, string Output) RunCaptured(string distro, string user, string script, params string[] args)
    {
        var psi = Start(distro, user, ["--exec", "/bin/bash", ToWslPath(Extract(script)), .. args]);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(10))) { p.Kill(true); return (-1, "시간 초과"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>사용자가 직접 입력해야 하는 명령(passwd 등). 콘솔을 그대로 넘긴다.</summary>
    public static int RunInteractive(string distro, string user, params string[] command) =>
        RunVisibleCore(distro, user, ["--exec", .. command]);

    /// <summary>한 줄을 표준입력으로 넘겨 스크립트 실행(공개키 등록). 인수나 셸 문자열로 넘기지 않는다.</summary>
    public static (int Exit, string Output) RunWithInput(string distro, string user, string script, string inputLine, params string[] args)
    {
        var psi = Start(distro, user, ["--exec", "/bin/bash", ToWslPath(Extract(script)), .. args]);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(inputLine.Replace("\r", "", StringComparison.Ordinal) + "\n");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(2))) { p.Kill(true); return (-1, "시간 초과"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static int RunVisibleCore(string distro, string user, string[] args)
    {
        using var p = Process.Start(Start(distro, user, args))!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static ProcessStartInfo Start(string distro, string user, IEnumerable<string> args)
    {
        if (!InputValidator.DistroName(distro).IsValid) throw new ArgumentException("배포판 이름 오류", nameof(distro));
        if (user != "root" && !InputValidator.LinuxUserName(user).IsValid) throw new ArgumentException("사용자 이름 오류", nameof(user));
        var psi = new ProcessStartInfo(Wsl) { UseShellExecute = false };
        foreach (var a in new[] { "--distribution", distro, "--user", user }.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }
}
