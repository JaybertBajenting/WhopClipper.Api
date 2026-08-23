using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class DocumentTextExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clipper-tests", Guid.NewGuid().ToString("N"));

    public DocumentTextExtractorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadsParagraphsTabsAndBreaksFromWordPackage()
    {
        var path = CreateDocx("Post to TikTok and Instagram Reels.", "Clips must be 20-45 seconds.");

        var result = await new DocumentTextExtractor().ExtractAsync(path, CampaignDocumentKind.Word, CancellationToken.None);

        Assert.Contains("Post to TikTok and Instagram Reels.", result.Text);
        Assert.Contains("Clips must be 20-45 seconds.", result.Text);
        Assert.Null(result.PageCount);
    }

    [Fact]
    public async Task RejectsAPackageThatIsNotAWordDocument()
    {
        var path = Path.Combine(_root, "legacy.docx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("other/part.xml").Open()))
            writer.Write("<root/>");

        var extractor = new DocumentTextExtractor();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            extractor.ExtractAsync(path, CampaignDocumentKind.Word, CancellationToken.None));

        Assert.Contains(".docx", error.Message);
    }

    [Fact]
    public async Task ReadsPlainTextAndStripsControlCharacters()
    {
        var path = Path.Combine(_root, "brief.txt");
        await File.WriteAllTextAsync(path, "Line one\u0007\r\nLine two\r\n\r\n\r\n\r\nLine three   \r\n", CancellationToken.None);

        var result = await new DocumentTextExtractor().ExtractAsync(path, CampaignDocumentKind.Text, CancellationToken.None);

        Assert.DoesNotContain('\u0007', result.Text);
        Assert.DoesNotContain('\r', result.Text);
        Assert.DoesNotContain("\n\n\n", result.Text);
        Assert.EndsWith("Line three", result.Text);
    }

    [Fact]
    public async Task StoresAnUploadedBriefInsideTheCampaignWorkspace()
    {
        var service = CreateService(out var workspace);
        var campaignId = Guid.NewGuid();
        await using var content = File.OpenRead(CreateDocx("Clips must be 20-45 seconds."));

        var stored = await service.StoreAsync(campaignId, "campaign brief.docx", content, CancellationToken.None);

        Assert.Null(stored.Warning);
        Assert.Equal(DocumentExtractionStatus.Extracted, stored.Document.ExtractionStatus);
        Assert.Contains("20-45 seconds", stored.Document.ExtractedText);
        Assert.True(stored.Document.IncludeInExtraction);
        Assert.StartsWith(Path.GetFullPath(_root), workspace.ResolveRelative(stored.Document.RelativePath));
        Assert.True(File.Exists(workspace.ResolveRelative(stored.Document.RelativePath)));
    }

    [Fact]
    public async Task RejectsAFileWhoseSignatureDoesNotMatchItsExtension()
    {
        var service = CreateService(out _);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("This is plain text pretending to be a PDF."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StoreAsync(Guid.NewGuid(), "brief.pdf", content, CancellationToken.None));

        Assert.Contains("not a valid PDF", error.Message);
    }

    [Fact]
    public async Task RejectsAnUnsupportedExtension()
    {
        var service = CreateService(out _);
        await using var content = new MemoryStream([1, 2, 3, 4]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StoreAsync(Guid.NewGuid(), "brief.rtf", content, CancellationToken.None));
    }

    [Fact]
    public void ComposerAppendsOnlyIncludedAndSuccessfullyExtractedBriefs()
    {
        var documents = new[]
        {
            new CampaignDocument { FileName = "rules.pdf", Kind = CampaignDocumentKind.Pdf, PageCount = 2,
                ExtractedText = "Vertical 9:16 only.", ExtractionStatus = DocumentExtractionStatus.Extracted,
                UploadedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
            new CampaignDocument { FileName = "excluded.docx", Kind = CampaignDocumentKind.Word,
                ExtractedText = "Ignore me.", ExtractionStatus = DocumentExtractionStatus.Extracted,
                IncludeInExtraction = false, UploadedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new CampaignDocument { FileName = "scan.pdf", Kind = CampaignDocumentKind.Pdf,
                ExtractedText = string.Empty, ExtractionStatus = DocumentExtractionStatus.Failed,
                UploadedAt = DateTimeOffset.UtcNow },
        };

        var composed = RequirementSourceComposer.Compose("Post to TikTok.", documents);

        Assert.StartsWith("Post to TikTok.", composed);
        Assert.Contains("--- Attached brief: rules.pdf (Pdf, 2 pages) ---", composed);
        Assert.Contains("Vertical 9:16 only.", composed);
        Assert.DoesNotContain("Ignore me.", composed);
        Assert.DoesNotContain("scan.pdf", composed);
    }

    private CampaignDocumentService CreateService(out WorkspaceService workspace)
    {
        workspace = new WorkspaceService(Options.Create(new AppOptions { WorkspaceRoot = _root }));
        return new CampaignDocumentService(workspace, new DocumentTextExtractor(),
            NullLogger<CampaignDocumentService>.Instance);
    }

    private string CreateDocx(params string[] paragraphs)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.docx");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open());
        writer.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""");
        foreach (var paragraph in paragraphs)
            writer.Write($"<w:p><w:r><w:t>{paragraph}</w:t></w:r></w:p>");
        writer.Write("</w:body></w:document>");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
