using System.IO.Compression;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Xunit;

namespace Clicky.Runtime.Tests;

public sealed class DocumentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClickyRuntimeTests", Guid.NewGuid().ToString("N"));
    private readonly DocumentTools tools;
    public DocumentTests()
    {
        Directory.CreateDirectory(root);
        tools = new(new()
        {
            WorkDirectory = root
        });
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("file.txt:secret")]
    public void RejectsPathEscape(string path) => Assert.Throws<UnauthorizedAccessException>(() => tools.ResolvePath(path));

    [Fact]
    public async Task ExistingFilesCannotBeOverwritten()
    {
        File.WriteAllText(Path.Combine(root, "note.txt"), "original");
        var result = await tools.ExecuteAsync("files.write_text", JsonSchema.Parse("""{"path":"note.txt","content":"changed"}"""), default);
        Assert.False(result.Success);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "note.txt")));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task GeneratedDocumentsActuallyRoundTrip(string extension)
    {
        var path = Path.Combine(root, "report" + extension);
        await DocumentWriter.GenerateAsync(path, "Validation report", "Local document round trip\nEnglish and Turkish: merhaba dünya.");
        var read = await tools.ExtractAsync(path);
        Assert.Contains("Local document round trip", read.Text);
        Assert.Contains("merhaba", read.Text);
        if (extension != ".pdf")
        {
            using OpenXmlPackage package = extension switch
            {
                ".docx" => WordprocessingDocument.Open(path, false),
                ".xlsx" => SpreadsheetDocument.Open(path, false),
                _ => PresentationDocument.Open(path, false)
            };
            var errors = new OpenXmlValidator().Validate(package).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }
    }

    [Fact]
    public async Task PersianDocxContainsRtlAndPreservesText()
    {
        var path = Path.Combine(root, "persian.docx");
        await DocumentWriter.GenerateAsync(path, "گزارش", "سلام، این یک گزارش محلی است.");
        var read = await tools.ExtractAsync(path);
        Assert.Contains("گزارش محلی", read.Text);
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        Assert.Contains("w:bidi", reader.ReadToEnd());
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
    }

    [Fact]
    public async Task PersianPdfUsesLocalTextShaping()
    {
        var path = Path.Combine(root, "persian.pdf");
        await DocumentWriter.GenerateAsync(path, "گزارش", "سلام، این یک گزارش محلی است.");
        var extracted = await tools.ExtractAsync(path);
        Assert.Contains("گزارش", extracted.Text);
        Assert.Contains("محلی", extracted.Text);
    }

    [Fact]
    public async Task DocumentsCannotWriteOutsideWorkspace()
    {
        var result = await tools.ExecuteAsync("documents.generate", JsonSchema.Parse("""{"path":"../outside.docx","title":"x","content":"x"}"""), default);
        Assert.False(result.Success);
        Assert.Contains("outside", result.Message);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("file:///c:/windows/win.ini")]
    public async Task ResearchRejectsUnsafeUrls(string url) => await Assert.ThrowsAsync<ArgumentException>(() => tools.ReadUrlAsync(url));

    [Fact]
    public async Task ResearchCannotReachLoopback()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => tools.ReadUrlAsync("https://127.0.0.1/"));
    }

    [Fact]
    public async Task ImportIsAnExplicitOperationAndNotAnAgentTool()
    {
        Assert.DoesNotContain(tools.Tools, t => t.Name.Contains("import", StringComparison.OrdinalIgnoreCase));
        var selected = Path.Combine(root, "user-selected.txt");
        File.WriteAllText(selected, "Selected document");
        var imported = await tools.ImportAsync(selected);
        Assert.StartsWith(Path.Combine(root, "Imported"), imported.Path);
        Assert.Equal("Selected document", imported.Text);
        Assert.Equal(64, imported.Sha256.Length);
    }

    public void Dispose()
    {
        tools.Dispose();
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task StaleLogicalPdfMetadataCannotReplaceVisibleText()
    {
        var path = Path.Combine(root, "metadata.pdf");
        await DocumentWriter.GenerateAsync(path, "Visible title", "Current visible content");
        byte[] tagged;
        using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        using (var stream = new MemoryStream())
        {
            pdf.Info.Elements.SetString("/ClickyLogicalTextBase64", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Stale hidden text")));
            pdf.Info.Elements.SetString("/ClickyVisibleTextHash", "incorrect");
            pdf.Save(stream, false);
            tagged = stream.ToArray();
        }
        await File.WriteAllBytesAsync(path, tagged);
        var read = await tools.ExtractAsync(path);
        Assert.Contains("Current visible content", read.Text);
        Assert.DoesNotContain("Stale hidden text", read.Text);
    }
}
