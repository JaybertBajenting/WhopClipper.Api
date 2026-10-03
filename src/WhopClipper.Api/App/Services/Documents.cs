using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
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
            CampaignDocumentKind.Html => ExtractHtml(await File.ReadAllTextAsync(fullPath, cancellationToken)),
            _ => new DocumentTextResult(Normalize(await File.ReadAllTextAsync(fullPath, cancellationToken)), null)
        };
    }

    /// <summary>
    /// Reads a campaign page the way the brief needs it, which is not the same as how it reads on
    /// screen. Two differences carry the whole value of taking the markup over a screenshot:
    /// every link keeps its exact <c>href</c>, and the page arrives whole rather than only the part
    /// that happened to be scrolled into view.
    /// </summary>
    internal static DocumentTextResult ExtractHtml(string html)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html ?? string.Empty);
        var body = (INode?)document.Body ?? document.DocumentElement;
        if (body is null) return new DocumentTextResult(string.Empty, null);
        var builder = new StringBuilder();
        Walk(body, builder);
        return new DocumentTextResult(Normalize(builder.ToString()), null);
    }

    /// <summary>Block elements that should start a new line, so one list item stays one requirement.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "hr", "li", "main", "nav", "ol", "p", "pre", "section", "table", "td", "th", "tr", "ul",
    };

    private static void Walk(INode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                builder.Append(text.Data);
                continue;
            }
            if (child is not IElement element) continue;

            // Script and style hold source code, not brief text. Left in, a minified bundle would
            // dwarf the requirements and be handed to the AI as though the client had written it.
            if (element.TagName is "SCRIPT" or "STYLE" or "NOSCRIPT" or "TEMPLATE" or "SVG") continue;

            var block = BlockTags.Contains(element.TagName);
            if (block) builder.Append('\n');
            Walk(element, builder);

            // The visible text is often a label - "Podcast Link" - with the address only in the
            // attribute. Dropping it would lose the exact thing that makes reading markup better
            // than reading pixels. Skipped when the label already is the address, which is how
            // this page happens to render, so the URL is not printed twice.
            if (element is IHtmlAnchorElement anchor)
            {
                var href = anchor.GetAttribute("href")?.Trim();
                if (!string.IsNullOrEmpty(href)
                    && href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    && !anchor.TextContent.Contains(href, StringComparison.OrdinalIgnoreCase))
                    builder.Append(" <").Append(href).Append('>');
            }
            if (block) builder.Append('\n');
        }
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

public sealed record FetchedPage(string Url, string Html, string Label);

/// <summary>
/// Fetches a campaign brief that is published as a web page rather than sent as a file. The result
/// is inert data, never instructions: anyone can put "ignore your instructions" on a public page,
/// and this text goes on to be part of an AI prompt.
/// </summary>
public sealed class CampaignPageFetcher(IHttpClientFactory factory, ILogger<CampaignPageFetcher> logger)
{
    public const string ClientName = "BriefFetch";
    public const int MaxPageBytes = 5 * 1024 * 1024;
    private const int MaxRedirects = 5;

    public async Task<FetchedPage> FetchAsync(string requestedUrl, CancellationToken cancellationToken)
    {
        var target = ParseAndGuard(requestedUrl);
        var client = factory.CreateClient(ClientName);

        // Redirects are followed by hand so every hop is guarded. Letting HttpClient follow them
        // checks only the address the user typed, and a public URL that redirects inward would
        // walk straight past the guard.
        HttpResponseMessage? response = null;
        try
        {
            for (var hop = 0; ; hop++)
            {
                response?.Dispose();
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WhopClipper/1.0");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain");
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                        throw new InvalidOperationException("That address redirected too many times.");
                    target = ParseAndGuard(location.IsAbsoluteUri ? location.ToString() : new Uri(target, location).ToString());
                    continue;
                }
                break;
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"The page could not be read ({(int)response.StatusCode} {response.ReasonPhrase}).");

