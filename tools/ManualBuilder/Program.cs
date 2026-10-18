using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ManualBuilder;

// 사용: dotnet run --project tools/ManualBuilder -- <출력 경로.pptx>
var output = args.Length > 0 ? args[0] : "WSL_Remote_Setup_설치매뉴얼.pptx";
var slides = new List<Slide>();
const double W = 13.333;

Slide New(string title, string? lead = null)
{
    var s = new Slide();
    s.Text(0.6, 0.42, 12.1, 0.75, new P(title, 30, Pal.Ink, Bold: true, After: 0));
    if (lead is not null) s.Text(0.6, 1.12, 12.1, 0.5, new P(lead, 15, Pal.Muted, After: 0));
    slides.Add(s);
    s.Text(0.6, 7.02, 6, 0.3, new P("WSL Remote Setup 설치 매뉴얼", 10, Pal.Muted, After: 0));
    s.Text(W - 1.6, 7.02, 1.0, 0.3, new P(slides.Count.ToString(), 10, Pal.Muted, Align: "r", After: 0));
    return s;
}

static void Num(Slide s, double x, double y, double d, string n, string fill = Pal.Teal) =>
    s.Box(x, y, d, d, [new P(n, d * 26, Pal.White, Bold: true, Align: "ctr", After: 0)], fill, geom: "ellipse", anchor: "ctr", inset: 0, name: "Number");

static void Card(Slide s, double x, double y, double w, double h, string fill = Pal.Card, string? line = null) =>
    s.Box(x, y, w, h, null, fill, line, "roundRect", shadow: fill == Pal.Card, name: "Card");

// 1. 표지 ─────────────────────────────────────────────
{
    var s = new Slide { Background = Pal.Navy };
    slides.Add(s);
    s.Text(0.8, 1.45, 8, 0.4, new P("설치 매뉴얼 · 버전 0.1", 14, "7FB7B9", Bold: true, After: 0));
    s.Text(0.8, 1.9, 8.6, 1.0, new P("WSL Remote Setup", 46, Pal.White, Bold: true, After: 0));
    s.Text(0.8, 3.0, 8.4, 1.2,
        new P("집이나 사무실의 PC를 Linux 작업 서버로 만들고,", 20, "D9E2EC", After: 2),
        new P("노트북에서 SSH와 원격 데스크톱으로 접속합니다.", 20, "D9E2EC", After: 0));
    s.Text(0.8, 5.3, 8.4, 0.9,
        new P("순서: 준비 → 설치 → 접속 확인 → 평소 관리 → 되돌리기", 14, "AFC2D6", After: 4),
        new P("문의·의견: https://www.edubeom.com/", 12, "AFC2D6", After: 0));
    string[] tags = ["WSL", "SSH", "RDP"];
    string[] sub = ["Ubuntu", "Linux 접속", "큰 화면"];
    for (var i = 0; i < 3; i++)
    {
        var y = 1.5 + i * 1.55;
        s.Box(10.0, y, 1.25, 1.25, [new P(tags[i], 20, Pal.White, Bold: true, Align: "ctr", After: 0)], Pal.Teal, geom: "ellipse", anchor: "ctr", inset: 0, name: "Tag");
        s.Text(11.45, y + 0.42, 1.6, 0.45, new P(sub[i], 14, "D9E2EC", After: 0));
    }
}

// 2. 하는 일 ──────────────────────────────────────────
{
    var s = New("이 프로그램이 해 주는 일", "노트북 → 이 PC → Linux까지 가는 길을 만들고, 끊기면 스스로 다시 잇습니다");
    (string t, string a, string b)[] nodes =
    [
        ("노트북", "소파·침대에서 큰 화면으로", "SSH · 원격 데스크톱"),
        ("이 PC (Windows)", "1004번 문으로 받은 접속을", "Linux로 전달합니다"),
        ("WSL 안의 Ubuntu", "Linux 작업 환경", "SSH 서버가 받습니다"),
    ];
    double[] xs = [0.6, 4.95, 9.3];
    for (var i = 0; i < 3; i++)
    {
        Card(s, xs[i], 1.85, 3.43, 1.85);
        s.Text(xs[i] + 0.3, 2.05, 2.9, 0.5, new P(nodes[i].t, 19, i == 1 ? Pal.Teal : Pal.Ink, Bold: true, After: 0));
        s.Text(xs[i] + 0.3, 2.65, 2.9, 0.9, new P(nodes[i].a, 14, Pal.Muted, After: 2), new P(nodes[i].b, 14, Pal.Muted, After: 0));
    }
    string[] arrowLabel = ["같은 공유기", "자동 전달"];
    for (var i = 0; i < 2; i++)
    {
        var ax = xs[i] + 3.55;
        s.Text(ax - 0.25, 2.2, 1.25, 0.35, new P(arrowLabel[i], 11, Pal.Teal, Bold: true, Align: "ctr", After: 0));
        s.Box(ax, 2.6, 0.75, 0.5, null, Pal.Teal, geom: "rightArrow", name: "Arrow");
    }
    (string t, string d)[] feats =
    [
        ("끊기면 스스로 복구", "WSL이 꺼지거나 주소가 바뀌어도 1분 안에 다시 연결합니다. 재부팅 뒤에도 자동입니다."),
        ("로그인 전 접속 (선택)", "PC를 켜기만 하면 로그인하지 않아도 약 1분 뒤부터 접속할 수 있습니다."),
        ("집 안에서만 열림", "같은 공유기에 연결된 기기만 허용합니다. 인터넷 전체에는 열지 않습니다."),
    ];
    double[] fx = [0.6, 4.75, 8.9];
    for (var i = 0; i < 3; i++)
    {
        s.Box(fx[i], 4.3, 3.85, 1.85, null, Pal.TealSoft, geom: "roundRect", name: "Feature");
        s.Text(fx[i] + 0.3, 4.5, 3.3, 0.45, new P(feats[i].t, 17, Pal.Teal, Bold: true, After: 0));
        s.Text(fx[i] + 0.3, 5.05, 3.3, 1.4, new P(feats[i].d, 14, Pal.Ink, After: 0));
    }
}

