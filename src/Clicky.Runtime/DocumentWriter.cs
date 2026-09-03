using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Clicky.Runtime;

public static class DocumentWriter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static async Task GenerateAsync(string destination, string title, string content, CancellationToken cancellationToken = default)
    {
        if (File.Exists(destination))
            throw new IOException("The destination already exists. Choose a new document name.");
        var extension = Path.GetExtension(destination).ToLowerInvariant();
        if (extension is not (".txt" or ".md" or ".csv" or ".json" or ".docx" or ".xlsx" or ".pptx" or ".pdf"))
            throw new ArgumentException("Choose a supported output extension: .txt, .md, .csv, .json, .docx, .xlsx, .pptx, or .pdf.");
        var temporary = destination + ".clicky-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (extension)
            {
                case ".docx":
                    await Task.Run(() => WriteDocx(temporary, title, content), cancellationToken).ConfigureAwait(false);
                    break;
                case ".xlsx":
                    await Task.Run(() => WriteXlsx(temporary, title, content), cancellationToken).ConfigureAwait(false);
                    break;
                case ".pptx":
                    await Task.Run(() => WritePptx(temporary, title, content), cancellationToken).ConfigureAwait(false);
                    break;
                case ".pdf":
                    if (ContainsRtl(title + content))
                        await WriteShapedPdfAsync(temporary, title, content, cancellationToken).ConfigureAwait(false);
                    else
                        await Task.Run(() => WritePdf(temporary, title, content), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteDocx(string path, string title, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        ContentTypes(zip, ("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"), ("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"));
        Relationships(zip, "_rels/.rels", ("rId1", "officeDocument", "word/document.xml"));
        Relationships(zip, "word/_rels/document.xml.rels", ("rId1", "styles", "styles.xml"));
        var body = new XElement(W + "body", WordParagraph(title, "Title"));
        foreach (var line in content.Replace("\r", "").Split('\n'))
            body.Add(WordParagraph(line.TrimStart('#', ' '), line.StartsWith('#') ? "Heading1" : "Normal"));
        body.Add(new XElement(W + "sectPr", new XElement(W + "pgSz", new XAttribute(W + "w", "11906"), new XAttribute(W + "h", "16838")),
            new XElement(W + "pgMar", new XAttribute(W + "top", "1134"), new XAttribute(W + "right", "1134"), new XAttribute(W + "bottom", "1134"), new XAttribute(W + "left", "1134"))));
        AddXml(zip, "word/document.xml", new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R), body));
        AddXml(zip, "word/styles.xml", new XElement(W + "styles", new XAttribute(XNamespace.Xmlns + "w", W),
            WordStyle("Normal", "Normal", 22, false), WordStyle("Title", "Title", 40, true), WordStyle("Heading1", "Heading 1", 28, true)));
    }
    private static XElement WordParagraph(string text, string style)
    {
        var rtl = ContainsRtl(text);
        var properties = new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", style)));
        if (rtl)
            properties.Add(new XElement(W + "bidi"));
        properties.Add(new XElement(W + "spacing", new XAttribute(W + "after", "160")));
        if (rtl)
            properties.Add(new XElement(W + "jc", new XAttribute(W + "val", "right")));
        return new XElement(W + "p", properties, new XElement(W + "r", rtl ? new XElement(W + "rPr", new XElement(W + "rtl"), new XElement(W + "lang", new XAttribute(W + "bidi", "fa-IR"))) : null,
            new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
    }
    private static XElement WordStyle(string id, string name, int size, bool bold) => new(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", id),
        id == "Normal" ? new XAttribute(W + "default", "1") : null, new XElement(W + "name", new XAttribute(W + "val", name)),
        new XElement(W + "rPr", new XElement(W + "rFonts", new XAttribute(W + "ascii", "Arial"), new XAttribute(W + "hAnsi", "Arial"), new XAttribute(W + "cs", "Arial")),
            bold ? new XElement(W + "b") : null, new XElement(W + "sz", new XAttribute(W + "val", size)), new XElement(W + "szCs", new XAttribute(W + "val", size))));

    private static void WriteXlsx(string path, string title, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        ContentTypes(zip, ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"), ("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
        Relationships(zip, "_rels/.rels", ("rId1", "officeDocument", "xl/workbook.xml"));
        Relationships(zip, "xl/_rels/workbook.xml.rels", ("rId1", "worksheet", "worksheets/sheet1.xml"));
        AddXml(zip, "xl/workbook.xml", new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R), new XElement(S + "sheets", new XElement(S + "sheet", new XAttribute("name", "Report"), new XAttribute("sheetId", 1), new XAttribute(R + "id", "rId1")))));
        var sheetData = new XElement(S + "sheetData");
        var lines = (title + "\n" + content.Replace("\r", "")).Split('\n');
        for (var row = 0; row < lines.Length; row++)
        {
            var cells = lines[row].Split('\t');
            var element = new XElement(S + "row", new XAttribute("r", row + 1));
            for (var column = 0; column < cells.Length; column++)
                element.Add(new XElement(S + "c", new XAttribute("r", ColumnName(column + 1) + (row + 1)), new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), cells[column]))));
            sheetData.Add(element);
        }
        AddXml(zip, "xl/worksheets/sheet1.xml", new XElement(S + "worksheet", new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", 0), ContainsRtl(content) ? new XAttribute("rightToLeft", 1) : null)), sheetData));
    }
    private static string ColumnName(int index)
    {
        var result = "";
        while (index > 0)
        {
            index--;
            result = (char)('A' + index % 26) + result;
            index /= 26;
        }
        return result;
    }

    private static void WritePptx(string path, string title, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var slides = content.Replace("\r", "").Split("\n---\n", StringSplitOptions.None);
        var types = new List<(string, string)> { ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"), ("/ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"), ("/ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"), ("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml") };
        for (var index = 1; index <= slides.Length; index++)
            types.Add(($"/ppt/slides/slide{index}.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"));
        ContentTypes(zip, types.ToArray());
        Relationships(zip, "_rels/.rels", ("rId1", "officeDocument", "ppt/presentation.xml"));
        var relations = new List<(string, string, string)> { ("rIdMaster", "slideMaster", "slideMasters/slideMaster1.xml") };
        var ids = new XElement(P + "sldIdLst");
        for (var index = 1; index <= slides.Length; index++)
        {
            relations.Add(($"rId{index}", "slide", $"slides/slide{index}.xml"));
            ids.Add(new XElement(P + "sldId", new XAttribute("id", 255 + index), new XAttribute(R + "id", $"rId{index}")));
            var lines = slides[index - 1].Split('\n');
            var heading = index == 1 ? title : lines.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.TrimStart('#', ' ') ?? $"Slide {index}";
            var slideText = index == 1 ? slides[0] : string.Join('\n', lines.SkipWhile(string.IsNullOrWhiteSpace).Skip(1));
            AddXml(zip, $"ppt/slides/slide{index}.xml", new XElement(P + "sld", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(P + "cSld", new XElement(P + "spTree", GroupProperties(), TextShape(2, "Title", heading, 640000, 450000, 10700000, 950000, 3200), TextShape(3, "Body", slideText, 640000, 1620000, 10700000, 4500000, 1900))), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
            Relationships(zip, $"ppt/slides/_rels/slide{index}.xml.rels", ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"));
        }
        Relationships(zip, "ppt/_rels/presentation.xml.rels", relations.ToArray());
        AddXml(zip, "ppt/presentation.xml", new XElement(P + "presentation", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", "2147483648"), new XAttribute(R + "id", "rIdMaster"))), ids, new XElement(P + "sldSz", new XAttribute("cx", 12192000), new XAttribute("cy", 6858000)), new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000))));
        var colorMap = new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"), new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"), new XAttribute("accent1", "accent1"), new XAttribute("accent2", "accent2"), new XAttribute("accent3", "accent3"), new XAttribute("accent4", "accent4"), new XAttribute("accent5", "accent5"), new XAttribute("accent6", "accent6"), new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink"));
        AddXml(zip, "ppt/slideMasters/slideMaster1.xml", new XElement(P + "sldMaster", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), new XElement(P + "cSld", new XElement(P + "spTree", GroupProperties())), colorMap, new XElement(P + "sldLayoutIdLst", new XElement(P + "sldLayoutId", new XAttribute("id", "2147483649"), new XAttribute(R + "id", "rId1"))), new XElement(P + "txStyles", new XElement(P + "titleStyle"), new XElement(P + "bodyStyle"), new XElement(P + "otherStyle"))));
        Relationships(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "theme", "../theme/theme1.xml"));
        AddXml(zip, "ppt/slideLayouts/slideLayout1.xml", new XElement(P + "sldLayout", new XAttribute("type", "blank"), new XAttribute("preserve", "1"), new XElement(P + "cSld", new XAttribute("name", "Blank"), new XElement(P + "spTree", GroupProperties())), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
        Relationships(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", ("rId1", "slideMaster", "../slideMasters/slideMaster1.xml"));
        AddXml(zip, "ppt/theme/theme1.xml", Theme());
    }
    private static object[] GroupProperties() => [new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")), new XElement(P + "grpSpPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)), new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0))))];
    private static XElement TextShape(int id, string name, string text, long x, long y, long width, long height, int size) => new(P + "sp",
        new XElement(P + "nvSpPr", new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)), new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
        new XElement(P + "spPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", x), new XAttribute("y", y)), new XElement(A + "ext", new XAttribute("cx", width), new XAttribute("cy", height))), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))),
        new XElement(P + "txBody", new XElement(A + "bodyPr", new XAttribute("wrap", "square"), new XElement(A + "normAutofit")), new XElement(A + "lstStyle"), text.Split('\n').Select(line => new XElement(A + "p", new XElement(A + "pPr", new XAttribute("algn", ContainsRtl(line) ? "r" : "l"), ContainsRtl(line) ? new XAttribute("rtl", 1) : null),
            new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", ContainsRtl(line) ? "fa-IR" : "en-US"), new XAttribute("sz", size), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", name == "Title" ? "174A8B" : "202938"))), new XElement(A + "latin", new XAttribute("typeface", "Arial")), new XElement(A + "cs", new XAttribute("typeface", "Arial"))), new XElement(A + "t", line))))));
    private static XElement Theme()
    {
        var colors = new[] { ("dk1", "202938"), ("lt1", "FFFFFF"), ("dk2", "174A8B"), ("lt2", "F4F7FB"), ("accent1", "386BFF"), ("accent2", "159A8C"), ("accent3", "D49432"), ("accent4", "8758B5"), ("accent5", "3E889C"), ("accent6", "C66073"), ("hlink", "386BFF"), ("folHlink", "8758B5") };
        XElement Fonts(string name) => new(A + name, new XElement(A + "latin", new XAttribute("typeface", "Arial")), new XElement(A + "ea", new XAttribute("typeface", "Arial")), new XElement(A + "cs", new XAttribute("typeface", "Arial")));
        XElement Fill() => new(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr")));
        return new XElement(A + "theme", new XAttribute("name", "Clicky"), new XElement(A + "themeElements", new XElement(A + "clrScheme", new XAttribute("name", "Clicky"), colors.Select(c => new XElement(A + c.Item1, new XElement(A + "srgbClr", new XAttribute("val", c.Item2))))),
            new XElement(A + "fontScheme", new XAttribute("name", "Arial"), Fonts("majorFont"), Fonts("minorFont")), new XElement(A + "fmtScheme", new XAttribute("name", "Clicky"),
                new XElement(A + "fillStyleLst", Fill(), Fill(), Fill()), new XElement(A + "lnStyleLst", Enumerable.Range(1, 3).Select(i => new XElement(A + "ln", new XAttribute("w", i * 12700), Fill(), new XElement(A + "prstDash", new XAttribute("val", "solid"))))),
                new XElement(A + "effectStyleLst", Enumerable.Range(1, 3).Select(_ => new XElement(A + "effectStyle", new XElement(A + "effectLst")))), new XElement(A + "bgFillStyleLst", Fill(), Fill(), Fill()))));
    }

    private static readonly object FontLock = new();
    private static async Task WriteShapedPdfAsync(string path, string title, string content, CancellationToken cancellationToken)
    {
        // Chromium provides complex-script shaping and Unicode PDF maps. Use a fresh, offline profile,
        // never the person's open browser/profile, and only render escaped local text with a restrictive CSP.
        var browser = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe") }.FirstOrDefault(File.Exists);
        if (browser is null)
            throw new InvalidOperationException("Persian/Arabic PDF export requires the Microsoft Edge print engine. Install Edge or choose DOCX/PPTX to preserve correctly marked RTL text.");
        var scratch = Path.Combine(Path.GetTempPath(), "ClickyPdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var htmlPath = Path.Combine(scratch, "document.html");
        var html = "<!doctype html><html lang=\"fa\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>" + WebUtility.HtmlEncode(title) +
            "</title><style>@page{size:A4;margin:20mm}body{font:12pt Arial,'Segoe UI',sans-serif;color:#202938;line-height:1.7}h1{font-size:22pt;color:#174a8b}p{white-space:pre-wrap;overflow-wrap:anywhere;margin:0 0 10pt}h1,p{unicode-bidi:plaintext}h1{break-after:avoid}p{orphans:3;widows:3}</style></head><body><h1 dir=\"auto\">" + WebUtility.HtmlEncode(title) + "</h1>" +
            string.Concat(content.Replace("\r", "").Split('\n').Select(line => "<p dir=\"auto\">" + WebUtility.HtmlEncode(line) + "</p>")) + "</body></html>";
        await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process { StartInfo = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--disable-background-networking", "--disable-extensions", "--disable-sync", "--no-first-run", "--no-default-browser-check", "--no-pdf-header-footer", "--user-data-dir=" + Path.Combine(scratch, "profile"), "--print-to-pdf=" + Path.GetFullPath(path), new Uri(htmlPath).AbsoluteUri })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start())
                throw new IOException("Could not start the local PDF print engine.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length < 100)
                throw new IOException("The local PDF print engine did not produce a document. Try DOCX export and check that Edge can start.");
            // PDF glyph streams often store complex scripts in visual order. Preserve the original logical
            // text for this app's own exports, tied to a hash of the visible text so later edits cannot return stale content.
            string visibleHash;
            using (var read = UglyToad.PdfPig.PdfDocument.Open(path))
                visibleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", read.GetPages().Select(p => p.Text)))));
            byte[] tagged;
            using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
            using (var stream = new MemoryStream())
            {
                pdf.Info.Elements.SetString("/ClickyLogicalTextBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(title + "\n" + content)));
                pdf.Info.Elements.SetString("/ClickyVisibleTextHash", visibleHash);
                pdf.Save(stream, false);
                tagged = stream.ToArray();
            }
            await File.WriteAllBytesAsync(path, tagged, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("The PDF print engine did not finish within 45 seconds."); }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) { }
            // Remove only the exact directory created by this invocation. It never contains user browser data.
            try
            {
                Directory.Delete(scratch, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static void WritePdf(string path, string title, string content)
    {
        if (ContainsRtl(title + content))
            throw new InvalidOperationException("Persian/Arabic PDF export requires text shaping that this PDF exporter does not provide. Choose DOCX or PPTX for correctly marked RTL text, then export PDF from Office.");
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new SystemFontResolver();
        }
        using var pdf = new PdfDocument();
        pdf.Info.Title = title;
        pdf.Info.Creator = "HeyBuddy";
        var bodyFont = new XFont("ClickyArial", 11);
        var titleFont = new XFont("ClickyArial", 20, XFontStyleEx.Bold);
        PdfPage page = null!;
        XGraphics canvas = null!;
        double y = 0;
        void NewPage()
        {
            canvas?.Dispose();
            page = pdf.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            canvas = XGraphics.FromPdfPage(page);
            y = 52;
        }
        NewPage();
        foreach (var (paragraph, font) in new[] { (title, titleFont) }.Concat(content.Replace("\r", "").Split('\n').Select(p => (p, bodyFont))))
        {
            var width = page.Width.Point - 104;
            var line = new StringBuilder();
            void DrawLine()
            {
                if (y > page.Height.Point - 60)
                    NewPage();
                canvas.DrawString(line.ToString(), font, XBrushes.Black, new XRect(52, y, width, 26), XStringFormats.TopLeft);
                y += font.Size * 1.45;
                line.Clear();
            }
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > 0 && canvas.MeasureString(line + " " + word, font).Width > width)
                    DrawLine();
                if (line.Length > 0)
                    line.Append(' ');
                line.Append(word);
            }
            DrawLine();
            y += 5;
        }
        canvas.Dispose();
        pdf.Save(path);
    }
    private sealed class SystemFontResolver : IFontResolver
    {
        public byte[] GetFont(string faceName)
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var file = Path.Combine(fonts, faceName == "arial-bold" ? "arialbd.ttf" : "arial.ttf");
            if (!File.Exists(file))
                throw new InvalidOperationException("Arial is required for PDF export. DOCX and text export remain available.");
            return File.ReadAllBytes(file);
        }
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) => new(bold ? "arial-bold" : "arial-regular");
    }
    private static bool ContainsRtl(string text) => text.Any(c => c is >= '\u0590' and <= '\u08FF');
    private static void ContentTypes(ZipArchive zip, params (string Path, string Type)[] parts)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        AddXml(zip, "[Content_Types].xml", new XElement(ns + "Types", new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")), new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")), parts.Select(p => new XElement(ns + "Override", new XAttribute("PartName", p.Path), new XAttribute("ContentType", p.Type)))));
    }
    private static void Relationships(ZipArchive zip, string path, params (string Id, string Type, string Target)[] relationships)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        AddXml(zip, path, new XElement(ns + "Relationships", relationships.Select(r => new XElement(ns + "Relationship", new XAttribute("Id", r.Id), new XAttribute("Type", R.NamespaceName + "/" + r.Type), new XAttribute("Target", r.Target)))));
    }
    private static void AddXml(ZipArchive zip, string path, XElement element)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        writer.Write(element.ToString(SaveOptions.DisableFormatting));
    }
}
