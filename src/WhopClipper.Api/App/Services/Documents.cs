using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Services;

public sealed record DocumentTextResult(string Text, int? PageCount);

public interface IDocumentTextExtractor
{
    /// <summary>Reads campaign-brief text out of a local PDF, Word, or plain-text file. The result is inert data, never instructions.</summary>
    Task<DocumentTextResult> ExtractAsync(string fullPath, CampaignDocumentKind kind, CancellationToken cancellationToken);
}

public sealed partial class DocumentTextExtractor : IDocumentTextExtractor
{
    public const int MaxExtractedCharacters = 400_000;
    private const long MaxUncompressedEntryBytes = 64L * 1024 * 1024;
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public async Task<DocumentTextResult> ExtractAsync(string fullPath, CampaignDocumentKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return kind switch
        {
            CampaignDocumentKind.Pdf => ExtractPdf(fullPath),
            CampaignDocumentKind.Word => ExtractWord(fullPath),
            _ => new DocumentTextResult(Normalize(await File.ReadAllTextAsync(fullPath, cancellationToken)), null)
        };
    }

    private static DocumentTextResult ExtractPdf(string fullPath)
    {
        using var document = PdfDocument.Open(fullPath, new ParsingOptions { UseLenientParsing = true });
        var builder = new StringBuilder();
        var pages = 0;
        foreach (var page in document.GetPages())
        {
            pages++;
            string pageText;
            try { pageText = ContentOrderTextExtractor.GetText(page) ?? string.Empty; }
            catch (Exception) { pageText = page.Text ?? string.Empty; }
            if (string.IsNullOrWhiteSpace(pageText)) continue;
            builder.AppendLine(pageText.Trim());
            builder.AppendLine();
            if (builder.Length > MaxExtractedCharacters) break;
        }
        return new DocumentTextResult(Normalize(builder.ToString()), pages);
    }

    private static DocumentTextResult ExtractWord(string fullPath)
    {
        using var archive = ZipFile.OpenRead(fullPath);
        var builder = new StringBuilder();
        foreach (var entryName in OrderedWordParts(archive))
        {
            var entry = archive.GetEntry(entryName);
            if (entry is null || entry.Length > MaxUncompressedEntryBytes) continue;
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            foreach (var paragraph in document.Descendants(Word + "p"))
            {
                var text = ReadParagraph(paragraph);
                if (text.Length > 0) builder.AppendLine(text);
            }
            builder.AppendLine();
            if (builder.Length > MaxExtractedCharacters) break;
        }
        if (builder.Length == 0)
            throw new InvalidOperationException("No readable text was found. Legacy .doc files must be re-saved as .docx.");
        return new DocumentTextResult(Normalize(builder.ToString()), null);
    }

    private static IEnumerable<string> OrderedWordParts(ZipArchive archive)
    {
        if (archive.GetEntry("word/document.xml") is null)
            throw new InvalidOperationException("This is not a Word .docx package. Legacy .doc files must be re-saved as .docx.");
        yield return "word/document.xml";
        foreach (var entry in archive.Entries
            .Where(x => x.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)
                || x.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)
                || x.FullName.StartsWith("word/footnotes", StringComparison.OrdinalIgnoreCase)
                || x.FullName.StartsWith("word/endnotes", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.FullName).Order(StringComparer.OrdinalIgnoreCase))
            yield return entry;
    }

    private static string ReadParagraph(XElement paragraph)
    {
        var builder = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == Word + "t") builder.Append(node.Value);
            else if (node.Name == Word + "tab") builder.Append('\t');
            else if (node.Name == Word + "br" || node.Name == Word + "cr") builder.Append('\n');
        }
        return builder.ToString().Trim();
    }

    /// <summary>Strips control characters, normalizes line endings, and bounds the size so a brief cannot flood the prompt.</summary>
    internal static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var cleaned = new StringBuilder(Math.Min(value.Length, MaxExtractedCharacters));
        foreach (var character in value)
        {
            if (character is '\r') continue;
            if (character is '\n' or '\t' || !char.IsControl(character)) cleaned.Append(character);
            else cleaned.Append(' ');
            if (cleaned.Length >= MaxExtractedCharacters) break;
        }
        var text = BlankLineRegex().Replace(cleaned.ToString(), "\n\n");
        return TrailingSpaceRegex().Replace(text, string.Empty).Trim();
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRegex();
    [GeneratedRegex(@"[ \t]+(?=\n)")]
    private static partial Regex TrailingSpaceRegex();
}

public sealed record DocumentUploadResult(CampaignDocument Document, string? Warning);