// 3. 준비물 ───────────────────────────────────────────
{
    var s = New("시작하기 전에 준비할 것", "아래 6가지가 갖춰지면 30분 안팎으로 끝납니다");
    (string t, string d)[] items =
    [
        ("Windows 10/11 Pro", "Home은 원격 데스크톱 없이 SSH만 됩니다."),
        ("관리자 계정으로 로그인", "중간에 '관리자 권한' 확인 창(UAC)이 뜹니다."),
        ("인터넷 연결", "WSL과 Ubuntu를 내려받습니다(수백 MB)."),
        ("같은 공유기", "노트북과 이 PC가 같은 공유기(Wi-Fi)에 있어야 합니다."),
        ("30분 정도", "처음 설치라면 재부팅이 한 번 필요할 수 있습니다."),
        ("WslRemoteSetup.exe", "파일 하나면 됩니다. 다른 프로그램은 필요 없습니다."),
    ];
    for (var i = 0; i < items.Length; i++)
    {
        var y = 1.85 + i * 0.8;
        Card(s, 0.6, y, 7.3, 0.68);
        Num(s, 0.75, y + 0.12, 0.44, (i + 1).ToString());
        s.Text(1.4, y + 0.08, 2.6, 0.52, new P(items[i].t, 15, Pal.Ink, Bold: true, After: 0));
        s.Text(4.0, y + 0.1, 3.8, 0.52, new P(items[i].d, 12.5, Pal.Muted, After: 0));
    }
    s.Box(8.3, 1.85, 4.43, 4.67, null, Pal.TealSoft, geom: "roundRect", name: "Panel");
    s.Text(8.6, 2.05, 3.9, 0.45, new P("이 PC 주소를 먼저 확인하세요", 17, Pal.Teal, Bold: true, After: 0));
    s.Text(8.6, 2.6, 3.9, 0.9,
        new P("명령 프롬프트(cmd)에서 아래를 입력하고 'IPv4 주소'를 적어 둡니다.", 13, Pal.Ink, After: 0));
    s.Box(8.6, 3.45, 3.85, 0.5, [new P("ipconfig", 15, Pal.Ink, Mono: true, After: 0)], Pal.White, anchor: "ctr", inset: 0.15, name: "Code");
    s.Text(8.6, 4.1, 3.9, 1.0,
        new P("주소가 자꾸 바뀌면(자동 할당) 공유기에서 이 PC 주소를 예약(고정)해 두면 안전합니다.", 13, Pal.Ink, After: 0));
    s.Box(8.6, 5.15, 3.85, 1.15,
        [new P("주의", 12, Pal.Amber, Bold: true, After: 2), new P("접속할 때 주소 끝자리 하나만 달라도 연결되지 않습니다.", 12.5, Pal.Ink, After: 0)],
        Pal.AmberSoft, geom: "roundRect", inset: 0.15, name: "Warning");
}

