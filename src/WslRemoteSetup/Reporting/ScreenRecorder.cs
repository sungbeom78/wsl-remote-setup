using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WslRemoteSetup.Reporting;

/// <summary>
/// 화면 기록(매뉴얼·발표 자료용). `--record &lt;폴더&gt;` 또는 환경 변수 WSLRS_RECORD로 켠다.
/// 이 프로그램이 화면에 쓴 글자(색 포함)와 사용자가 입력한 줄을 JSON Lines로 그대로 남긴다.
/// 관리자 창도 같은 폴더에 따로 기록한다. 다른 프로그램(wsl 설치 진행, passwd 등)이 직접 쓴 화면은 기록하지 않고 표시만 남긴다.
/// 비밀번호는 이 프로그램이 읽지 않으므로 기록에 들어가지 않는다.
/// </summary>
internal static class ScreenRecorder
{
    private static StreamWriter? _file;
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string? Folder { get; private set; }
    public static bool Active => _file is not null;

    /// <summary>관리자 창에 넘길 인수(기록 중일 때만).</summary>
    public static string[] ChildArgs => Folder is null ? [] : ["--record", Folder];

    /// <summary>`--record 폴더`를 인수에서 빼고, 켜져 있으면 화면 출력·입력을 기록하기 시작한다.</summary>
    public static string[] Start(string[] args)
    {
        var i = Array.IndexOf(args, "--record");
        string? folder = null;
        if (i >= 0 && i + 1 < args.Length)
        {
            folder = args[i + 1];
            args = [.. args[..i], .. args[(i + 2)..]];
        }
        folder ??= Environment.GetEnvironmentVariable("WSLRS_RECORD");
        if (string.IsNullOrWhiteSpace(folder)) return args;

        Directory.CreateDirectory(folder);
        Folder = Path.GetFullPath(folder);
        var command = args.Length == 0 ? "menu" : args[0];
        var name = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Safe(command)}.jsonl");
        _file = new StreamWriter(Path.Combine(Folder, name), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        Event("start", string.Join(' ', args), null, new { elevated = Forwarding.WindowsForwarding.IsElevated() });
        Console.SetOut(new TeeWriter(Console.Out, "out"));
        Console.SetError(new TeeWriter(Console.Error, "err"));
        Console.SetIn(new TeeReader(Console.In));
        return args;
    }

    /// <summary>이 프로그램이 직접 그리지 않는 화면(외부 프로그램 창)을 표시만 남긴다.</summary>
    public static void Note(string text) => Event("note", text, null);

    public static void Stop(int exitCode)
    {
        Event("end", exitCode.ToString(CultureInfo.InvariantCulture), null);
        lock (Gate) { _file?.Dispose(); _file = null; }
    }

    internal static void Event(string kind, string text, ConsoleColor? color, object? extra = null)
    {
        if (_file is null) return;
        var line = JsonSerializer.Serialize(new
        {
            t = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            k = kind,
            c = color?.ToString(),
            x = text,
            extra,
        }, Json);
        lock (Gate) _file?.WriteLine(line);
    }

    private static string Safe(string s) => new([.. s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(30)]);

    private static ConsoleColor? CurrentColor()
    {
        try { return Console.ForegroundColor; } catch (IOException) { return null; }
    }

    /// <summary>화면에 쓰면서 같은 글자와 그때의 글자색을 기록한다.</summary>
    private sealed class TeeWriter(TextWriter inner, string kind) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) { inner.Write(value); Event(kind, value.ToString(), CurrentColor()); }
        public override void Write(string? value) { inner.Write(value); if (!string.IsNullOrEmpty(value)) Event(kind, value, CurrentColor()); }
        public override void WriteLine(string? value) { inner.WriteLine(value); Event(kind, (value ?? "") + "\n", CurrentColor()); }
        public override void WriteLine() { inner.WriteLine(); Event(kind, "\n", CurrentColor()); }
        public override void Flush() => inner.Flush();
    }

    /// <summary>사용자가 입력한 줄을 기록한다(화면에는 콘솔이 직접 보여 준다).</summary>
    private sealed class TeeReader(TextReader inner) : TextReader
    {
        public override string? ReadLine()
        {
            var line = inner.ReadLine();
            Event("in", line ?? "", null);
            return line;
        }
        public override int Read() => inner.Read();
        public override int Peek() => inner.Peek();
    }
}
