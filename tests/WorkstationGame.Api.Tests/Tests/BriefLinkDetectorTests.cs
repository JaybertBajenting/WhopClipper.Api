using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class BriefLinkDetectorTests
{
    private const string Brief = """
        PLOX MEDIA - FEXR Clipping Guidelines
        Campaign page: https://whop.com/plox-media-5248/exp_FnvXNrlx0NCla9/app/
        Source footage: https://www.youtube.com/watch?v=dQw4w9WgXcQ&utm_source=whop
        Backup VOD: https://www.twitch.tv/videos/123456789
        Reference clip (match this style): https://www.tiktok.com/@creator/video/7300000000000000000
        Brand kit: https://example.com/brand-kit.pdf
        Questions? https://discord.gg/abcdef
        """;

    [Fact]
    public void FindsEveryLinkInTheBriefOnce()
    {
        var links = new BriefLinkDetector().Detect(Brief, [], []);

        Assert.Equal(6, links.Count);
        Assert.All(links, link => Assert.Equal(1, link.Occurrences));
    }

    [Fact]
    public void SeparatesFootageFromCampaignAdminAndOtherLinks()
    {
        var links = new BriefLinkDetector().Detect(Brief, [], []).ToDictionary(x => x.Host, x => x.Kind);

        Assert.Equal("Media", links["youtube.com"]);
        Assert.Equal("Media", links["twitch.tv"]);
        Assert.Equal("Media", links["tiktok.com"]);
        Assert.Equal("Admin", links["whop.com"]);
        Assert.Equal("Admin", links["discord.gg"]);
        Assert.Equal("Other", links["example.com"]);
    }

    [Fact]
    public void MarksLinksThatAreAlreadyAddedSoTheyAreNotOfferedTwice()
    {
        var links = new BriefLinkDetector().Detect(Brief,
            ["https://youtube.com/watch?v=dQw4w9WgXcQ"],
            ["https://www.tiktok.com/@creator/video/7300000000000000000/"]);

        Assert.True(links.Single(x => x.Host == "youtube.com").AlreadySource);
        Assert.True(links.Single(x => x.Host == "tiktok.com").AlreadyReference);
        Assert.False(links.Single(x => x.Host == "twitch.tv").AlreadySource);
    }

    [Fact]
    public void KeepsTheSurroundingLineSoTheUserCanSeeWhatEachLinkWasFor()
    {
        var reference = new BriefLinkDetector().Detect(Brief, [], []).Single(x => x.Host == "tiktok.com");

        Assert.Contains("match this style", reference.Context);
    }

    [Theory]
    [InlineData("https://youtu.be/abc.", "https://youtu.be/abc")]
    [InlineData("(see https://vimeo.com/123)", "https://vimeo.com/123")]
    [InlineData("https://youtu.be/abc,", "https://youtu.be/abc")]
    public void TrimsPunctuationThatBelongsToTheSentence(string line, string expected)
    {
        Assert.Equal(expected, new BriefLinkDetector().Detect(line, [], []).Single().Url);
    }

    [Fact]
    public void TreatsTrackingParametersAndTrailingSlashesAsTheSameLink()
    {
        Assert.Equal(
            BriefLinkDetector.Normalize("https://www.youtube.com/watch?v=abc"),
            BriefLinkDetector.Normalize("https://youtube.com/watch?v=abc&utm_source=whop"));
        Assert.Equal(
            BriefLinkDetector.Normalize("https://vimeo.com/123"),
            BriefLinkDetector.Normalize("https://vimeo.com/123/"));
    }

    [Fact]
    public void OnlyTreatsAnXLinkAsMediaWhenItPointsAtAPost()
    {
        Assert.Equal("Media", BriefLinkDetector.Classify("https://x.com/creator/status/1790000000000000000"));
        Assert.Equal("Other", BriefLinkDetector.Classify("https://x.com/creator"));
    }

    [Theory]
    [InlineData("https://example.com/brand-kit.pdf")]
    [InlineData("https://whop.com/plox-media-5248/app/")]
    [InlineData("https://discord.gg/abcdef")]
    [InlineData("https://x.com/creator")]
    public void NonVideoLinksAreNeverClassifiedAsDownloadableFootage(string url)
    {
        // The import endpoint only accepts "Media", so anything else can never be fetched.
        Assert.NotEqual("Media", BriefLinkDetector.Classify(url));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("https://www.twitch.tv/videos/123")]
    [InlineData("https://www.tiktok.com/@c/video/7300000000000000000")]
    [InlineData("https://drive.google.com/file/d/abc/view")]
    public void ProvidedFootageAndReferenceClipsAreClassifiedAsMedia(string url)
    {
        Assert.Equal("Media", BriefLinkDetector.Classify(url));
    }

    [Fact]
    public void ReturnsNothingForABriefWithNoLinks()
    {
        Assert.Empty(new BriefLinkDetector().Detect("Post to TikTok. 20-45 seconds, 9:16.", [], []));
    }
}