// 4. 실행 ─────────────────────────────────────────────
{
    var s = New("실행하기", "더블클릭하면 메뉴가 열립니다. 번호를 입력하고 Enter를 누르세요");
    (string t, string d)[] steps =
    [
        ("WslRemoteSetup.exe 더블클릭", "일반 창으로 열립니다. 관리자 권한으로 따로 실행하지 않아도 됩니다."),
        ("메뉴 번호 입력 → Enter", "처음 설치는 1번, 상태 확인은 2번, 되돌리기는 6번입니다."),
        ("확인 질문에 답하기", "[Y/n]은 그냥 Enter = 예, n = 아니오(건너뛰기)입니다."),
        ("관리자 창이 열리면", "UAC 창에서 '예' → 새 창에서 Y → 끝나면 Enter로 닫습니다."),
    ];
    for (var i = 0; i < steps.Length; i++)
    {
        var y = 1.85 + i * 1.12;
        Num(s, 0.6, y + 0.05, 0.55, (i + 1).ToString());
        s.Text(1.35, y, 5.6, 0.45, new P(steps[i].t, 17, Pal.Ink, Bold: true, After: 0));
        s.Text(1.35, y + 0.45, 5.6, 0.6, new P(steps[i].d, 13, Pal.Muted, After: 0));
    }
    s.Box(7.35, 1.85, 5.38, 3.35,
    [
        new P("WSL Remote Setup 0.1.0", 13, "7FB7B9", Mono: true, After: 6),
        new P("1. 새로 설치 / 이어서 설치", 13, Pal.White, Mono: true, After: 2),
        new P("2. 현재 상태 확인", 13, Pal.White, Mono: true, After: 2),
        new P("3. 연결 복구", 13, Pal.White, Mono: true, After: 2),
        new P("4. 설정 변경", 13, Pal.White, Mono: true, After: 2),
        new P("5. 자동 연결 관리 멈춤 / 다시 켜기", 13, Pal.White, Mono: true, After: 2),
        new P("6. 되돌리기", 13, Pal.White, Mono: true, After: 2),
        new P("0. 종료", 13, Pal.White, Mono: true, After: 6),
        new P("선택: _", 13, "7FB7B9", Mono: true, After: 0),
    ], Pal.Navy, geom: "roundRect", inset: 0.3, shadow: true, name: "Console");
    s.Box(7.35, 5.45, 5.38, 0.8,
        [new P("처음 실행 때 'Windows의 PC 보호' 창이 뜨면, 직접 받은 파일이 맞는지 확인한 뒤 '추가 정보 → 실행'을 누릅니다.", 11.5, Pal.Ink, After: 0)],
        Pal.AmberSoft, geom: "roundRect", anchor: "ctr", inset: 0.15, name: "Note");
    s.Text(0.6, 6.4, 6.4, 0.45, new P("중간에 멈춰도 괜찮습니다. 다시 실행해서 1번을 고르면 이어서 진행합니다.", 13, Pal.Teal, Bold: true, After: 0));
}

// 5. 설치 질문 ────────────────────────────────────────
{
    var s = New("설치 질문 6가지", "대부분 Enter(기본값)로 충분합니다. IP 주소와 원격 데스크톱 포트는 묻지 않고 지금 설정을 씁니다");
    string[] head = ["", "질문", "기본값 (Enter)", "이렇게 고르세요"];
    string[][] rows =
    [
        ["1", "다른 기기에서 접속할 포트", "1004", "다른 프로그램이 쓰고 있으면 빈 번호를 추천해 줍니다"],
        ["2", "WSL을 쓸 Windows 계정", "지금 로그인한 계정", "평소 이 PC에 로그인하는 계정"],
        ["3", "Ubuntu 사용자 이름", "linux_admin", "영문 소문자로 시작하는 이름"],
        ["4", "SSH 로그인 방식", "1) 비밀번호 또는 키", "mRemoteNG 같은 도구는 1번이 편합니다"],
        ["5", "SSH 말고 더 열 포트", "아니오", "웹 서버 등을 열 때만 '예'"],
        ["6", "로그인 전에도 접속", "아니오", "서버처럼 켜 두는 PC라면 '예'"],
    ];
    double[] cx = [0.6, 1.15, 4.75, 7.45];
    double[] cw = [0.55, 3.6, 2.7, 5.28];
    s.Box(0.6, 1.85, 12.13, 0.55, null, Pal.Navy, geom: "rect", name: "Header");
    for (var c = 0; c < 4; c++) s.Text(cx[c] + 0.15, 1.95, cw[c] - 0.2, 0.4, new P(head[c], 13, Pal.White, Bold: true, After: 0));
    for (var r = 0; r < rows.Length; r++)
    {
        var y = 2.4 + r * 0.7;
        s.Box(0.6, y, 12.13, 0.7, null, r % 2 == 0 ? Pal.Card : "F8FAFB", Pal.Line, name: "Row", lineW: 0.75);
        s.Text(cx[0] + 0.15, y + 0.17, cw[0] - 0.2, 0.4, new P(rows[r][0], 14, Pal.Teal, Bold: true, After: 0));
        s.Text(cx[1] + 0.15, y + 0.17, cw[1] - 0.2, 0.4, new P(rows[r][1], 14, Pal.Ink, Bold: true, After: 0));
        s.Text(cx[2] + 0.15, y + 0.17, cw[2] - 0.2, 0.4, new P(rows[r][2], 14, Pal.Teal, After: 0));
        s.Text(cx[3] + 0.15, y + 0.18, cw[3] - 0.25, 0.45, new P(rows[r][3], 12.5, Pal.Muted, After: 0));
    }
    s.Text(0.6, 6.7, 12.1, 0.3, new P("고른 값은 저장되어, 다음에 다시 실행하면 '이 값으로 계속할까요?'라고 묻습니다.", 12, Pal.Muted, After: 0));
}