/// <summary>Validates, stores, and text-extracts an uploaded campaign brief. Extension and magic bytes must agree.</summary>
public sealed class CampaignDocumentService(WorkspaceService workspace, IDocumentTextExtractor extractor,
    ILogger<CampaignDocumentService> logger)
{
    public const long MaxUploadBytes = 25L * 1024 * 1024;

    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];

    private static readonly Dictionary<string, CampaignDocumentKind> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = CampaignDocumentKind.Pdf,
        [".docx"] = CampaignDocumentKind.Word,
        [".dotx"] = CampaignDocumentKind.Word,
        [".txt"] = CampaignDocumentKind.Text,
        [".md"] = CampaignDocumentKind.Markdown,
    };

    public static string AcceptedExtensions => string.Join(", ", SupportedExtensions.Keys.Order(StringComparer.OrdinalIgnoreCase));

    public static bool TryResolveKind(string fileName, out CampaignDocumentKind kind) =>
        SupportedExtensions.TryGetValue(Path.GetExtension(fileName), out kind);

    public async Task<DocumentUploadResult> StoreAsync(Guid campaignId, string originalFileName, Stream content,
        CancellationToken cancellationToken)
    {
        if (!TryResolveKind(originalFileName, out var kind))
            throw new InvalidOperationException($"Unsupported file type. Accepted formats: {AcceptedExtensions}.");
        var directory = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "campaign", "documents"));
        Directory.CreateDirectory(directory);
        var safeName = WorkspaceService.SafeFileName(Path.GetFileName(originalFileName), "brief");
        var storedName = $"{Guid.NewGuid():N}-{safeName}";
        var fullPath = Path.Combine(directory, storedName);
        WorkspaceService.EnsureInside(directory, fullPath);

        await using (var file = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        try
        {
            EnsureSignatureMatches(fullPath, kind);
            var document = new CampaignDocument
            {
                CampaignId = campaignId,
                FileName = safeName,
                RelativePath = workspace.ToRelative(fullPath),
                Kind = kind,
                SizeBytes = new FileInfo(fullPath).Length,
                ContentHash = await WorkspaceService.HashFileAsync(fullPath, cancellationToken),
            };
            string? warning = null;
            try
            {
                var extracted = await extractor.ExtractAsync(fullPath, kind, cancellationToken);
                document.ExtractedText = extracted.Text;
                document.ExtractedCharacterCount = extracted.Text.Length;
                document.PageCount = extracted.PageCount;
                document.ExtractionStatus = DocumentExtractionStatus.Extracted;
                if (extracted.Text.Length == 0)
                {
                    document.ExtractionStatus = DocumentExtractionStatus.Failed;
                    document.ExtractionError = "No text layer was found. Scanned or image-only files need OCR before they can be extracted.";
                    warning = document.ExtractionError;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Text extraction failed for campaign document {FileName}", safeName);
                document.ExtractionStatus = DocumentExtractionStatus.Failed;
                document.ExtractionError = ex.Message.Length <= 500 ? ex.Message : ex.Message[..500];
                warning = document.ExtractionError;
            }
            return new DocumentUploadResult(document, warning);
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }
    }

    public void DeleteFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        try { TryDelete(workspace.ResolveRelative(relativePath)); }
        catch (InvalidOperationException ex) { logger.LogWarning(ex, "Refused to delete a document outside the workspace."); }
    }

    private static void TryDelete(string fullPath)
    {
        try { if (File.Exists(fullPath)) File.Delete(fullPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Browser-supplied content types are untrusted, so PDF and Word uploads are confirmed by their file signature.</summary>
    private static void EnsureSignatureMatches(string fullPath, CampaignDocumentKind kind)
    {
        if (kind is not (CampaignDocumentKind.Pdf or CampaignDocumentKind.Word)) return;
        Span<byte> header = stackalloc byte[5];
        using var stream = File.OpenRead(fullPath);
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var matches = kind switch
        {
            CampaignDocumentKind.Pdf => read >= 5 && header[..5].SequenceEqual("%PDF-"u8),
            CampaignDocumentKind.Word => read >= 4 && header[..4].SequenceEqual(ZipSignature),
            _ => true
        };
        if (!matches)
            throw new InvalidOperationException(kind == CampaignDocumentKind.Pdf
                ? "The file is not a valid PDF."
                : "The file is not a valid .docx package. Legacy .doc files must be re-saved as .docx.");
    }
}

/// <summary>Combines the pasted brief with attached document text so one auditable text blob drives extraction.</summary>
public static class RequirementSourceComposer
{
    public static string Compose(string pastedInstructions, IEnumerable<CampaignDocument> documents)
    {
        var builder = new StringBuilder(pastedInstructions?.Trim() ?? string.Empty);
        foreach (var document in documents
            .Where(x => x.IncludeInExtraction && x.ExtractionStatus == DocumentExtractionStatus.Extracted
                && !string.IsNullOrWhiteSpace(x.ExtractedText))
            .OrderBy(x => x.UploadedAt))
        {
            if (builder.Length > 0) builder.AppendLine().AppendLine();
            var pages = document.PageCount.HasValue ? $", {document.PageCount} page{(document.PageCount == 1 ? "" : "s")}" : string.Empty;
            builder.AppendLine($"--- Attached brief: {document.FileName} ({document.Kind}{pages}) ---");
            builder.AppendLine(document.ExtractedText.Trim());
        }
        return builder.ToString().Trim();
    }
}
