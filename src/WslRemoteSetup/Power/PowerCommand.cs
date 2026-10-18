using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using WslRemoteSetup.Agent;
using WslRemoteSetup.Core.Execution;
using WslRemoteSetup.Core.Power;
using WslRemoteSetup.Core.State;
using WslRemoteSetup.Reporting;
using WslRemoteSetup.Service;

namespace WslRemoteSetup.Power;

/// <summary>
/// 절전 점검·"전원 연결 시 절전 안 함"(ADR 0009). 바꾸는 것은 현재 전원 계획의 전원 연결(AC) 절전·최대 절전 시간뿐이다.
/// 배터리(DC) 설정, 화면 꺼짐, 덮개 닫기 동작은 건드리지 않는다. 적용 전 값을 저장해 'power restore'로 되돌린다.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PowerCommand
{
    private static readonly string PowerCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");

    private static string BackupFile => Path.Combine(UserPaths.Root, "user-state", "power-before.json");

    /// <summary>읽기 전용 조회(현지화 출력 대신 전원 API 사용).</summary>
    public static SleepInfo Read()
    {
        uint? standby = null, hibernate = null;
        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) == 0)
        {
            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
                var sub = SubSleep;
                var st = StandbyIdle;
                var hb = HibernateIdle;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref st, out var v1) == 0) standby = v1;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref hb, out var v2) == 0) hibernate = v2;
            }
            finally { LocalFree(schemePtr); }
        }
        var hasBattery = GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255;
        return new SleepInfo(standby, hibernate, hasBattery);
    }

    public static async Task<int> RunAsync(string sub) => sub switch
    {
        "" or "status" => Show(),
        "keep-awake" => await KeepAwakeAsync(),
        "restore" => await RestoreAsync(),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("사용: power [status|keep-awake|restore]");
        return App.ExitUsage;
    }

    private static int Show()
    {
        var s = Read();
        var line = SleepAdvisor.Line(s);
        Ui.Title("절전 설정");
        Ui.Item(1, "전원 연결 시 절전", line.Health switch { Core.Status.Health.Good => "정상", Core.Status.Health.Warn => "주의", _ => "확인 필요" }, line.Text);
        if (line.Hint is not null) Ui.Note(line.Hint);
        Ui.Note("Wake-on-LAN(원격으로 깨우기)은 보통 유선 랜에서만 됩니다. 무선 연결 PC는 잠들지 않게 두는 방법을 권합니다.");
        return App.ExitOk;
    }

    private static async Task<int> KeepAwakeAsync()
    {
        var s = Read();
        Ui.Title("전원 연결 시 절전 안 함");
        if (s.AcStandbySeconds is null)
        {
            Ui.Blocked("현재 절전 설정을 읽지 못해 바꾸지 않습니다.");
            return App.ExitChecksFailed;
        }
        if (s.AcNeverSleeps)
        {
            Ui.Item(1, "전원 연결 시 절전", "변경 없음", "이미 잠들지 않게 되어 있습니다.");
            return App.ExitOk;
        }

        Ui.Line($"지금: 전원 연결 시 절전 {Text(s.AcStandbySeconds)}, 최대 절전 {Text(s.AcHibernateSeconds)}");
        Ui.Line("바꾼 후: 전원 연결 시 절전·최대 절전 모두 '사용 안 함'");
        Ui.Note("배터리 사용 중 설정, 화면 꺼짐 시간, 덮개 닫기 동작은 바꾸지 않습니다. 전기 사용량이 조금 늘 수 있습니다.");
        Ui.Note("되돌리기: 'power restore'(지금 값으로 복원).");
        if (!ConfirmPrompt.Ask(out var code)) return code;

        var history = Ui.OpenHistory("절전 안 함 적용");
        history.Begin("이전 값 저장");
        history.Detail("이전 AC 값(초)", $"standby={s.AcStandbySeconds} hibernate={s.AcHibernateSeconds}");
        if (!File.Exists(BackupFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
            var backup = new SleepBackup(s.AcStandbySeconds.Value, s.AcHibernateSeconds ?? 0, DateTimeOffset.Now);
            await File.WriteAllTextAsync(BackupFile, JsonSerializer.Serialize(backup, StateStore.JsonOptions));
            history.Done(BackupFile);
        }
        else history.Skip("이전 백업 유지(처음 값 보존)");

        var ok = await ApplyAsync(0, 0, history);
        return Finish(ok, history, "전원에 연결되어 있으면 이제 잠들지 않습니다.");
    }

    public static bool HasBackup => File.Exists(BackupFile);

    public static async Task<int> RestoreAsync(bool confirm = true)
    {
        Ui.Title("절전 설정 되돌리기");
        SleepBackup? b = null;
        try { if (File.Exists(BackupFile)) b = JsonSerializer.Deserialize<SleepBackup>(await File.ReadAllTextAsync(BackupFile), StateStore.JsonOptions); }
        catch (JsonException) { }
        if (b is null)
        {
            Ui.Item(1, "절전 설정", "변경 없음", "이 도구로 바꾼 기록이 없습니다.");
            return App.ExitOk;
        }
        Ui.Line($"되돌릴 값: 전원 연결 시 절전 {Text(b.AcStandbySeconds)}, 최대 절전 {Text(b.AcHibernateSeconds)}");
        if (confirm && !ConfirmPrompt.Ask(out var code)) return code;
        var history = Ui.OpenHistory("절전 설정 되돌리기");
        var ok = await ApplyAsync(b.AcStandbySeconds, b.AcHibernateSeconds, history);
        if (ok) File.Delete(BackupFile);
        return Finish(ok, history, "절전 설정을 이전 값으로 되돌렸습니다.");
    }

    /// <summary>현재 전원 계획에 AC 값만 쓰고 계획을 다시 활성화한다.</summary>
    private static async Task<bool> ApplyAsync(uint standby, uint hibernate, Core.Reporting.OperationHistory h)
    {
        h.Begin("전원 계획 변경");
        var runner = new CommandRunner([PowerCfg]);
        foreach (var (name, value) in new[] { ("STANDBYIDLE", standby), ("HIBERNATEIDLE", hibernate) })
        {
            var r = await runner.RunAsync(new CommandSpec(PowerCfg,
                ["/setacvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", name, value.ToString(CultureInfo.InvariantCulture)]));
            h.Detail($"powercfg {name}={value}", $"exit={r.ExitCode} {r.StandardError.Trim()}");
            if (!r.ExitedZero) { h.Fail(name); return false; }
        }
        var a = await runner.RunAsync(new CommandSpec(PowerCfg, ["/setactive", "SCHEME_CURRENT"]));
        h.Detail("powercfg setactive", $"exit={a.ExitCode}");
        var now = Read();
        h.Detail("적용 후 AC 값(초)", $"standby={now.AcStandbySeconds} hibernate={now.AcHibernateSeconds}");
        var ok = a.ExitedZero && now.AcStandbySeconds == standby && (now.AcHibernateSeconds ?? 0) == hibernate;
        if (ok) h.Done("확인됨"); else h.Fail("적용 후 값이 다름");
        return ok;
    }

    private static int Finish(bool ok, Core.Reporting.OperationHistory h, string message)
    {
        h.Finish(ok ? "완료" : "실패");
        Console.WriteLine();
        if (ok) Ui.Line(message); else Ui.Blocked("설정을 바꾸지 못했습니다. 관리자 창에서 다시 시도해 보세요.");
        Ui.HistoryHint(h);
        return ok ? App.ExitOk : App.ExitChecksFailed;
    }

    private static string Text(uint? seconds) => seconds switch
    {
        null => "알 수 없음",
        0 => "사용 안 함",
        var v => SleepAdvisor.Duration(v.Value) + " 후",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