// 6~7. 설치 8단계 ─────────────────────────────────────
(string t, string d, string todo)[] install =
[
    ("설치 질문", "6가지 질문에 답합니다. 대부분 Enter면 됩니다.", "Enter 또는 값 입력"),
    ("WSL 설치", "Windows에서 Linux를 돌리는 기능을 설치합니다. 처음이면 재부팅이 필요할 수 있습니다.", "UAC '예' → 재부팅 뒤 같은 계정으로 로그인해 다시 실행"),
    ("계정 확인", "WSL은 Windows 계정마다 따로 설치됩니다.", "질문 2의 계정으로 로그인했는지 확인"),
    ("Ubuntu 설치", "인터넷에서 Ubuntu를 내려받아 이 계정에 설치합니다.", "Y (몇 분 기다리기)"),
    ("Linux 사용자", "Ubuntu 사용자를 만들고 비밀번호를 정합니다. Windows 비밀번호와 따로입니다.", "비밀번호 두 번 입력 (글자가 화면에 안 보임)"),
    ("SSH 서버", "Ubuntu 안에 SSH 서버를 설치합니다. root 로그인은 막습니다.", "공개키 질문은 n(나중에 가능) → Y"),
    ("연결 통로 열기", "이 PC의 1004번으로 온 접속을 Ubuntu로 넘깁니다. 같은 공유기만 허용합니다.", "UAC '예' → 관리자 창에서 Y"),
    ("자동 실행", "백그라운드 관리 프로그램과 자동 실행을 등록합니다(로그인 전 접속 포함).", "UAC '예' → Y → Enter로 창 닫기"),
];
for (var part = 0; part < 2; part++)
{
    var s = New(part == 0 ? "설치 8단계 (1~4)" : "설치 8단계 (5~8)",
        part == 0 ? "단계마다 무엇을 바꾸는지 먼저 보여 주고, 확인을 받은 뒤 진행합니다" : "관리자 권한이 필요한 단계는 새 창이 열립니다. 창의 질문에 답하고 Enter로 닫으세요");
    for (var k = 0; k < 4; k++)
    {
        var i = part * 4 + k;
        var x = 0.6 + k * 3.12;
        Card(s, x, 1.85, 2.85, 4.3);
        Num(s, x + 0.25, 2.1, 0.7, (i + 1).ToString(), i == 1 || i >= 6 ? Pal.Navy : Pal.Teal);
        s.Text(x + 0.25, 3.0, 2.4, 0.5, new P(install[i].t, 18, Pal.Ink, Bold: true, After: 0));
        s.Text(x + 0.25, 3.55, 2.4, 1.2, new P(install[i].d, 13, Pal.Muted, After: 0));
        s.Box(x + 0.15, 4.8, 2.55, 1.2,
            [new P("내가 할 일", 11.5, Pal.Teal, Bold: true, After: 3), new P(install[i].todo, 12.5, Pal.Ink, After: 0)],
            Pal.TealSoft, geom: "roundRect", inset: 0.13, name: "Todo");
    }
    if (part == 1)
        s.Text(0.6, 6.35, 12.1, 0.3, new P("남색 번호 = 관리자 창(UAC)이 열리는 단계", 10.5, Pal.Muted, After: 0));
    else
        s.Text(0.6, 6.35, 12.1, 0.3, new P("남색 번호 = 관리자 창(UAC)이 열리는 단계 · WSL이 이미 있으면 2단계는 건너뜁니다", 10.5, Pal.Muted, After: 0));
}

