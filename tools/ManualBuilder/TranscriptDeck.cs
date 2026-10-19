using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ManualBuilder;

/// <summary>
/// 화면 기록(`WslRemoteSetup --record`)의 JSON Lines를 검은 터미널 화면 슬라이드로 만든다.
/// 글자는 기록 그대로 쓴다(바꾸지 않음). 입력한 줄은 굵게, 다른 프로그램 화면 자리는 회색 기울임 표시.
/// 선택: --mask 파일(JSON {"실제":"예시"})로 공개용 치환본을 만든다.
/// </summary>
public static partial class TranscriptDeck
{
    private const double SlideW = 13.333;
    private const int MaxLines = 30;
    private const double FontPt = 11;

    private static readonly Dictionary<string, string> Palette = new()
    {
        ["Black"] = "0C0C0C", ["DarkBlue"] = "0037DA", ["DarkGreen"] = "13A10E", ["DarkCyan"] = "3A96DD",
        ["DarkRed"] = "C50F1F", ["DarkMagenta"] = "881798", ["DarkYellow"] = "C19C00", ["Gray"] = "CCCCCC",
        ["DarkGray"] = "767676", ["Blue"] = "3B78FF", ["Green"] = "16C60C", ["Cyan"] = "61D6D6",
        ["Red"] = "E74856", ["Magenta"] = "B4009E", ["Yellow"] = "F9F1A5", ["White"] = "F2F2F2",
    };

    private sealed record Ev(int Window, string Kind, string? Color, string Text);

    private sealed record Page(string Window, string? Step, List<List<Run>> Lines, string When);

    [GeneratedRegex(@"^\s*\[\d+/\d+\]\s")]
    private static partial Regex StepHeader();

    public static int Run(string folder, string output, string? previewDir, string? maskFile, string title)
    {
        var mask = maskFile is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(maskFile))!;
        string M(string s) => mask.Aggregate(s, (acc, kv) => acc.Replace(kv.Key, kv.Value, StringComparison.Ordinal));

