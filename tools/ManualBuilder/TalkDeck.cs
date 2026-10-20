using System.Text;

namespace ManualBuilder;

/// <summary>
/// 유튜브 발표 자료: 설명 슬라이드 + 실제 화면 기록(공개용 치환) + 슬라이드별 발표 대본(Markdown).
/// 사용: --talk &lt;출력.pptx&gt; [미리보기폴더] --recordings &lt;기록 폴더&gt; --mask &lt;치환.json&gt; --assets &lt;그림 폴더&gt; --script &lt;대본.md&gt;
/// </summary>
public static class TalkDeck
{
    private const double W = 13.333;
    private static readonly List<Slide> Slides = [];
    private static readonly List<(string Title, string Script)> Notes = [];
    private const string Footer = "WSL Remote Setup · 집 PC를 Linux 작업 서버로";

    private static Slide Content(string title, string? lead, string script)
    {
        var s = new Slide();
        s.Text(0.6, 0.42, 12.1, 0.75, new P(title, 30, Pal.Ink, Bold: true, After: 0));
        if (lead is not null) s.Text(0.6, 1.12, 12.1, 0.5, new P(lead, 15, Pal.Muted, After: 0));
        Slides.Add(s);
        s.Text(0.6, 7.02, 7, 0.3, new P(Footer, 10, Pal.Muted, After: 0));
        s.Text(W - 1.6, 7.02, 1.0, 0.3, new P(Slides.Count.ToString(), 10, Pal.Muted, Align: "r", After: 0));
        Notes.Add((title, script));
        return s;
    }

    private static void Section(string no, string title, string sub, string script)
    {
        var s = new Slide { Background = Pal.Navy };
        Slides.Add(s);
        s.Box(0.9, 2.35, 1.5, 1.5, [new P(no, 40, Pal.White, Bold: true, Align: "ctr", After: 0)], Pal.Teal, geom: "ellipse", anchor: "ctr", inset: 0, name: "SectionNo");
        s.Text(2.8, 2.45, 9.8, 0.9, new P(title, 36, Pal.White, Bold: true, After: 0));
        s.Text(2.8, 3.4, 9.8, 0.6, new P(sub, 18, "AFC2D6", After: 0));
        Notes.Add(($"[구간] {title}", script));
    }

    private static void Snapshot(TranscriptDeck.TranscriptPage page, string caption, string script)
    {
        var s = new Slide { Background = "000000" };
        Slides.Add(s);
        TranscriptDeck.RenderPage(s, page, $"실제 화면(공개용 예시 값) · 굵은 글자 = 입력 · ▶ = 다른 프로그램 화면 · {caption}", Slides.Count.ToString());
        Notes.Add(($"[실제 화면] {page.Window}{(page.Step is null ? "" : " " + page.Step)}", script));
    }

    private static void Num(Slide s, double x, double y, double d, string n, string fill = Pal.Teal) =>
        s.Box(x, y, d, d, [new P(n, d * 26, Pal.White, Bold: true, Align: "ctr", After: 0)], fill, geom: "ellipse", anchor: "ctr", inset: 0, name: "Number");

    private static void Card(Slide s, double x, double y, double w, double h, string fill = Pal.Card) =>
        s.Box(x, y, w, h, null, fill, null, "roundRect", shadow: fill == Pal.Card, name: "Card");