// 8. 접속 확인 ────────────────────────────────────────
{
    var s = New("설치가 끝나면: 접속 확인", "설치 마지막에 상태 화면이 나옵니다. 모두 '정상'이면 노트북에서 접속해 보세요");
    s.Box(0.6, 1.85, 6.0, 3.75, null, Pal.Card, Pal.Line, "roundRect", shadow: true, name: "Status");
    s.Text(0.9, 2.05, 5.4, 0.35, new P("현재 상태", 14, Pal.Ink, Bold: true, After: 0));
    s.Text(0.9, 2.45, 5.4, 0.4, new P("● 모두 정상입니다. 다른 PC에서 SSH로 접속할 수 있습니다.", 12.5, Pal.Teal, Bold: true, After: 0));
    (string k, string v)[] status =
    [
        ("SSH 접속", "이 PC 주소, 1004번"), ("WSL", "켜져 있음"), ("자동 연결 관리", "1분마다 점검"),
        ("방화벽", "같은 공유기만 허용"), ("로그인 전 접속", "켜짐"), ("절전", "잠들지 않음"),
    ];
    for (var i = 0; i < status.Length; i++)
    {
        var y = 3.0 + i * 0.4;
        s.Text(0.9, y, 0.8, 0.35, new P("● 정상", 12.5, Pal.Teal, After: 0));
        s.Text(1.85, y, 1.9, 0.35, new P(status[i].k, 12.5, Pal.Ink, Bold: true, After: 0));
        s.Text(3.75, y, 2.7, 0.35, new P(status[i].v, 12.5, Pal.Muted, After: 0));
    }
    s.Text(0.6, 5.8, 6.0, 0.5, new P("언제든 메뉴 2번(또는 status)으로 다시 볼 수 있습니다.", 13, Pal.Muted, After: 0));

    s.Text(7.0, 1.85, 5.7, 0.4, new P("노트북에서 접속하는 방법", 17, Pal.Ink, Bold: true, After: 0));
    s.Text(7.0, 2.35, 5.7, 0.35, new P("터미널(PowerShell)", 13, Pal.Teal, Bold: true, After: 0));
    s.Box(7.0, 2.72, 5.73, 0.5, [new P("ssh -p 1004 linux_admin@이PC주소", 13.5, Pal.Ink, Mono: true, After: 0)], Pal.Code, anchor: "ctr", inset: 0.15, name: "Code");
    s.Text(7.0, 3.4, 5.7, 0.35, new P("mRemoteNG · PuTTY", 13, Pal.Teal, Bold: true, After: 0));
    s.Text(7.0, 3.75, 5.7, 0.7, new P("호스트 = 이 PC 주소, 포트 = 1004, 프로토콜 = SSH 버전 2, 사용자 = linux_admin", 13, Pal.Ink, After: 0));
    s.Text(7.0, 4.5, 5.7, 0.35, new P("원격 데스크톱(큰 화면)", 13, Pal.Teal, Bold: true, After: 0));
    s.Text(7.0, 4.85, 5.7, 0.4, new P("'이 PC 주소:원격 데스크톱 포트'로 접속합니다(기존 설정 그대로).", 13, Pal.Ink, After: 0));
    s.Box(7.0, 5.45, 5.73, 1.0,
        [new P("처음 접속할 때", 12, Pal.Amber, Bold: true, After: 2),
         new P("'서버를 신뢰할까요?'가 나오면 수락합니다. 접속이 안 되면 주소 끝자리와 포트부터 다시 확인하세요.", 12.5, Pal.Ink, After: 0)],
        Pal.AmberSoft, geom: "roundRect", inset: 0.15, name: "Warning");
}

// 9. 노트북 접속 도우미 ───────────────────────────────
{
    var s = New("(선택) 노트북 접속 도우미", "노트북 쪽 SSH 설정을 파일 하나로 자동으로 준비합니다. 관리자 권한이 필요 없습니다");
    (string where, string what, string cmd)[] flow =
    [
        ("이 PC(서버)에서", "연결 정보 파일을 바탕화면에 만듭니다.", "WslRemoteSetup.exe export-client"),
        ("파일 옮기기", "만든 '이름.wrs.json'과 EXE를 노트북으로 복사합니다(USB·공유 폴더).", ""),
        ("노트북에서", "파일을 지정해 실행하고, 질문마다 Enter를 누릅니다.", "WslRemoteSetup.exe client 이름.wrs.json"),
    ];
    for (var i = 0; i < 3; i++)
    {
        var x = 0.6 + i * 4.15;
        Card(s, x, 1.85, 3.85, 2.55);
        Num(s, x + 0.25, 2.07, 0.55, (i + 1).ToString());
        s.Text(x + 0.95, 2.12, 2.8, 0.45, new P(flow[i].where, 16, Pal.Ink, Bold: true, After: 0));
        s.Text(x + 0.25, 2.8, 3.35, 0.85, new P(flow[i].what, 13, Pal.Muted, After: 0));
        if (flow[i].cmd.Length > 0)
            s.Box(x + 0.2, 3.7, 3.45, 0.5, [new P(flow[i].cmd, 10.5, Pal.Ink, Mono: true, After: 0)], Pal.Code, anchor: "ctr", inset: 0.1, name: "Code");
        if (i < 2) s.Box(x + 3.9, 2.9, 0.2, 0.4, null, Pal.Teal, geom: "rightArrow", name: "Arrow");
    }
    s.Text(0.6, 4.65, 7.2, 0.4, new P("노트북에서 자동으로 해 주는 일", 16, Pal.Ink, Bold: true, After: 0));
    s.Text(0.6, 5.1, 7.2, 1.8,
        new P("열쇠 파일(SSH 키)이 없으면 만들기", 13, Pal.Ink, Bullet: true, After: 3),
        new P("진짜 서버인지 확인하는 정보 등록", 13, Pal.Ink, Bullet: true, After: 3),
        new P("'ssh 이름' 한 줄로 접속되게 등록 (기존 설정은 백업)", 13, Pal.Ink, Bullet: true, After: 3),
        new P("서버에 키 등록 (Linux 비밀번호 한 번 입력) · 바탕화면에 원격 데스크톱 아이콘", 13, Pal.Ink, Bullet: true, After: 0));
    s.Box(8.2, 4.65, 4.53, 1.5,
        [new P("안심하세요", 13, Pal.Teal, Bold: true, After: 4),
         new P("연결 정보 파일에는 비밀번호와 개인키가 들어가지 않습니다.", 12.5, Pal.Ink, Bullet: true, After: 3),
         new P("ARM 노트북은 ARM64용 EXE를 쓰세요.", 12.5, Pal.Ink, Bullet: true, After: 0)],
        Pal.TealSoft, geom: "roundRect", inset: 0.2, name: "Panel");
}

