using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ManualBuilder;

/// <summary>문단 하나. 크기는 pt, 색은 6자리 hex.</summary>
public sealed record P(string Text, double Size = 16, string Color = Pal.Ink, bool Bold = false, string Align = "l",
    bool Bullet = false, double After = 6, bool Mono = false, bool Italic = false);

/// <summary>본문 글꼴(언어별). 한글은 항상 맑은 고딕으로 표시한다.</summary>
public static class Fonts
{
    public static string Body { get; set; } = "맑은 고딕";
    public static string BodyGdi { get; set; } = "Malgun Gothic";
}

public static class Pal
{
    public const string Ink = "1F2A44";      // 남색(본문·제목)
    public const string Muted = "5B6B7F";    // 보조 글자
    public const string Teal = "0F7C80";     // 주 강조
    public const string TealSoft = "DCEFEF"; // 강조 배경
    public const string Bg = "F3F6F8";       // 밝은 배경
    public const string Card = "FFFFFF";
    public const string Line = "D5DDE5";
    public const string Amber = "B7791F";    // 주의
    public const string AmberSoft = "FBF1DC";
    public const string Red = "B03A2E";      // 위험
    public const string RedSoft = "F8E3E0";
    public const string Navy = "1F2A44";
    public const string White = "FFFFFF";
    public const string Code = "EEF2F5";
}

public sealed record ShapeInfo(double X, double Y, double W, double H, string? Fill, string? Line, string Geom, string Anchor, double Inset, IReadOnlyList<P> Text, string Name);

public sealed class Slide
{
    public List<ShapeInfo> Shapes { get; } = [];
    private readonly StringBuilder _sp = new();
    private int _id = 2;
    public string Background { get; init; } = Pal.Bg;

    private static long E(double inches) => (long)Math.Round(inches * 914400);
    private static string X(string s) => SecurityElement.Escape(s) ?? "";
    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>도형(사각형·둥근 사각형·원·화살표) + 글자.</summary>
    public Slide Box(double x, double y, double w, double h, IEnumerable<P>? text = null, string? fill = null,
        string? line = null, string geom = "rect", string anchor = "t", double inset = 0.12, bool shadow = false,
        double lineW = 1, string name = "Shape")
    {
        Shapes.Add(new ShapeInfo(x, y, w, h, fill, line, geom, anchor, inset, text?.ToList() ?? [], name));
        var id = _id++;
        var sb = _sp;
        var isText = fill is null && line is null;
        sb.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{X(name)} {id}\"/><p:cNvSpPr{(isText ? " txBox=\"1\"" : "")}/><p:nvPr/></p:nvSpPr>");
        sb.Append($"<p:spPr><a:xfrm><a:off x=\"{E(x)}\" y=\"{E(y)}\"/><a:ext cx=\"{E(w)}\" cy=\"{E(h)}\"/></a:xfrm>");
        sb.Append($"<a:prstGeom prst=\"{geom}\"><a:avLst>{(geom == "roundRect" ? "<a:gd name=\"adj\" fmla=\"val 7000\"/>" : "")}</a:avLst></a:prstGeom>");
        sb.Append(fill is null ? "<a:noFill/>" : $"<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill>");
        sb.Append(line is null ? "<a:ln><a:noFill/></a:ln>" : $"<a:ln w=\"{(long)(lineW * 12700)}\"><a:solidFill><a:srgbClr val=\"{line}\"/></a:solidFill></a:ln>");
        if (shadow)
            sb.Append("<a:effectLst><a:outerShdw blurRad=\"63500\" dist=\"19050\" dir=\"5400000\" algn=\"t\" rotWithShape=\"0\"><a:srgbClr val=\"1F2A44\"><a:alpha val=\"16000\"/></a:srgbClr></a:outerShdw></a:effectLst>");
        sb.Append("</p:spPr>");
        var ins = E(inset);
        sb.Append($"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"{ins}\" tIns=\"{ins}\" rIns=\"{ins}\" bIns=\"{ins}\" anchor=\"{anchor}\" rtlCol=\"0\"><a:noAutofit/></a:bodyPr><a:lstStyle/>");
        var any = false;
        foreach (var p in text ?? [])
        {
            any = true;
            var font = p.Mono ? "Consolas" : Fonts.Body;
            const string ea = "맑은 고딕"; // 한글은 항상 맑은 고딕(고정폭 글꼴엔 한글이 없음)
            var bullet = p.Bullet
                ? $"<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>"
                : "<a:buNone/>";
            var indent = p.Bullet ? $" marL=\"{E(0.22)}\" indent=\"-{E(0.22)}\"" : " marL=\"0\" indent=\"0\"";
            sb.Append($"<a:p><a:pPr algn=\"{p.Align}\"{indent}><a:lnSpc><a:spcPct val=\"110000\"/></a:lnSpc><a:spcAft><a:spcPts val=\"{(int)(p.After * 100)}\"/></a:spcAft>{bullet}</a:pPr>");
            sb.Append($"<a:r><a:rPr lang=\"ko-KR\" altLang=\"en-US\" sz=\"{(int)Math.Round(p.Size * 100)}\" b=\"{(p.Bold ? 1 : 0)}\" i=\"{(p.Italic ? 1 : 0)}\" dirty=\"0\">");
            sb.Append($"<a:solidFill><a:srgbClr val=\"{p.Color}\"/></a:solidFill><a:latin typeface=\"{font}\"/><a:ea typeface=\"{ea}\"/><a:cs typeface=\"{font}\"/></a:rPr>");
            sb.Append($"<a:t>{X(p.Text)}</a:t></a:r></a:p>");
        }
        if (!any) sb.Append("<a:p><a:endParaRPr lang=\"ko-KR\" dirty=\"0\"/></a:p>");
        sb.Append("</p:txBody></p:sp>");
        return this;
    }

    public Slide Text(double x, double y, double w, double h, params P[] text) => Box(x, y, w, h, text, name: "Text", inset: 0);

    public string Xml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
        $"<p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"{Background}\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>" +
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
        _sp + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
}

