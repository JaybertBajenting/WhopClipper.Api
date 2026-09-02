using System.Net;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// The fetcher retrieves a URL a user typed, from the user's own machine. That machine is also
/// running this API on loopback with every campaign behind it, so "fetch whatever you are given"
/// would be a way to read the workspace back out through a brief.
/// </summary>
public sealed class BriefPageFetcherGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5099/api/campaigns")]  // the app's own API
    [InlineData("http://localhost:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]  // cloud metadata
    [InlineData("http://10.0.0.5/brief")]
    [InlineData("http://192.168.1.10/brief")]
    [InlineData("http://172.16.4.4/brief")]
    [InlineData("http://[::1]/brief")]
    public void RefusesAddressesOnThisMachineOrThePrivateNetwork(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CampaignPageFetcher.ParseAndGuard(url));
        Assert.Contains("private network", error.Message);
    }

    [Theory]
    [InlineData("file:///C:/Users/secrets.txt")]
    [InlineData("ftp://example.com/brief")]
    public void RefusesSchemesThatAreNotWebAddresses(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CampaignPageFetcher.ParseAndGuard(url));
        Assert.Contains("http", error.Message);
    }

    [Fact]
    public void RefusesTextThatIsNotAnAddressAtAll()
    {
        Assert.Throws<InvalidOperationException>(() => CampaignPageFetcher.ParseAndGuard("not a url"));
        Assert.Throws<InvalidOperationException>(() => CampaignPageFetcher.ParseAndGuard("   "));
    }

    [Fact]
    public void AcceptsAPublicCampaignPage()
    {
        var uri = CampaignPageFetcher.ParseAndGuard("https://reachclipping.com/Lacy#resources");
        Assert.Equal("reachclipping.com", uri.Host);
    }

    /// <summary>
    /// The classifier itself, including the ranges a redirect would land on. The fetcher re-runs
    /// this on every hop, so a public address that redirects inward is caught at the second hop
    /// rather than followed.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.255.255.254", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("104.18.32.7", false)]
    [InlineData("172.32.0.1", false)]   // just outside the private block
    [InlineData("172.15.0.1", false)]
    public void ClassifiesAddressesTheWayARedirectCheckNeeds(string address, bool expected) =>
        Assert.Equal(expected, CampaignPageFetcher.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void NamesTheDocumentAfterThePageSoTheListSaysWhereItCameFrom() =>
        Assert.Equal("reachclipping.com/Lacy",
            CampaignPageFetcher.LabelFor(new Uri("https://reachclipping.com/Lacy#resources")));
}

/// <summary>
/// Reading the markup instead of a screenshot is the whole design, and these are the properties
/// that make it worth doing. Losing any one of them would make a screenshot just as good.
/// </summary>
public sealed class BriefPageExtractionTests
{
    /// <summary>
    /// A link whose visible text is a label keeps its address. This is the case that decides the
    /// design: on a real campaign page the footage sits behind "Podcast Link", and a Drive folder
    /// id mixing 1, l and I could never be recovered from pixels.
    /// </summary>
    [Fact]
    public void KeepsTheAddressOfALinkWhoseTextIsOnlyALabel()
    {
        const string folder = "https://drive.google.com/drive/folders/1WdlIPICk2UTXHItCq4UA7nfBVIoKzDDJ?usp=sharing";
        var text = DocumentTextExtractor.ExtractHtml(
            $"<html><body><li>Podcast Link:<a href=\"{folder}\">Podcast Link</a></li></body></html>").Text;

        Assert.Contains(folder, text);
    }

    [Fact]
    public void DoesNotPrintTheAddressTwiceWhenTheLinkTextAlreadyIsTheAddress()
    {
        const string url = "https://drive.google.com/file/d/1oHyKzTuvpooobQsCgeIevXZf-VEI0xGN/view";
        var text = DocumentTextExtractor.ExtractHtml(
            $"<html><body><p><a href=\"{url}\">{url}</a></p></body></html>").Text;

        Assert.Equal(1, text.Split(url).Length - 1);
    }