// 10. 평소 관리 ───────────────────────────────────────
{
    var s = New("평소 관리", "메뉴로 할 수 있고, EXE 폴더에서 PowerShell을 열어 명령으로도 할 수 있습니다");
    (string t, string how, string d)[] tiles =
    [
        ("상태 보기", "메뉴 2  ·  status", "접속·자동 실행·절전 상태를 한눈에"),
        ("연결 복구", "메뉴 3", "설치 단계를 다시 확인하고 빠진 것만 고칩니다"),
        ("잠시 멈춤 / 다시 켜기", "메뉴 5  ·  pause / resume-monitoring", "멈추면 WSL을 다시 켜지 않습니다"),
        ("잠들지 않게", "power keep-awake", "전원 연결 시 절전 안 함 (되돌리기: power restore)"),
        ("로그인 전 접속", "autostart prelogin-on / prelogin-off", "관리자 창에서 켜고 끕니다"),
        ("작업 기록", "history.log", "%LOCALAPPDATA%\\WslRemoteSetup\\logs 폴더"),
    ];
    for (var i = 0; i < tiles.Length; i++)
    {
        var x = 0.6 + (i % 3) * 4.1;
        var y = 1.85 + (i / 3) * 2.45;
        Card(s, x, y, 3.85, 2.2);
        s.Text(x + 0.25, y + 0.2, 3.4, 0.45, new P(tiles[i].t, 17, Pal.Teal, Bold: true, After: 0));
        s.Box(x + 0.2, y + 0.75, 3.45, 0.5, [new P(tiles[i].how, 11, Pal.Ink, Mono: true, After: 0)], Pal.Code, anchor: "ctr", inset: 0.1, name: "Code");
        s.Text(x + 0.25, y + 1.38, 3.4, 0.75, new P(tiles[i].d, 12.5, Pal.Muted, Mono: tiles[i].d.Contains('\\'), After: 0));
    }
    s.Text(0.6, 6.75, 12.1, 0.3, new P("명령 예: EXE가 있는 폴더에서  .\\WslRemoteSetup.exe status", 12, Pal.Muted, Mono: true, After: 0));
}

// 11. 되돌리기 ────────────────────────────────────────
{
    var s = New("되돌리기 5단계", "메뉴 6(또는 uninstall)을 일반 창에서 실행합니다. 단계마다 영향을 알려 주고 확인을 받습니다");
    (string t, string ans, string d)[] steps =
    [
        ("외부 연결 닫기", "권장 Y", "다른 기기에서 SSH로 들어올 수 없게 됩니다. 원격 데스크톱은 그대로입니다."),
        ("자동 실행 중지", "Y", "백그라운드 프로그램·자동 실행·로그인 전 접속을 지웁니다."),
        ("Ubuntu 안 SSH 설정 되돌리기", "Y", "이 도구가 만든 설정과 등록한 키만 지웁니다."),
        ("(선택) Ubuntu 삭제", "기본 N", "지우기 전에 백업할지 묻습니다(다음 장)."),
        ("설치 이력 안내", "", "남겨 둔 프로그램과 직접 지우는 방법을 알려 줍니다."),
    ];
    for (var i = 0; i < steps.Length; i++)
    {
        var y = 1.85 + i * 0.96;
        Card(s, 0.6, y, 7.6, 0.82, i == 3 ? Pal.AmberSoft : Pal.Card);
        Num(s, 0.78, y + 0.16, 0.5, (i + 1).ToString(), i == 3 ? Pal.Amber : Pal.Teal);
        s.Text(1.5, y + 0.08, 3.6, 0.38, new P(steps[i].t, 15, Pal.Ink, Bold: true, After: 0));
        if (steps[i].ans.Length > 0)
            s.Box(6.95, y + 0.17, 1.05, 0.42, [new P(steps[i].ans, 11.5, i == 3 ? Pal.Amber : Pal.Teal, Bold: true, Align: "ctr", After: 0)], Pal.White, i == 3 ? Pal.Amber : Pal.Teal, "roundRect", "ctr", 0.03, name: "Answer");
        s.Text(1.5, y + 0.45, 5.4, 0.35, new P(steps[i].d, 11.5, Pal.Muted, After: 0));
    }
    s.Box(8.6, 1.85, 4.13, 4.67,
    [
        new P("되돌리기 원칙", 16, Pal.Teal, Bold: true, After: 8),
        new P("원하지 않는 단계는 n으로 건너뜁니다.", 13, Pal.Ink, Bullet: true, After: 5),
        new P("이 도구가 만든 것만 지웁니다.", 13, Pal.Ink, Bullet: true, After: 5),
        new P("WSL 프로그램, SSH 프로그램, Windows 계정·원격 데스크톱은 지우지 않습니다.", 13, Pal.Ink, Bullet: true, After: 5),
        new P("절전 설정을 바꿨다면 되돌릴지 따로 묻습니다(기본 N).", 13, Pal.Ink, Bullet: true, After: 5),
        new P("관리자 단계는 UAC 창과 새 창에서 한 번 더 확인합니다.", 13, Pal.Ink, Bullet: true, After: 0),
    ], Pal.TealSoft, geom: "roundRect", inset: 0.25, name: "Panel");
}