            // A brief is text. Without this a PDF or a video URL would be read as though it were
            // markup and stored as a wall of mojibake.
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"That address returned {(mediaType.Length == 0 ? "an unknown type" : mediaType)}, not a web page. "
                    + "Upload a file instead if the brief is a document.");
            if (response.Content.Headers.ContentLength is > MaxPageBytes)
                throw new InvalidOperationException("That page is larger than 5 MB.");

            var html = await ReadCappedAsync(response, cancellationToken);
            logger.LogInformation("Fetched campaign brief page {Url} ({Bytes} bytes)", target, html.Length);
            return new FetchedPage(target.ToString(), html, LabelFor(target));
        }
        finally { response?.Dispose(); }
    }

    /// <summary>Reads at most <see cref="MaxPageBytes"/> so a server that lies about its length cannot fill the disk.</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxPageBytes)
                throw new InvalidOperationException("That page is larger than 5 MB.");
            buffer.Write(chunk, 0, read);
        }
        var encoding = response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset
            ? TryEncoding(charset) : Encoding.UTF8;
        return encoding.GetString(buffer.ToArray());
    }

    private static Encoding TryEncoding(string charset)
    {
        try { return Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    /// <summary>Host and path, so the document list reads "reachclipping.com/Lacy" and not a GUID.</summary>
    internal static string LabelFor(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        return (uri.Host + path) is { Length: > 0 } label && label.Length <= 120 ? label : uri.Host;
    }

    /// <summary>
    /// Rejects anything that is not a public http(s) address. This runs on the user's own machine,
    /// where the app's API is listening on loopback with every campaign behind it - a fetcher that
    /// will retrieve 127.0.0.1 is a way to read that back out through a brief. Cloud metadata
    /// endpoints (169.254.169.254) are blocked for the same reason.
    /// </summary>
    internal static Uri ParseAndGuard(string requestedUrl)
    {
        if (string.IsNullOrWhiteSpace(requestedUrl))
            throw new InvalidOperationException("Enter the address of the campaign page.");
        if (!Uri.TryCreate(requestedUrl.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("That is not a valid web address.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only http and https addresses can be fetched.");

        var addresses = ResolveAddresses(uri.DnsSafeHost);
        if (addresses.Count == 0)
            throw new InvalidOperationException($"'{uri.Host}' could not be resolved.");
        // Every address, not just the first: a host that answers with one public and one private
        // address would otherwise be a way through.
        if (addresses.Any(IsPrivate))
            throw new InvalidOperationException(
                $"'{uri.Host}' resolves to an address on this machine or private network, which cannot be fetched.");
        return uri;
    }

    private static List<IPAddress> ResolveAddresses(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        try { return [.. Dns.GetHostAddresses(host)]; }
        catch (SocketException) { return []; }
        catch (ArgumentException) { return []; }
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            if (address.IsIPv4MappedToIPv6) return IsPrivate(address.MapToIPv4());
            var v6 = address.GetAddressBytes();
            return (v6[0] & 0xFE) == 0xFC || address.Equals(IPAddress.IPv6Any); // unique-local fc00::/7
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 or 10 or 127 => true,                              // this-network, private, loopback
            169 => bytes[1] == 254,                              // link-local, incl. cloud metadata
            172 => bytes[1] >= 16 && bytes[1] <= 31,             // private
            192 => bytes[1] == 168 || (bytes[1] == 0 && bytes[2] == 2),
            _ => bytes[0] >= 224,                                // multicast and reserved
        };
    }
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

    /// <summary>
    /// Stores a fetched campaign page as a document, so a brief published on the web is handled
    /// exactly like one that was emailed as a PDF - same list, same include-in-extraction toggle,
    /// same delete. The raw markup is kept rather than only the extracted text: the text is a lossy
    /// derivative, and when a rule is later disputed the bytes actually served are the evidence.
    /// </summary>
    public async Task<DocumentUploadResult> StoreFetchedPageAsync(Guid campaignId, FetchedPage page,
        CancellationToken cancellationToken)
    {
        var directory = workspace.ResolveRelative(Path.Combine("campaigns", campaignId.ToString("N"), "campaign", "documents"));
        Directory.CreateDirectory(directory);
        var safeName = WorkspaceService.SafeFileName(page.Label + ".html", "brief-page");
        var fullPath = Path.Combine(directory, $"{Guid.NewGuid():N}-{safeName}");
        WorkspaceService.EnsureInside(directory, fullPath);
        await File.WriteAllTextAsync(fullPath, page.Html, new UTF8Encoding(false), cancellationToken);

        try
        {
            var document = new CampaignDocument
            {
                CampaignId = campaignId,
                // The address, not the stored filename: the list should say where this came from.
                FileName = page.Label,
                RelativePath = workspace.ToRelative(fullPath),
                Kind = CampaignDocumentKind.Html,
                SizeBytes = new FileInfo(fullPath).Length,
                ContentHash = await WorkspaceService.HashFileAsync(fullPath, cancellationToken),
            };
            string? warning = null;
            try
            {
                var extracted = await extractor.ExtractAsync(fullPath, CampaignDocumentKind.Html, cancellationToken);
                document.ExtractedText = extracted.Text;
                document.ExtractedCharacterCount = extracted.Text.Length;
                document.ExtractionStatus = DocumentExtractionStatus.Extracted;
                if (extracted.Text.Length == 0)
                {
                    document.ExtractionStatus = DocumentExtractionStatus.Failed;
                    document.ExtractionError = "The page had no readable text. It may build its content with JavaScript, "
                        + "in which case paste the brief text instead.";
                    warning = document.ExtractionError;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Text extraction failed for fetched campaign page {Page}", page.Label);
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