    /// <summary>
    /// The heuristic extractor reads line by line, so requirements have to arrive one per line or
    /// a whole list collapses into a single unmatchable run of text.
    /// </summary>
    [Fact]
    public void PutsEachRequirementOnItsOwnLine()
    {
        var text = DocumentTextExtractor.ExtractHtml(
            "<html><body><ul>"
            + "<li>Videos can only be posted on Instagram Reels, TikTok, and YouTube Shorts.</li>"
            + "<li>Your captions must use the hashtag: #DoubleCoverage</li>"
            + "<li>AI-generated videos are not allowed.</li>"
            + "</ul></body></html>").Text;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        Assert.Contains("Your captions must use the hashtag: #DoubleCoverage", lines);
        Assert.Contains("AI-generated videos are not allowed.", lines);
    }

    /// <summary>A minified bundle would dwarf the brief and reach the AI as though a client wrote it.</summary>
    [Fact]
    public void LeavesOutScriptAndStyleSource()
    {
        var text = DocumentTextExtractor.ExtractHtml(
            "<html><head><style>.a{color:red}</style></head><body>"
            + "<script>var secret='alert-me';window.x=1</script>"
            + "<p>Videos must have the watermark visible.</p></body></html>").Text;

        Assert.DoesNotContain("alert-me", text);
        Assert.DoesNotContain("color:red", text);
        Assert.Contains("watermark", text);
    }

    [Fact]
    public void SurvivesMarkupThatIsNotWellFormed()
    {
        var text = DocumentTextExtractor.ExtractHtml("<div><p>Minimum 10 seconds<div>Unclosed<span>").Text;
        Assert.Contains("Minimum 10 seconds", text);
    }

    [Fact]
    public void ReturnsNothingRatherThanThrowingOnAnEmptyPage() =>
        Assert.Equal(string.Empty, DocumentTextExtractor.ExtractHtml("").Text);

    /// <summary>
    /// End to end over the shape of a real campaign page: what the extractor produces has to be
    /// something the untouched downstream services can already act on.
    /// </summary>
    [Fact]
    public void ProducesTextTheExistingExtractorAndLinkDetectorCanAlreadyRead()
    {
        const string folder = "https://drive.google.com/drive/folders/1WdlIPICk2UTXHItCq4UA7nfBVIoKzDDJ?usp=sharing";
        var text = DocumentTextExtractor.ExtractHtml(
            "<html><body><h2>REQUIREMENTS</h2><ul>"
            + $"<li>Videos must be from the episode. Podcast Link:<a href=\"{folder}\">here</a></li>"
            + "<li>Videos can only be posted on Instagram Reels, TikTok, and YouTube Shorts.</li>"
            + "<li>Videos MUST have the Podcast watermark clearly visible.</li>"
            + "<li>Your captions must use the hashtag: #DoubleCoverage</li>"
            + "</ul></body></html>").Text;

        var requirements = new HeuristicRequirementExtractor().Extract(text);
        Assert.Contains(requirements, x => x.Key == "platform" && x.Value == "TikTok");
        Assert.Contains(requirements, x => x.Key == "platform" && x.Value == "InstagramReels");
        Assert.Contains(requirements, x => x.Key == "platform" && x.Value == "YouTubeShorts");
        Assert.Contains(requirements, x => x.Key == "hashtag" && x.Value.Contains("#DoubleCoverage"));
        Assert.Contains(requirements, x => x.Key == "branding");

        // The link detector already treats drive.google.com as media, so the footage is offered
        // for import with no change to it at all.
        var links = new BriefLinkDetector().Detect(text, [], []);
        var drive = Assert.Single(links, x => x.Url.StartsWith("https://drive.google.com"));
        Assert.Equal("Media", drive.Kind);
        Assert.Equal(folder, drive.Url);
    }
}