// 12. 백업 ────────────────────────────────────────────
{
    var s = New("Ubuntu 삭제 전 백업과 되살리기", "백업 파일 하나로 통째로 저장해 두면, 나중에 사용자·설정까지 그대로 되살릴 수 있습니다");
    string[] flow =
    [
        "'삭제할까요?' → y",
        "'삭제하기 전에 백업할까요?' → Enter(예)",
        "저장 위치 입력 → Enter = 문서 폴더의 'WSL 백업'",
        "남은 공간 확인 → WSL을 끄고 백업",
        "배포판 이름을 그대로 입력 → 삭제",
    ];
    for (var i = 0; i < flow.Length; i++)
    {
        var y = 1.85 + i * 0.92;
        Num(s, 0.6, y + 0.1, 0.5, (i + 1).ToString());
        Card(s, 1.3, y, 5.6, 0.72);
        s.Text(1.5, y + 0.17, 5.3, 0.4, new P(flow[i], 14, Pal.Ink, After: 0));
    }
    s.Text(0.6, 6.5, 6.3, 0.4, new P("백업이 확인되지 않으면 아무것도 지우지 않습니다.", 13, Pal.Teal, Bold: true, After: 0));

    s.Text(7.4, 1.85, 5.3, 0.4, new P("되살리는 방법", 17, Pal.Ink, Bold: true, After: 0));
    s.Text(7.4, 2.32, 5.3, 0.4, new P("일반 PowerShell 창에서 실행합니다(삭제 후 화면에도 나옵니다).", 13, Pal.Muted, After: 0));
    s.Box(7.4, 2.85, 5.33, 1.2,
    [
        new P("wsl --import Ubuntu-24.04", 12.5, Pal.Ink, Mono: true, After: 2),
        new P("  \"%LOCALAPPDATA%\\WSL\\Ubuntu-24.04\"", 12.5, Pal.Ink, Mono: true, After: 2),
        new P("  \"백업파일.tar\"", 12.5, Pal.Ink, Mono: true, After: 0),
    ], Pal.Code, inset: 0.2, name: "Code");
    s.Box(7.4, 4.3, 5.33, 1.85,
    [
        new P("알아 두세요", 13, Pal.Amber, Bold: true, After: 4),
        new P("백업 크기는 Ubuntu 안에서 쓰는 용량만큼입니다(수 GB).", 12.5, Pal.Ink, Bullet: true, After: 3),
        new P("같은 이름의 파일은 덮어쓰지 않습니다.", 12.5, Pal.Ink, Bullet: true, After: 3),
        new P("백업 중에는 WSL이 꺼져 SSH 접속이 끊깁니다.", 12.5, Pal.Ink, Bullet: true, After: 0),
    ], Pal.AmberSoft, geom: "roundRect", inset: 0.2, name: "Warning");
}

