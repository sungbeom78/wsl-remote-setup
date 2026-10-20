using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Versioning;

namespace ManualBuilder;

/// <summary>
/// 대략적인 미리보기(PowerPoint 렌더링과 똑같지 않음). 글자가 상자를 넘치면 빨간 테두리로 표시하고 목록을 돌려준다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Preview
{
    private const float Dpi = 120f;

    public static List<string> Render(IReadOnlyList<Slide> slides, string dir)
    {
        Directory.CreateDirectory(dir);
        var problems = new List<string>();
        for (var i = 0; i < slides.Count; i++)
        {
            using var bmp = new Bitmap((int)(13.333 * Dpi), (int)(7.5 * Dpi));
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Hex(slides[i].Background));
            foreach (var s in slides[i].Shapes)
            {
                var r = new RectangleF((float)s.X * Dpi, (float)s.Y * Dpi, (float)s.W * Dpi, (float)s.H * Dpi);
                using var path = Shape(s.Geom, r);
                if (s.Fill is not null) using (var b = new SolidBrush(Hex(s.Fill))) g.FillPath(b, path);
                if (s.Line is not null) using (var p = new Pen(Hex(s.Line), 1.2f)) g.DrawPath(p, path);
                if (s.Text.Count == 0) continue;

                var inset = (float)s.Inset * Dpi;
                var inner = RectangleF.Inflate(r, -inset, -inset);
                var lines = new List<(string text, Font font, Color color, StringAlignment align, float h, float indent)>();
                float total = 0;
                foreach (var p in s.Text)
                {
                    var font = new Font(p.Mono ? "Consolas" : Fonts.BodyGdi, (float)p.Size, p.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                    var indent = p.Bullet ? 0.22f * Dpi : 0f;
                    var text = (p.Bullet ? "• " : "") + p.Text;
                    var size = g.MeasureString(text, font, new SizeF(Math.Max(1, inner.Width - indent), 10000), StringFormat.GenericTypographic);
                    var lineH = font.GetHeight(g) * 1.1f;
                    var nLines = Math.Max(1, (int)Math.Ceiling(size.Height / font.GetHeight(g) - 0.05));
                    var h = nLines * lineH + (float)p.After * Dpi / 72f;
                    lines.Add((text, font, Hex(p.Color), p.Align switch { "ctr" => StringAlignment.Center, "r" => StringAlignment.Far, _ => StringAlignment.Near }, h, indent));
                    total += h;
                }
                var y = s.Anchor == "ctr" ? inner.Y + (inner.Height - total) / 2 : inner.Y;
                foreach (var l in lines)
                {
                    using var brush = new SolidBrush(l.color);
                    using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = l.align };
                    g.DrawString(l.text, l.font, brush, new RectangleF(inner.X + l.indent, y, inner.Width - l.indent, l.h + 2), fmt);
                    y += l.h;
                    l.font.Dispose();
                }
                if (total > inner.Height + 2 || r.Right > 13.333f * Dpi + 1 || r.Bottom > 7.5f * Dpi + 1)
                {
                    using var red = new Pen(Color.Red, 3);
                    g.DrawRectangle(red, r.X, r.Y, r.Width, r.Height);
                    problems.Add($"슬라이드 {i + 1}: '{s.Text[0].Text}' 필요 {total / Dpi:0.00}in > 상자 {inner.Height / Dpi:0.00}in");
                }
            }
            foreach (var im in slides[i].Images)
            {
                using var pic = Image.FromFile(im.Path);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(pic, (float)im.X * Dpi, (float)im.Y * Dpi, (float)im.W * Dpi, (float)im.H * Dpi);
            }
            foreach (var t in slides[i].Terminals)
            {
                var problem = DrawTerminal(g, t);
                if (problem is not null) problems.Add($"슬라이드 {i + 1}: {problem}");
            }
            bmp.Save(Path.Combine(dir, $"slide-{i + 1:00}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        return problems;
    }

    /// <summary>터미널 그리기. 한글은 맑은 고딕, 나머지는 Consolas로 조각을 나눠 이어 그린다. 넘치면 문제를 돌려준다.</summary>
    public static string? DrawTerminal(Graphics g, TerminalInfo t, float dpi = Dpi)
    {
        var r = new RectangleF((float)t.X * dpi, (float)t.Y * dpi, (float)t.W * dpi, (float)t.H * dpi);
        using (var bg = new SolidBrush(Hex(t.Bg))) g.FillRectangle(bg, r);
        var inset = (float)t.Inset * dpi;
        var x0 = r.X + inset;
        var y = r.Y + inset;
        using var mono = new Font("Consolas", (float)t.Size, FontStyle.Regular, GraphicsUnit.Point);
        using var monoB = new Font("Consolas", (float)t.Size, FontStyle.Bold, GraphicsUnit.Point);
        using var kor = new Font("Malgun Gothic", (float)t.Size, FontStyle.Regular, GraphicsUnit.Point);
        using var korB = new Font("Malgun Gothic", (float)t.Size, FontStyle.Bold, GraphicsUnit.Point);
        var lineH = mono.GetHeight(g);
        var fmt = StringFormat.GenericTypographic;
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        var maxRight = x0;
        foreach (var line in t.Lines)
        {
            var x = x0;
            foreach (var run in line)
                foreach (var (text, hangul) in Segments(run.Text))
                {
                    var f = hangul ? (run.Bold ? korB : kor) : (run.Bold ? monoB : mono);
                    using var b = new SolidBrush(Hex(run.Color));
                    g.DrawString(text, f, b, x, y + (hangul ? -lineH * 0.08f : 0), fmt);
                    x += g.MeasureString(text, f, int.MaxValue, fmt).Width;
                }
            maxRight = Math.Max(maxRight, x);
            y += lineH;
        }
        if (y > r.Bottom - inset + 2) return $"터미널 줄이 상자를 넘침({t.Lines.Count}줄)";
        if (maxRight > r.Right - inset + 2) return "터미널 줄이 너무 김";
        return null;
    }

    private static IEnumerable<(string, bool)> Segments(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool? cur = null;
        foreach (var ch in s)
        {
            var h = (ch >= 0x1100 && ch <= 0x11FF) || (ch >= 0x3130 && ch <= 0x318F) || (ch >= 0xAC00 && ch <= 0xD7A3)
                    || ch is '…' or '·' or '※' or '✔' or '○' or '●' or '▲' or '✕' or '→' or '•' or '—';
            if (cur is not null && h != cur) { yield return (sb.ToString(), cur.Value); sb.Clear(); }
            cur = h;
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return (sb.ToString(), cur!.Value);
    }

    private static GraphicsPath Shape(string geom, RectangleF r)
    {
        var p = new GraphicsPath();
        switch (geom)
        {
            case "ellipse": p.AddEllipse(r); break;
            case "roundRect":
                var d = Math.Min(r.Width, r.Height) * 0.14f;
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure(); break;
            case "rightArrow":
                var head = Math.Min(r.Width * 0.5f, r.Height);
                p.AddPolygon(new PointF[]
                {
                    new(r.X, r.Y + r.Height * 0.25f), new(r.Right - head, r.Y + r.Height * 0.25f), new(r.Right - head, r.Y),
                    new(r.Right, r.Y + r.Height / 2), new(r.Right - head, r.Bottom), new(r.Right - head, r.Y + r.Height * 0.75f),
                    new(r.X, r.Y + r.Height * 0.75f),
                });
                break;
            default: p.AddRectangle(r); break;
        }
        return p;
    }

    private static Color Hex(string h) => Color.FromArgb(Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..], 16));
}
