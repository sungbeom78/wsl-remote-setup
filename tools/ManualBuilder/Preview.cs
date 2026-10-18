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
                    var font = new Font(p.Mono ? "Consolas" : "Malgun Gothic", (float)p.Size, p.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
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
            bmp.Save(Path.Combine(dir, $"slide-{i + 1:00}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        return problems;
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