// 13. G2 실습 ─────────────────────────────────────────
{
    var s = New("실습: 지우고 다시 설치해 보기", "권장 답을 따라가면 이 PC의 IP·원격 데스크톱은 그대로 둔 채 연습할 수 있습니다");
    Card(s, 0.6, 1.85, 5.95, 2.85);
    s.Text(0.85, 2.0, 5.5, 0.45, new P("① 되돌리기 (메뉴 6)", 17, Pal.Teal, Bold: true, After: 0));
    s.Text(0.85, 2.55, 5.5, 2.7,
        new P("1 외부 연결 닫기 → Y (UAC 예 → Y)", 13, Pal.Ink, Bullet: true, After: 5),
        new P("2 자동 실행 중지 → Y (UAC 예 → Y)", 13, Pal.Ink, Bullet: true, After: 5),
        new P("3 SSH 설정 되돌리기 → Y", 13, Pal.Ink, Bullet: true, After: 5),
        new P("4 Ubuntu 삭제 → 처음엔 N (지운다면 백업 Y)", 13, Pal.Ink, Bullet: true, After: 5),
        new P("절전 설정 되돌리기 → N (서버로 계속 쓸 때)", 13, Pal.Ink, Bullet: true, After: 0));
    Card(s, 6.78, 1.85, 5.95, 2.85);
    s.Text(7.03, 2.0, 5.5, 0.45, new P("② 다시 설치 (메뉴 1)", 17, Pal.Teal, Bold: true, After: 0));
    s.Text(7.03, 2.55, 5.5, 2.7,
        new P("포트 1004 · 지금 계정 · linux_admin", 13, Pal.Ink, Bullet: true, After: 5),
        new P("로그인 방식 1) · 추가 포트 N · 로그인 전 접속 Y", 13, Pal.Ink, Bullet: true, After: 5),
        new P("Ubuntu를 지웠다면 Linux 비밀번호를 새로 정합니다.", 13, Pal.Ink, Bullet: true, After: 5),
        new P("관리자 창 2번(연결 통로, 자동 실행): UAC 예 → Y", 13, Pal.Ink, Bullet: true, After: 0));
    s.Box(0.6, 4.95, 5.95, 1.6,
    [
        new P("③ 확인", 14, Pal.Teal, Bold: true, After: 4),
        new P("메뉴 2 상태가 모두 '정상'인지 → 노트북 mRemoteNG로 접속", 12.5, Pal.Ink, Bullet: true, After: 0),
    ], Pal.TealSoft, geom: "roundRect", inset: 0.18, name: "Check");
    s.Box(6.78, 4.95, 5.95, 1.6,
    [
        new P("주의", 12, Pal.Amber, Bold: true, After: 2),
        new P("원격 데스크톱은 끊기지 않고 SSH만 잠깐 끊깁니다.", 12, Pal.Ink, Bullet: true, After: 2),
        new P("Ubuntu를 새로 깔면 서버 키가 바뀌어 '키가 바뀌었다' 경고가 납니다 → 새 키 수락.", 12, Pal.Ink, Bullet: true, After: 2),
        new P("3단계에서 노트북 키가 지워집니다 → 키 로그인은 다시 등록(비밀번호 로그인은 그대로).", 12, Pal.Ink, Bullet: true, After: 0),
    ], Pal.AmberSoft, geom: "roundRect", inset: 0.15, name: "Warning");
}

// 14. 문제 해결 ───────────────────────────────────────
{
    var s = New("문제가 생기면", "대부분 아래에서 해결됩니다. 그래도 안 되면 작업 기록과 함께 문의해 주세요");
    (string q, string a)[] faq =
    [
        ("노트북에서 접속이 안 돼요", "주소 끝자리·포트(1004)를 다시 확인하고, 같은 공유기인지 봅니다. 메뉴 2로 상태를 확인하세요."),
        ("재부팅 직후 안 돼요", "1~2분 기다립니다. 로그인 전 접속을 껐다면 이 PC에 로그인해야 합니다."),
        ("관리자 창이 안 뜨거나 사라졌어요", "UAC 창이 시간이 지나 닫힌 것입니다. 다시 실행하면 이어서 진행합니다."),
        ("'서버 키가 바뀌었다'는 경고", "Ubuntu를 다시 설치한 경우에만 수락합니다. 아니라면 접속을 멈추고 문의하세요."),
        ("한참 안 쓰면 끊겨요", "PC가 잠든 것입니다. power keep-awake로 전원 연결 시 절전을 끄세요."),
    ];
    for (var i = 0; i < faq.Length; i++)
    {
        var y = 1.85 + i * 0.86;
        Card(s, 0.6, y, 12.13, 0.74);
        s.Text(0.85, y + 0.2, 3.9, 0.38, new P(faq[i].q, 14, Pal.Teal, Bold: true, After: 0));
        s.Text(4.85, y + 0.2, 7.75, 0.38, new P(faq[i].a, 12.5, Pal.Ink, After: 0));
    }
    s.Box(0.6, 6.25, 12.13, 0.62,
        [new P("작업 기록: %LOCALAPPDATA%\\WslRemoteSetup\\logs\\history.log  ·  문의·의견: https://www.edubeom.com/", 12, Pal.White, Mono: true, After: 0)],
        Pal.Navy, geom: "roundRect", anchor: "ctr", inset: 0.2, name: "Contact");
}

Deck.Write(output, slides, "WSL Remote Setup 설치 매뉴얼");

// 스키마 검증(PowerPoint가 열 수 있는 구조인지)
using (var doc = PresentationDocument.Open(output, false))
{
    var errors = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(doc).ToList();
    foreach (var e in errors.Take(20)) Console.WriteLine($"[검증] {e.Part?.Uri} {e.Path?.XPath}: {e.Description}");
    Console.WriteLine($"슬라이드 {slides.Count}장, 검증 오류 {errors.Count}건 → {Path.GetFullPath(output)}");
    if (errors.Count > 0) Environment.ExitCode = 1;
}

// 미리보기(선택): 두 번째 인수로 폴더를 주면 PNG를 만들고 글자 넘침을 알려 준다.
if (args.Length > 1 && OperatingSystem.IsWindows())
{
    var problems = Preview.Render(slides, args[1]);
    foreach (var p in problems) Console.WriteLine($"[넘침] {p}");
    Console.WriteLine($"미리보기 {slides.Count}장 → {Path.GetFullPath(args[1])}, 넘침 {problems.Count}건");
}