        var files = Directory.GetFiles(folder, "*.jsonl").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("기록 파일이 없습니다."); return 2; }

        // 파일(창)마다 이벤트를 시간순으로 모은 뒤 전체를 시간순으로 합친다.
        var labels = new List<string>();
        var whens = new List<string>();
        var all = new List<(DateTime At, int Seq, Ev E)>();
        var seq = 0;
        foreach (var f in files)
        {
            var w = labels.Count;
            var name = Path.GetFileNameWithoutExtension(f);
            var day = DateTime.ParseExact(name[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
            whens.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var label = "WSL Remote Setup";
            foreach (var raw in File.ReadLines(f))
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                var kind = r.GetProperty("k").GetString()!;
                var text = r.GetProperty("x").GetString() ?? "";
                var color = r.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var at = day + TimeSpan.ParseExact(r.GetProperty("t").GetString()!, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                if (kind == "start")
                {
                    var elevated = r.TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Object && ex.GetProperty("elevated").GetBoolean();
                    label = Label(text, elevated);
                    whens[w] = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    continue;
                }
                if (kind == "end") continue;
                all.Add((at, seq++, new Ev(w, kind, color, M(text))));
            }
            labels.Add(label);
        }
        all.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Seq.CompareTo(b.Seq));

        // 창이 바뀌거나, 단계 머리줄([n/N])이 나오거나, 줄이 많으면 새 장으로 나눈다.
        var pages = new List<Page>();
        Page? page = null;
        List<Run>? line = null;
        int? window = null;
        string? step = null;

        void NewPage() { page = new Page(labels[window!.Value], step, [], whens[window.Value]); pages.Add(page); line = null; }
        void EndLine() { if (line is null) { page!.Lines.Add([]); } line = null; }
        void Add(Run r)
        {
            if (line is null)
            {
                if (page!.Lines.Count >= MaxLines) NewPage();
                line = [];
                page!.Lines.Add(line);
            }
            line.Add(r);
        }

        foreach (var (_, _, e) in all)
        {
            if (window != e.Window) { window = e.Window; step = null; NewPage(); }
            switch (e.Kind)
            {
                case "in":
                    Add(new Run(e.Text, Palette["White"], Bold: true));
                    EndLine();
                    break;
                case "note":
                    if (line is not null) EndLine();
                    Add(new Run("▶ " + e.Text, "8A8A8A", Italic: true));
                    EndLine();
                    break;
                default:
                    var color = Palette.TryGetValue(e.Color ?? "Gray", out var hex) ? hex : Palette["Gray"];
                    var parts = e.Text.Split('\n');
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (i > 0) EndLine();
                        var text = parts[i].Replace("\r", "", StringComparison.Ordinal);
                        if (text.Length == 0) continue;
                        if (line is null && StepHeader().IsMatch(text))
                        {
                            step = text.Trim();
                            if (page!.Lines.Any(l => l.Count > 0)) NewPage();
                            else page = pages[^1] = page with { Step = step };
                        }
                        Add(new Run(text, color));
                    }
                    break;
            }
        }

        // 각 장 앞뒤 빈 줄 정리(화면 글자는 그대로, 위아래 공백만 줄임)
        foreach (var p in pages)
        {
            while (p.Lines.Count > 0 && p.Lines[0].Count == 0) p.Lines.RemoveAt(0);
            while (p.Lines.Count > 0 && p.Lines[^1].Count == 0) p.Lines.RemoveAt(p.Lines.Count - 1);
        }
        pages.RemoveAll(p => p.Lines.Count == 0);

        var slides = new List<Slide>();
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var s = new Slide { Background = "000000" };
            slides.Add(s);
            s.Box(0.35, 0.25, SlideW - 0.7, 0.42, [new P(p.Window + (p.Step is null ? "" : "   " + p.Step), 13, "E6E6E6", Bold: true, After: 0)],
                "2B2B2B", geom: "rect", anchor: "ctr", inset: 0.08, name: "TitleBar");
            s.Terminal(0.35, 0.67, SlideW - 0.7, 6.38, p.Lines, FontPt, "0C0C0C", 0.2);
            s.Text(0.35, 7.12, 9.5, 0.28, new P($"{title} · 실제 화면 기록({p.When}) · 굵은 글자 = 입력, ▶ 회색 = 다른 프로그램 화면(기록 안 함)", 9, "8A8A8A", After: 0));
            s.Text(SlideW - 1.4, 7.12, 1.05, 0.28, new P($"{i + 1} / {pages.Count}", 9, "8A8A8A", Align: "r", After: 0));
        }

        Fonts.Body = "맑은 고딕";
        Deck.Write(output, slides, title);
        using (var doc = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(output, false))
        {
            var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(doc).ToList();
            foreach (var e in errors.Take(10)) Console.WriteLine($"[검증] {e.Part?.Uri} {e.Description}");
            Console.WriteLine($"검증 오류 {errors.Count}건");
        }
        Console.WriteLine($"기록 {files.Count}개 창 → 화면 {slides.Count}장 → {Path.GetFullPath(output)}{(maskFile is null ? "" : " (공개용 치환 적용)")}");
        if (previewDir is not null && OperatingSystem.IsWindows())
        {
            var problems = Preview.Render(slides, previewDir);
            foreach (var pr in problems) Console.WriteLine($"[넘침] {pr}");
            Console.WriteLine($"이미지 {slides.Count}장 → {Path.GetFullPath(previewDir)}, 넘침 {problems.Count}건");
        }
        return 0;
    }

    /// <summary>창 제목: 관리자 창은 하는 일을 쉬운 말로(실제 인수 값은 넣지 않음).</summary>
    private static string Label(string command, bool elevated)
    {
        var c = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var key = c.Length == 0 ? "" : c.Length > 1 && !c[1].StartsWith("--", StringComparison.Ordinal) ? $"{c[0]} {c[1]}" : c[0];
        var what = key switch
        {
            "" => "WSL Remote Setup",
            "install" or "resume" => "WSL Remote Setup — 설치",
            "uninstall" => "WSL Remote Setup — 되돌리기",
            "status" => "WSL Remote Setup — 상태",
            "internal-install-wsl" => "WSL 설치",
            "forward apply" => "연결 통로 열기",
            "forward remove" => "외부 연결 닫기",
            "autostart install" => "자동 실행 설정",
            "autostart remove" => "자동 실행 중지",
            "autostart prelogin-on" => "로그인 전 접속 켜기",
            "autostart prelogin-off" => "로그인 전 접속 끄기",
            "network apply" => "이 PC 주소 고정",
            "network revert" => "주소 고정 해제",
            _ => "WSL Remote Setup",
        };
        return elevated ? $"관리자: {what}" : what;
    }
}