public static class Deck
{
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static void Write(string path, IReadOnlyList<Slide> slides, string title)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        var ct = new StringBuilder(Head + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" +
            "<Override PartName=\"/ppt/presProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presProps+xml\"/>" +
            "<Override PartName=\"/ppt/viewProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml\"/>" +
            "<Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
        for (var i = 1; i <= slides.Count; i++)
            ct.Append($"<Override PartName=\"/ppt/slides/slide{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        ct.Append("</Types>");
        Add("[Content_Types].xml", ct.ToString());

        Add("_rels/.rels", Head + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{Rel}/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>");

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        Add("docProps/core.xml", Head + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{SecurityElement.Escape(title)}</dc:title><dc:creator>WSL Remote Setup</dc:creator>" +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>");
        Add("docProps/app.xml", Head + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
            $"<Application>Microsoft Office PowerPoint</Application><Slides>{slides.Count}</Slides></Properties>");

        var pres = new StringBuilder(Head + $"<p:presentation xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" saveSubsetFonts=\"1\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>");
        for (var i = 0; i < slides.Count; i++) pres.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{10 + i}\"/>");
        pres.Append("</p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>" +
            "<p:defaultTextStyle><a:defPPr><a:defRPr lang=\"ko-KR\"/></a:defPPr></p:defaultTextStyle></p:presentation>");
        Add("ppt/presentation.xml", pres.ToString());

        var prels = new StringBuilder(Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"theme/theme1.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{Rel}/presProps\" Target=\"presProps.xml\"/>" +
            $"<Relationship Id=\"rId4\" Type=\"{Rel}/viewProps\" Target=\"viewProps.xml\"/>" +
            $"<Relationship Id=\"rId5\" Type=\"{Rel}/tableStyles\" Target=\"tableStyles.xml\"/>");
        for (var i = 0; i < slides.Count; i++)
            prels.Append($"<Relationship Id=\"rId{10 + i}\" Type=\"{Rel}/slide\" Target=\"slides/slide{i + 1}.xml\"/>");
        prels.Append("</Relationships>");
        Add("ppt/_rels/presentation.xml.rels", prels.ToString());

        Add("ppt/presProps.xml", Head + $"<p:presentationPr xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"/>");
        Add("ppt/viewProps.xml", Head + $"<p:viewPr xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>");
        Add("ppt/tableStyles.xml", Head + $"<a:tblStyleLst xmlns:a=\"{NsA}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>");

        const string emptyTree = "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree>";
        Add("ppt/slideMasters/slideMaster1.xml", Head + $"<p:sldMaster xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\">" +
            $"<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>{emptyTree}</p:cSld>" +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
            "<p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"3200\"/></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"1600\"/></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1600\"/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>");
        Add("ppt/slideMasters/_rels/slideMaster1.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"../theme/theme1.xml\"/></Relationships>");
        Add("ppt/slideLayouts/slideLayout1.xml", Head + $"<p:sldLayout xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\" type=\"blank\" preserve=\"1\">" +
            $"<p:cSld name=\"빈 화면\">{emptyTree}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Add("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        Add("ppt/theme/theme1.xml", Theme());

        for (var i = 0; i < slides.Count; i++)
        {
            Add($"ppt/slides/slide{i + 1}.xml", slides[i].Xml());
            Add($"ppt/slides/_rels/slide{i + 1}.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>");
        }
    }

    private static string Theme()
    {
        static string C(string n, string v) => $"<a:{n}><a:srgbClr val=\"{v}\"/></a:{n}>";
        const string font = "<a:latin typeface=\"맑은 고딕\"/><a:ea typeface=\"맑은 고딕\"/><a:cs typeface=\"\"/>";
        const string solid = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        const string ln = "<a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>";
        return Head + $"<a:theme xmlns:a=\"{NsA}\" name=\"WSL Remote Setup\"><a:themeElements><a:clrScheme name=\"WSL Remote Setup\">" +
            "<a:dk1><a:srgbClr val=\"1F2A44\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>" +
            C("dk2", "1F2A44") + C("lt2", "F3F6F8") + C("accent1", "0F7C80") + C("accent2", "1F2A44") + C("accent3", "B7791F") +
            C("accent4", "B03A2E") + C("accent5", "5B6B7F") + C("accent6", "7FB7B9") + C("hlink", "0F7C80") + C("folHlink", "5B6B7F") +
            $"</a:clrScheme><a:fontScheme name=\"WSL Remote Setup\"><a:majorFont>{font}</a:majorFont><a:minorFont>{font}</a:minorFont></a:fontScheme>" +
            $"<a:fmtScheme name=\"WSL Remote Setup\"><a:fillStyleLst>{solid}{solid}{solid}</a:fillStyleLst>" +
            $"<a:lnStyleLst>{ln}{ln}{ln}</a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            $"<a:bgFillStyleLst>{solid}{solid}{solid}</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }
}