    public static int Run(string output, string? previewDir, string recordings, string? mask, string assets, string scriptPath)
    {
        var install = TranscriptDeck.LoadPages(Path.Combine(recordings, "01-install"), mask);
        var uninstall = TranscriptDeck.LoadPages(Path.Combine(recordings, "02-uninstall"), mask);
        var ubuntu = TranscriptDeck.LoadPages(Path.Combine(recordings, "02b-uninstall-ubuntu"), mask);
        var ports = TranscriptDeck.LoadPages(Path.Combine(recordings, "04-ports-add"), mask);
        static TranscriptDeck.TranscriptPage Pick(List<TranscriptDeck.TranscriptPage> pages, int n) => pages[n - 1];

        // 1. 표지 ──────────────────────────────────────────
        {
            var s = new Slide { Background = Pal.Navy };
            Slides.Add(s);
            s.Text(0.8, 1.5, 9, 0.4, new P("WSL Remote Setup · 실습 강의", 14, "7FB7B9", Bold: true, After: 0));
            s.Text(0.8, 1.95, 9.2, 1.9,
                new P("집 PC를 Linux 작업 서버로", 44, Pal.White, Bold: true, After: 4),
                new P("소파에서 노트북으로 이어서 일하기", 28, "D9E2EC", After: 0));
            s.Text(0.8, 4.4, 9, 1.2,
                new P("리눅스가 왜 필요한지 → WSL → Windows Pro → 설치 → 접속 → 서비스 포트 → 되돌리기", 14, "AFC2D6", After: 6),
                new P("github.com/sungbeom78/wsl-remote-setup  ·  문의 https://www.edubeom.com/", 13, "AFC2D6", After: 0));
            string[] tags = ["Linux", "WSL", "SSH"];
            for (var i = 0; i < 3; i++)
                s.Box(10.4, 1.4 + i * 1.6, 1.3, 1.3, [new P(tags[i], 19, Pal.White, Bold: true, Align: "ctr", After: 0)], Pal.Teal, geom: "ellipse", anchor: "ctr", inset: 0, name: "Tag");
            Notes.Add(("표지", "안녕하세요. 오늘은 집이나 사무실에 있는 Windows PC를 Linux 작업 서버로 만들고, 소파나 침대에서 노트북으로 그 PC에 접속해 일하는 방법을 보여 드리겠습니다. 프로그램 하나로 설치부터 되돌리기까지 실제 화면으로 따라가 보겠습니다."));
        }

        // 2. 오늘 할 이야기 ────────────────────────────────
        {
            var s = Content("오늘 할 이야기", "개념 → 준비 → 실습 순서로 갑니다",
                "오늘은 크게 일곱 부분입니다. 먼저 왜 리눅스가 필요한지, 그리고 Windows에서 리눅스를 쓰는 방법인 WSL을 설명합니다. 원격 데스크톱 때문에 Windows Pro가 왜 필요한지도 짚고, 이어서 실제 설치, 노트북 접속, 서비스 포트 관리, 되돌리기까지 실습하겠습니다.");
            string[] items = ["왜 리눅스인가", "WSL이란", "Windows Pro가 필요한 이유", "설치 실습", "노트북에서 접속", "서비스 포트 관리", "되돌리기 실습"];
            for (var i = 0; i < items.Length; i++)
            {
                var col = i < 4 ? 0 : 1;
                var row = i < 4 ? i : i - 4;
                var x = 0.6 + col * 6.2;
                var y = 1.95 + row * 1.15;
                Card(s, x, y, 5.9, 0.95);
                Num(s, x + 0.25, y + 0.18, 0.6, (i + 1).ToString(), i >= 3 ? Pal.Navy : Pal.Teal);
                s.Text(x + 1.1, y + 0.25, 4.6, 0.5, new P(items[i], 19, Pal.Ink, Bold: true, After: 0));
            }
            s.Text(6.8, 5.55, 5.9, 0.9, new P("남색 번호 = 실제 화면으로 보는 실습", 13, Pal.Muted, After: 0));
        }

        // ── 1. 왜 리눅스인가 ───────────────────────────────
        Section("1", "왜 리눅스인가", "개발·AI·서버 도구는 리눅스를 기준으로 만들어집니다",
            "첫 번째 이야기, 왜 리눅스인가입니다.");
        {
            var s = Content("개발·AI 도구는 리눅스가 기본", "Windows로도 되지만, 안내서와 도구는 대부분 리눅스를 먼저 생각합니다",
                "서버와 클라우드는 대부분 리눅스로 돌아갑니다. 그래서 개발 도구나 AI 라이브러리의 설치 안내도 리눅스 명령을 기준으로 쓰여 있는 경우가 많습니다. Docker 같은 컨테이너도 리눅스 기술이고, 반복 작업을 명령어로 자동화하기도 쉽습니다. 내 PC에도 리눅스 환경이 있으면 이런 안내를 그대로 따라 할 수 있습니다.");
            (string t, string d)[] cards =
            [
                ("서버와 같은 환경", "서비스가 실제로 돌아갈 곳은 대부분 리눅스 서버입니다. 내 PC에서 같은 환경으로 만들고 시험합니다."),
                ("안내서를 그대로", "개발 도구·AI 라이브러리 설치 안내는 리눅스 명령 기준이 많습니다. 바꿔 읽을 필요가 없습니다."),
                ("컨테이너·자동화", "Docker는 리눅스 기술입니다. 셸 명령으로 반복 작업을 자동화하기도 쉽습니다."),
            ];
            for (var i = 0; i < 3; i++)
            {
                var x = 0.6 + i * 4.15;
                Card(s, x, 1.95, 3.85, 3.6);
                Num(s, x + 0.25, 2.2, 0.6, (i + 1).ToString());
                s.Text(x + 0.25, 3.0, 3.35, 0.5, new P(cards[i].t, 19, Pal.Ink, Bold: true, After: 0));
                s.Text(x + 0.25, 3.6, 3.35, 1.8, new P(cards[i].d, 14, Pal.Muted, After: 0));
            }
            s.Box(0.6, 5.8, 12.13, 0.85, [new P("목표: Windows는 그대로 쓰면서, 리눅스 작업 환경을 하나 더 갖는 것", 16, Pal.Teal, Bold: true, After: 0)],
                Pal.TealSoft, geom: "roundRect", anchor: "ctr", inset: 0.25, name: "Goal");
        }
        {
            var s = Content("Windows PC에서 리눅스를 쓰는 방법", "네 가지 방법 비교 — 오늘은 WSL을 씁니다",
                "Windows PC에서 리눅스를 쓰는 방법은 여러 가지입니다. 듀얼 부팅은 재부팅해야 하고 동시에 쓸 수 없습니다. 가상 머신은 무겁고 설정이 많습니다. 리눅스 PC를 따로 두면 장비가 하나 더 필요합니다. WSL은 Windows와 함께 가볍게 돌아가고 Microsoft가 공식으로 지원합니다. 오늘은 WSL을 씁니다.");
            string[] head = ["방법", "좋은 점", "아쉬운 점"];
            string[][] rows =
            [
                ["듀얼 부팅", "리눅스를 PC 전체 성능으로", "재부팅해야 바뀜, 동시에 못 씀"],
                ["가상 머신", "완전히 분리된 환경", "무겁고 설정이 많음"],
                ["리눅스 PC 따로", "가장 자유로움", "장비·관리가 하나 더"],
                ["WSL", "Windows와 함께, 가볍고 빠름, 공식 지원", "밖에서 접속하려면 설정 필요 → 오늘의 주제"],
            ];
            double[] cx = [0.6, 3.6, 8.1];
            double[] cw = [3.0, 4.5, 4.63];
            s.Box(0.6, 1.85, 12.13, 0.6, null, Pal.Navy, geom: "rect", name: "Header");
            for (var c = 0; c < 3; c++) s.Text(cx[c] + 0.2, 1.97, cw[c] - 0.3, 0.4, new P(head[c], 14, Pal.White, Bold: true, After: 0));
            for (var r = 0; r < rows.Length; r++)
            {
                var y = 2.45 + r * 0.95;
                var hl = r == 3;
                s.Box(0.6, y, 12.13, 0.95, null, hl ? Pal.TealSoft : (r % 2 == 0 ? Pal.Card : "F8FAFB"), Pal.Line, name: "Row", lineW: 0.75);
                s.Text(cx[0] + 0.2, y + 0.27, cw[0] - 0.3, 0.45, new P(rows[r][0], 17, hl ? Pal.Teal : Pal.Ink, Bold: true, After: 0));
                s.Text(cx[1] + 0.2, y + 0.29, cw[1] - 0.3, 0.45, new P(rows[r][1], 14, Pal.Ink, After: 0));
                s.Text(cx[2] + 0.2, y + 0.29, cw[2] - 0.3, 0.45, new P(rows[r][2], 14, hl ? Pal.Teal : Pal.Muted, Bold: hl, After: 0));
            }
        }

        // ── 2. WSL ────────────────────────────────────────
        Section("2", "WSL이란", "Windows 안에서 진짜 리눅스를 돌리는 공식 기능",
            "두 번째, WSL입니다.");
        {
            var s = Content("WSL(Windows Subsystem for Linux)", "Windows 10·11에 들어 있는 Microsoft 공식 기능입니다",
                "WSL은 Windows 안에서 리눅스를 돌리는 Microsoft 공식 기능입니다. WSL 2는 가벼운 가상화 위에서 진짜 리눅스 커널을 씁니다. 몇 초 만에 켜지고, Windows 파일과도 오갈 수 있습니다. 우리는 그 위에 Ubuntu 24.04를 설치해서 씁니다.");
            Card(s, 0.6, 1.95, 6.0, 4.7);
            s.Text(0.9, 2.15, 5.5, 4.4,
                new P("진짜 리눅스 커널(WSL 2)", 18, Pal.Ink, Bold: true, After: 4),
                new P("가벼운 가상화 위에서 리눅스 커널이 직접 돌아갑니다.", 14, Pal.Muted, After: 14),
                new P("몇 초 만에 켜짐", 18, Pal.Ink, Bold: true, After: 4),
                new P("필요할 때 켜지고, 쓰지 않으면 알아서 꺼집니다.", 14, Pal.Muted, After: 14),
                new P("Windows와 함께", 18, Pal.Ink, Bold: true, After: 4),
                new P("Windows 프로그램을 쓰면서 리눅스 창을 같이 엽니다.", 14, Pal.Muted, After: 14),
                new P("배포판 선택", 18, Pal.Ink, Bold: true, After: 4),
                new P("오늘은 Ubuntu 24.04를 씁니다.", 14, Pal.Muted, After: 0));
            // 구조 그림
            s.Box(7.0, 1.95, 5.73, 4.7, null, Pal.TealSoft, geom: "roundRect", name: "Diagram");
            s.Text(7.3, 2.1, 5.2, 0.4, new P("한 대의 PC 안", 14, Pal.Teal, Bold: true, After: 0));
            s.Box(7.3, 2.6, 5.13, 1.3, [new P("Windows", 20, Pal.Ink, Bold: true, After: 2), new P("평소 쓰는 프로그램 · 192.168.1.x (공유기 주소)", 13, Pal.Muted, After: 0)],
                Pal.Card, Pal.Line, "roundRect", inset: 0.2, name: "Windows");
            s.Box(9.6, 4.0, 0.5, 0.45, null, Pal.Teal, geom: "downArrow", name: "Arrow");
            s.Box(7.3, 4.55, 5.13, 1.8, [new P("WSL 2 · Ubuntu", 20, Pal.Ink, Bold: true, After: 2), new P("리눅스 작업 환경 · 172.24.x.x (PC 안에서만 보이는 주소)", 13, Pal.Muted, After: 4),
                new P("→ 다른 기기에서는 바로 보이지 않습니다", 13, Pal.Amber, Bold: true, After: 0)], Pal.Card, Pal.Line, "roundRect", inset: 0.2, name: "Wsl");
        }
        {
            var s = Content("노트북에서 쓰려면 생기는 문제", "WSL은 'PC 안에서' 쓰도록 만들어져 있습니다. 이 프로그램이 이 네 가지를 해결합니다",
                "WSL을 다른 기기에서 쓰려면 문제가 네 가지 있습니다. WSL은 PC 안에서만 보이는 주소를 쓰고, 그 주소는 다시 켤 때마다 바뀔 수 있습니다. 아무것도 실행하지 않으면 잠시 뒤 꺼지고, PC를 켠 뒤에도 로그인해야 켜집니다. 오늘 쓰는 프로그램이 이 네 가지를 자동으로 해결합니다.");
            (string p, string fix)[] items =
            [
                ("PC 안에서만 보이는 주소", "이 PC의 1004번으로 온 접속을 WSL로 넘기는 통로를 만듭니다"),
                ("다시 켜면 주소가 바뀜", "바뀐 주소를 찾아 통로를 자동으로 고칩니다"),
                ("안 쓰면 잠시 뒤 꺼짐", "WSL을 켜 둔 채로 1분마다 점검합니다"),
                ("로그인해야 켜짐", "(선택) 로그인하지 않아도 켜지면 약 1분 뒤부터 접속"),
            ];
            for (var i = 0; i < items.Length; i++)
            {
                var y = 1.95 + i * 1.18;
                Card(s, 0.6, y, 12.13, 1.0);
                Num(s, 0.8, y + 0.22, 0.56, (i + 1).ToString(), Pal.Amber);
                s.Text(1.6, y + 0.29, 4.3, 0.45, new P(items[i].p, 17, Pal.Ink, Bold: true, After: 0));
                s.Box(6.0, y + 0.3, 0.5, 0.4, null, Pal.Teal, geom: "rightArrow", name: "Arrow");
                s.Text(6.7, y + 0.3, 5.9, 0.45, new P(items[i].fix, 15, Pal.Teal, After: 0));
            }
        }

        // ── 3. Windows Pro ────────────────────────────────
        Section("3", "Windows Pro가 필요한 이유", "큰 화면으로 접속하는 원격 데스크톱 때문입니다",
            "세 번째, Windows Pro가 왜 필요한지 보겠습니다.");
        {
            var s = Content("Windows Home과 Pro의 차이", "원격 데스크톱으로 '접속을 받는 쪽'은 Pro 이상이어야 합니다",
                "Windows Home에서도 WSL과 SSH는 됩니다. 다만 노트북에서 이 PC 화면 전체를 원격 데스크톱으로 보려면, 접속을 받는 이 PC가 Pro 이상이어야 합니다. 에디션은 설정, 시스템, 정보에서 확인할 수 있습니다.");
            string[] head = ["기능", "Home", "Pro 이상"];
            string[][] rows =
            [
                ["WSL · Ubuntu", "가능", "가능"],
                ["SSH로 리눅스 접속", "가능", "가능"],
                ["원격 데스크톱으로 이 PC 화면 받기", "안 됨", "가능"],
                ["원격 데스크톱으로 다른 PC에 접속하기", "가능", "가능"],
            ];
            double[] cx = [0.6, 6.6, 9.65];
            double[] cw = [6.0, 3.05, 3.08];
            s.Box(0.6, 1.85, 12.13, 0.6, null, Pal.Navy, geom: "rect", name: "Header");
            for (var c = 0; c < 3; c++) s.Text(cx[c] + 0.2, 1.97, cw[c] - 0.3, 0.4, new P(head[c], 14, Pal.White, Bold: true, Align: c == 0 ? "l" : "ctr", After: 0));
            for (var r = 0; r < rows.Length; r++)
            {
                var y = 2.45 + r * 0.8;
                s.Box(0.6, y, 12.13, 0.8, null, r == 2 ? Pal.AmberSoft : (r % 2 == 0 ? Pal.Card : "F8FAFB"), Pal.Line, name: "Row", lineW: 0.75);
                s.Text(cx[0] + 0.2, y + 0.22, cw[0] - 0.3, 0.42, new P(rows[r][0], 16, Pal.Ink, Bold: r == 2, After: 0));
                for (var c = 1; c < 3; c++)
                    s.Text(cx[c] + 0.2, y + 0.22, cw[c] - 0.3, 0.42, new P(rows[r][c], 16, rows[r][c] == "안 됨" ? Pal.Red : Pal.Teal, Bold: true, Align: "ctr", After: 0));
            }
            s.Box(0.6, 5.85, 12.13, 0.8, [new P("확인: 설정 → 시스템 → 정보 → 'Windows 사양'의 에디션.   Home이어도 SSH 접속까지는 그대로 따라 할 수 있습니다.", 14, Pal.Ink, After: 0)],
                Pal.TealSoft, geom: "roundRect", anchor: "ctr", inset: 0.25, name: "Check");
        }
        {
            var s = Content("이런 장면에 씁니다", "책상의 PC는 서버, 소파의 노트북은 큰 화면",
                "이런 장면을 생각하시면 됩니다. 책상 위 데스크톱이나 미니 PC는 계속 켜 두는 작업 서버가 되고, 소파나 침대에서는 노트북으로 접속합니다. 터미널은 SSH로, 화면 전체는 원격 데스크톱으로, 코드는 VS Code 원격 기능으로 열 수 있습니다.");
            (string t, string a, string b)[] nodes =
            [
                ("노트북", "소파·침대에서", "SSH · 원격 데스크톱 · VS Code"),
                ("이 PC (Windows)", "책상 위 데스크톱·미니 PC", "켜 두면 계속 접속 가능"),
                ("WSL 안의 Ubuntu", "실제로 일하는 곳", "개발·AI·서버 도구"),
            ];
            double[] xs = [0.6, 4.95, 9.3];
            for (var i = 0; i < 3; i++)
            {
                Card(s, xs[i], 2.0, 3.43, 2.0);
                s.Text(xs[i] + 0.3, 2.2, 2.9, 0.5, new P(nodes[i].t, 19, i == 1 ? Pal.Teal : Pal.Ink, Bold: true, After: 0));
                s.Text(xs[i] + 0.3, 2.8, 2.9, 1.0, new P(nodes[i].a, 14, Pal.Muted, After: 2), new P(nodes[i].b, 14, Pal.Muted, After: 0));
                if (i < 2) s.Box(xs[i] + 3.55, 2.75, 0.75, 0.5, null, Pal.Teal, geom: "rightArrow", name: "Arrow");
            }
            s.Box(0.6, 4.5, 12.13, 1.9,
            [
                new P("같은 공유기 안에서만", 16, Pal.Teal, Bold: true, After: 6),
                new P("집 안 Wi-Fi에 연결된 기기만 들어올 수 있습니다. 인터넷 전체에는 열지 않습니다.", 15, Pal.Ink, Bullet: true, After: 4),
                new P("밖에서 접속하려면 VPN 같은 별도 도구가 필요합니다(오늘 범위 밖).", 15, Pal.Ink, Bullet: true, After: 0),
            ], Pal.TealSoft, geom: "roundRect", inset: 0.25, name: "Scope");
        }

        // ── 4. 설치 실습 ───────────────────────────────────
        Section("4", "설치 실습", "EXE 하나, 질문 6개, 8단계",
            "이제 실제 설치 화면으로 가 보겠습니다. 화면은 실제로 설치하면서 기록한 그대로이고, 주소와 계정 이름만 예시 값으로 바꿨습니다.");
        {
            var s = Content("준비물과 내려받기", "아래가 갖춰지면 30분 안팎이면 끝납니다",
                "준비물은 Windows 10이나 11, 관리자 계정, 인터넷, 그리고 노트북과 같은 공유기입니다. 프로그램은 GitHub의 Releases 페이지에서 EXE 하나만 받으면 됩니다. 시작하기 전에 명령 프롬프트에서 ipconfig로 이 PC 주소를 적어 두세요.");
            (string t, string d)[] items =
            [
                ("Windows 10/11 (Pro 권장)", "Home은 원격 데스크톱 없이 SSH만"),
                ("관리자 계정", "중간에 UAC 확인 창이 뜹니다"),
                ("인터넷", "WSL·Ubuntu를 내려받습니다"),
                ("같은 공유기", "노트북과 이 PC가 같은 Wi-Fi"),
            ];
            for (var i = 0; i < items.Length; i++)
            {
                var y = 1.95 + i * 0.95;
                Card(s, 0.6, y, 6.6, 0.8);
                Num(s, 0.8, y + 0.15, 0.5, (i + 1).ToString());
                s.Text(1.5, y + 0.1, 5.5, 0.4, new P(items[i].t, 16, Pal.Ink, Bold: true, After: 0));
                s.Text(1.5, y + 0.45, 5.5, 0.3, new P(items[i].d, 12.5, Pal.Muted, After: 0));
            }
            s.Box(7.6, 1.95, 5.13, 4.55, null, Pal.TealSoft, geom: "roundRect", name: "Download");
            s.Text(7.9, 2.15, 4.6, 0.45, new P("내려받기", 18, Pal.Teal, Bold: true, After: 0));
            s.Text(7.9, 2.7, 4.6, 0.7, new P("GitHub → Releases에서 EXE 하나", 14, Pal.Ink, After: 0));
            s.Box(7.9, 3.3, 4.55, 0.55, [new P("github.com/sungbeom78/wsl-remote-setup", 12, Pal.Ink, Mono: true, After: 0)], Pal.White, anchor: "ctr", inset: 0.15, name: "Url");
            s.Text(7.9, 4.05, 4.6, 0.5, new P("이 PC 주소 확인(명령 프롬프트)", 14, Pal.Ink, After: 0));
            s.Box(7.9, 4.55, 4.55, 0.55, [new P("ipconfig  →  IPv4 주소", 13, Pal.Ink, Mono: true, After: 0)], Pal.White, anchor: "ctr", inset: 0.15, name: "Ip");
            s.Text(7.9, 5.3, 4.6, 1.0, new P("설치 매뉴얼(PowerPoint)도 같은 곳에 있습니다.", 13, Pal.Muted, After: 0));
        }
        Snapshot(Pick(install, 1), "EXE를 더블클릭하면 나오는 메뉴",
            "EXE를 더블클릭하면 이렇게 메뉴가 나옵니다. 처음 설치는 1번입니다. 굵은 글자가 제가 입력한 값입니다.");
        Snapshot(Pick(install, 2), "설치 질문 6가지 — 대부분 Enter",
            "설치 질문은 여섯 가지입니다. 대괄호 안이 기본값이고, Enter만 누르면 기본값을 씁니다. 저는 마지막 질문, 로그인 전에도 접속할지에만 y를 입력했습니다.");
        Snapshot(Pick(install, 5), "Ubuntu를 내려받아 설치",
            "Ubuntu를 이 계정에 설치하는 단계입니다. 무엇을 하는지 먼저 보여 주고 확인을 받은 뒤 진행합니다. 회색 줄 부분에서는 내려받는 진행 화면이 나옵니다.");
        Snapshot(Pick(install, 6), "Linux 사용자와 비밀번호",
            "Linux 사용자를 만들고 비밀번호를 정합니다. Windows 비밀번호와는 따로입니다. 입력하는 글자는 화면에 보이지 않으니 천천히 두 번 입력하세요. 이 프로그램은 비밀번호를 읽거나 저장하지 않습니다.");
        Snapshot(Pick(install, 7), "SSH 서버 설치",
            "다음은 Ubuntu 안에 SSH 서버를 설치합니다. 공개키 등록은 나중에 해도 되니 n을 누르고 넘어갑니다.");
        Snapshot(Pick(install, 11), "SSH 서버 점검 결과",
            "설치가 끝나면 SSH 설정을 점검하고, 22번에서 정상적으로 받는지까지 확인합니다.");
        Snapshot(Pick(install, 13), "관리자 창 — 연결 통로 열기",
            "여기서부터 관리자 권한이 필요한 단계입니다. UAC 창에서 예를 누르면 이런 관리자 창이 따로 열립니다. 바꿀 내용을 한 번 더 보여 주고, Y를 누르면 연결 통로와 방화벽 규칙을 만듭니다. 방화벽은 같은 공유기의 기기만 허용합니다.");
        Snapshot(Pick(install, 16), "관리자 창 — 자동 실행 설정",
            "마지막 관리자 단계는 자동 실행 설정입니다. 백그라운드 연결 관리 프로그램과 로그인 시 자동 실행을 등록합니다. 그래서 재부팅해도 연결이 저절로 다시 살아납니다.");
        Snapshot(Pick(install, 18), "설치 완료 — 모두 정상",
            "설치가 끝나면 상태 화면이 나옵니다. SSH 접속부터 절전까지 모두 정상이면 끝입니다. 이 화면은 언제든 메뉴 2번으로 다시 볼 수 있습니다.");

        // ── 5. 접속 ───────────────────────────────────────
        Section("5", "노트북에서 접속", "SSH · 원격 데스크톱 · VS Code",
            "이제 노트북에서 접속해 보겠습니다.");
        {
            var s = Content("노트북에서 접속하는 방법", "주소는 이 PC의 IPv4 주소, 포트는 1004입니다",
                "노트북에서는 터미널에서 ssh 명령 한 줄로 접속합니다. mRemoteNG나 PuTTY 같은 도구라면 호스트, 포트 1004, 사용자 linux_admin을 넣으면 됩니다. 화면 전체가 필요하면 원격 데스크톱으로, 코드 편집은 VS Code의 Remote SSH로 같은 주소에 접속합니다. 처음 접속할 때 서버를 신뢰할지 물으면 수락합니다.");
            (string t, string body, bool code)[] ways =
            [
                ("터미널(PowerShell)", "ssh -p 1004 linux_admin@192.168.1.222", true),
                ("mRemoteNG · PuTTY", "호스트 = 이 PC 주소 · 포트 1004 · SSH 버전 2 · 사용자 linux_admin", false),
                ("원격 데스크톱(큰 화면)", "'이 PC 주소:원격 데스크톱 포트'로 접속 (Pro 필요)", false),
                ("VS Code", "Remote - SSH 확장으로 같은 주소·포트에 접속", false),
            ];
            for (var i = 0; i < ways.Length; i++)
            {
                var y = 1.95 + i * 1.1;
                Card(s, 0.6, y, 12.13, 0.92);
                s.Text(0.9, y + 0.27, 3.6, 0.42, new P(ways[i].t, 16, Pal.Teal, Bold: true, After: 0));
                if (ways[i].code)
                    s.Box(4.6, y + 0.17, 7.9, 0.58, [new P(ways[i].body, 15, Pal.Ink, Mono: true, After: 0)], Pal.Code, anchor: "ctr", inset: 0.15, name: "Code");
                else s.Text(4.6, y + 0.29, 7.9, 0.42, new P(ways[i].body, 15, Pal.Ink, After: 0));
            }
            s.Text(0.6, 6.45, 12.1, 0.4, new P("192.168.1.222는 예시입니다. 자기 PC의 주소로 바꾸세요 — 끝자리 하나만 틀려도 접속되지 않습니다.", 13, Pal.Amber, Bold: true, After: 0));
        }

        // ── 6. 서비스 포트 ─────────────────────────────────
        Section("6", "서비스 포트 관리", "WSL 안의 웹 서버·Jupyter·Docker를 노트북에서",
            "여섯 번째는 서비스 포트 관리입니다. WSL 안에서 띄운 웹 서버나 Jupyter 같은 서비스를 노트북에서 쓰는 방법입니다.");
        {
            var s = Content("서비스 포트 관리(메뉴 7)", "SSH처럼, WSL 안 서비스에도 통로를 만들어 줍니다",
                "SSH 말고도 WSL 안에서 여러 서비스를 띄웁니다. 메뉴 7에서 열려 있는 포트를 찾아 골라 주면, 노트북에서 들어올 통로와 방화벽 규칙을 만들어 줍니다. 한 번 열어 두면 재부팅해도 유지되고, 서비스가 꺼져 있어도 통로는 그대로 두었다가 켜지면 바로 연결됩니다.");
            (string t, string d)[] f =
            [
                ("찾아서 고르기", "WSL 안에서 열린 포트를 보여 줍니다. 번호만 고르면 됩니다."),
                ("재부팅해도 유지", "백그라운드 프로그램이 통로와 방화벽을 계속 지킵니다."),
                ("지금 연결", "WSL을 다시 켠 직후에도 바로 모든 포트를 맞춥니다."),
                ("삭제", "통로와 방화벽 규칙만 지웁니다. 서비스는 그대로."),
            ];
            for (var i = 0; i < f.Length; i++)
            {
                var x = 0.6 + (i % 2) * 6.2;
                var y = 1.95 + (i / 2) * 2.1;
                Card(s, x, y, 5.93, 1.85);
                Num(s, x + 0.25, y + 0.25, 0.55, (i + 1).ToString());
                s.Text(x + 1.0, y + 0.3, 4.7, 0.45, new P(f[i].t, 18, Pal.Ink, Bold: true, After: 0));
                s.Text(x + 1.0, y + 0.85, 4.7, 0.9, new P(f[i].d, 14, Pal.Muted, After: 0));
            }
            s.Text(0.6, 6.3, 12.1, 0.4, new P("예: 노트북 → 192.168.1.222:8000 → WSL 안 8000번 웹 서버", 14, Pal.Teal, Bold: true, After: 0));
        }
        Snapshot(Pick(ports, 1), "메뉴 7 → 포트 추가",
            "메뉴 7에서 포트 추가를 고르면, 지금 WSL 안에서 열려 있는 포트를 찾아 줍니다. 8000번 파이썬 웹 서버를 골랐고, 이름과 외부 포트는 Enter로 기본값을 썼습니다. 9001번 docker-proxy 옆에는 WSL 안에서만 열려 있다는 경고가 나옵니다. 이건 잠시 뒤에 설명하겠습니다.");
        Snapshot(Pick(ports, 2), "관리자 창 — 서비스 포트 열기",
            "관리자 창에서 한 번 더 확인하면 통로와 방화벽 규칙이 만들어집니다.");
        {
            var s = Content("노트북 브라우저에서 확인", "이 PC 주소:8000 으로 WSL 안 웹 서버가 열립니다",
                "노트북 브라우저에서 이 PC 주소의 8000번으로 접속하면, WSL 안에서 띄운 웹 서버 화면이 나옵니다. 주소창의 주소는 예시 값으로 가렸습니다.");
            s.Image(Path.Combine(assets, "laptop-browser-8000.png"), 0.9, 1.95, 7.6, 3.62, "노트북 브라우저에서 이 PC 주소 8000번 접속 화면", Pal.Line);
            s.Box(8.9, 1.95, 3.83, 2.1,
            [
                new P("WSL 안에서", 15, Pal.Teal, Bold: true, After: 4),
                new P("python3 -m http.server 8000 --bind 0.0.0.0", 11.5, Pal.Ink, Mono: true, After: 12),
                new P("노트북에서", 15, Pal.Teal, Bold: true, After: 4),
                new P("http://192.168.1.222:8000", 12, Pal.Ink, Mono: true, After: 0),
            ], Pal.TealSoft, geom: "roundRect", inset: 0.25, name: "How");
        }
        {
            var s = Content("주의: 127.0.0.1에만 열린 서비스", "통로를 만들어도 WSL 안에서만 열린 서비스에는 들어갈 수 없습니다",
                "많이 막히는 부분입니다. 서비스가 127.0.0.1에만 열려 있으면, 통로를 만들어도 바깥에서 들어갈 수 없습니다. 그래서 목록에 경고가 나옵니다. 서비스를 0.0.0.0으로 열도록 설정을 바꾸면 됩니다. Docker라면 포트를 연결할 때 127.0.0.1을 빼면 됩니다.");
            (string t, string bad, string good)[] rows =
            [
                ("파이썬 웹 서버", "--bind 127.0.0.1", "--bind 0.0.0.0"),
                ("개발 서버(예: Vite, Jupyter)", "기본값 localhost", "--host 0.0.0.0 / --ip 0.0.0.0"),
                ("Docker", "-p 127.0.0.1:9001:80", "-p 9001:80"),
            ];
            s.Box(0.6, 1.85, 12.13, 0.6, null, Pal.Navy, geom: "rect", name: "Header");
            s.Text(0.8, 1.97, 3.8, 0.4, new P("서비스", 14, Pal.White, Bold: true, After: 0));
            s.Text(4.8, 1.97, 3.8, 0.4, new P("밖에서 안 됨", 14, Pal.White, Bold: true, After: 0));
            s.Text(8.8, 1.97, 3.8, 0.4, new P("밖에서 됨", 14, Pal.White, Bold: true, After: 0));
            for (var r = 0; r < rows.Length; r++)
            {
                var y = 2.45 + r * 0.95;
                s.Box(0.6, y, 12.13, 0.95, null, r % 2 == 0 ? Pal.Card : "F8FAFB", Pal.Line, name: "Row", lineW: 0.75);
                s.Text(0.8, y + 0.28, 3.8, 0.42, new P(rows[r].t, 15, Pal.Ink, Bold: true, After: 0));
                s.Text(4.8, y + 0.28, 3.8, 0.42, new P(rows[r].bad, 13.5, Pal.Red, Mono: true, After: 0));
                s.Text(8.8, y + 0.28, 3.8, 0.42, new P(rows[r].good, 13.5, Pal.Teal, Mono: true, After: 0));
            }
            s.Box(0.6, 5.55, 12.13, 1.0, [new P("데이터베이스(5432, 3306 등)는 한 번 더 경고합니다. 같은 공유기만 허용되지만, 비밀번호가 약하면 위험할 수 있습니다.", 14, Pal.Ink, After: 0)],
                Pal.AmberSoft, geom: "roundRect", anchor: "ctr", inset: 0.25, name: "Db");
        }
        Snapshot(Pick(ports, 3), "메뉴 7 → 포트 삭제",
            "다 쓴 포트는 메뉴 7에서 삭제합니다. 번호를 고르고 관리자 창에서 확인하면 통로와 방화벽 규칙만 지워집니다. WSL 안의 서비스는 그대로 둡니다.");

        // ── 7. 되돌리기 ────────────────────────────────────
        Section("7", "되돌리기 실습", "외부 연결 닫기부터 단계별로, 지우기 전에 백업",
            "마지막으로 되돌리기를 실습하겠습니다.");
        {
            var s = Content("되돌리기 5단계(메뉴 6)", "단계마다 영향을 알려 주고 확인을 받습니다. 원하지 않는 단계는 n",
                "되돌리기는 다섯 단계입니다. 가장 먼저 외부 연결을 닫고, 자동 실행을 멈추고, Ubuntu 안 SSH 설정을 되돌립니다. Ubuntu 삭제는 선택이고 기본값은 아니오입니다. 지울 때는 먼저 백업할지 묻습니다. 이 프로그램이 만든 것만 지우고, 원격 데스크톱이나 IP 주소는 건드리지 않습니다.");
            (string t, string d)[] steps =
            [
                ("외부 연결 닫기", "다른 기기에서 들어오는 통로와 방화벽 규칙 삭제"),
                ("자동 실행 중지", "백그라운드 프로그램·자동 실행·로그인 전 접속 삭제"),
                ("SSH 설정 되돌리기", "이 도구가 만든 설정과 등록한 키만 삭제"),
                ("(선택) Ubuntu 삭제", "기본값 아니오. 지우기 전에 백업 파일로 저장"),
                ("설치 이력 안내", "남겨 둔 프로그램과 직접 지우는 방법"),
            ];
            for (var i = 0; i < steps.Length; i++)
            {
                var y = 1.9 + i * 0.95;
                Card(s, 0.6, y, 12.13, 0.8, i == 3 ? Pal.AmberSoft : Pal.Card);
                Num(s, 0.8, y + 0.14, 0.52, (i + 1).ToString(), i == 3 ? Pal.Amber : Pal.Teal);
                s.Text(1.6, y + 0.2, 3.9, 0.42, new P(steps[i].t, 17, Pal.Ink, Bold: true, After: 0));
                s.Text(5.6, y + 0.23, 7.0, 0.42, new P(steps[i].d, 14, Pal.Muted, After: 0));
            }
        }
        Snapshot(Pick(uninstall, 2), "1단계 — 외부 연결 닫기",
            "1단계는 외부 연결 닫기입니다. 무엇이 바뀌는지 먼저 알려 줍니다. 원격 데스크톱과 다른 방화벽 규칙은 그대로 둔다는 것도 보입니다.");
        Snapshot(Pick(uninstall, 3), "관리자 창 — 연결 통로 제거",
            "관리자 창에서 이 도구가 만든 것만 골라서 지웁니다.");
        Snapshot(Pick(uninstall, 6), "관리자 창 — 자동 실행 설정 제거",
            "2단계도 마찬가지로 관리자 창에서, 이 도구가 등록한 서비스와 작업만 지웁니다.");
        Snapshot(Pick(ubuntu, 5), "4단계 — Ubuntu 백업 후 삭제",
            "Ubuntu를 지우는 단계입니다. y를 누르면 먼저 백업할지 묻고, Enter를 누르면 문서 폴더에 백업 파일 하나로 저장합니다. 백업이 확인되어야만 지우고, 마지막에 배포판 이름을 직접 입력해야 삭제됩니다. 삭제한 뒤에는 되살리는 명령도 알려 줍니다.");
        {
            var s = Content("백업에서 되살리기", "백업 파일 하나로 사용자·설정까지 그대로 돌아옵니다",
                "백업해 둔 Ubuntu는 이 명령 한 줄로 되살릴 수 있습니다. 사용자와 설정까지 백업한 그때 그대로 돌아옵니다.");
            s.Box(0.6, 1.95, 12.13, 1.5,
            [
                new P("wsl --import Ubuntu-24.04 \"%LOCALAPPDATA%\\WSL\\Ubuntu-24.04\" \"백업파일.tar\"", 15, Pal.Ink, Mono: true, After: 0),
            ], Pal.Code, anchor: "ctr", inset: 0.3, name: "Code");
            (string t, string d)[] notes =
            [
                ("어디에?", "문서 폴더의 'WSL 백업'(위치는 바꿀 수 있음)"),
                ("크기는?", "Ubuntu 안에서 쓰는 용량만큼(수 GB)"),
                ("덮어쓰기?", "같은 이름의 파일은 덮어쓰지 않음"),
            ];
            for (var i = 0; i < notes.Length; i++)
            {
                var x = 0.6 + i * 4.15;
                Card(s, x, 3.8, 3.85, 1.9);
                s.Text(x + 0.3, 4.0, 3.3, 0.45, new P(notes[i].t, 17, Pal.Teal, Bold: true, After: 0));
                s.Text(x + 0.3, 4.55, 3.3, 1.0, new P(notes[i].d, 14, Pal.Ink, After: 0));
            }
        }

        // 정리 ───────────────────────────────────────────────
        {
            var s = Content("정리", "오늘 본 것",
                "정리하겠습니다. 리눅스 작업 환경이 필요할 때 WSL이 가장 가벼운 방법입니다. 이 프로그램은 EXE 하나로 설치하고, 노트북에서 접속할 통로와 자동 복구를 만들어 줍니다. 서비스 포트도 메뉴에서 관리하고, 필요 없어지면 단계별로 깨끗하게 되돌릴 수 있습니다.");
            string[] items =
            [
                "리눅스 작업 환경은 WSL이 가장 가벼운 방법",
                "원격 데스크톱(큰 화면)을 받으려면 Windows Pro",
                "EXE 하나로 설치 — 질문 6개, 8단계, 자동 복구",
                "노트북: SSH · 원격 데스크톱 · VS Code",
                "서비스 포트: 메뉴 7에서 추가·삭제, 재부팅해도 유지",
                "되돌리기: 외부 연결부터 단계별로, 지우기 전 백업",
            ];
            for (var i = 0; i < items.Length; i++)
            {
                var y = 1.9 + i * 0.78;
                Num(s, 0.6, y + 0.07, 0.48, "✓");
                s.Text(1.3, y + 0.1, 11.4, 0.45, new P(items[i], 17, Pal.Ink, After: 0));
            }
        }
        {
            var s = new Slide { Background = Pal.Navy };
            Slides.Add(s);
            s.Text(0.8, 2.0, 11.7, 1.0, new P("감사합니다", 44, Pal.White, Bold: true, After: 0));
            s.Text(0.8, 3.2, 11.7, 1.6,
                new P("내려받기·소스: github.com/sungbeom78/wsl-remote-setup", 18, "D9E2EC", After: 8),
                new P("문의·의견: https://www.edubeom.com/", 18, "D9E2EC", After: 0));
            s.Text(0.8, 5.4, 11.7, 0.6, new P("MIT 라이선스 · 누구나 자유롭게 쓰고 고칠 수 있습니다", 14, "AFC2D6", After: 0));
            Notes.Add(("마무리", "오늘 보신 프로그램과 매뉴얼은 GitHub에서 받으실 수 있습니다. 궁금한 점이나 의견은 edubeom.com으로 남겨 주세요. 시청해 주셔서 감사합니다."));
        }

        // 대본
        var md = new StringBuilder();
        md.AppendLine("# 발표 대본 — 집 PC를 Linux 작업 서버로 (WSL Remote Setup)");
        md.AppendLine();
        md.AppendLine("슬라이드 번호마다 읽을 내용입니다. 실제 화면 슬라이드는 기록된 화면 그대로이며 주소·계정은 예시 값입니다.");
        md.AppendLine();
        for (var i = 0; i < Notes.Count; i++)
        {
            md.AppendLine($"## {i + 1}. {Notes[i].Title}");
            md.AppendLine();
            md.AppendLine(Notes[i].Script);
            md.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(scriptPath))!);
        File.WriteAllText(scriptPath, md.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"대본 {Notes.Count}개 → {Path.GetFullPath(scriptPath)}");
        if (Notes.Count != Slides.Count) Console.WriteLine($"[경고] 슬라이드 {Slides.Count}장과 대본 {Notes.Count}개 수가 다릅니다.");
        return TranscriptDeck.Finish(Slides, output, previewDir, "WSL Remote Setup 발표", mask is not null);
    }
}
