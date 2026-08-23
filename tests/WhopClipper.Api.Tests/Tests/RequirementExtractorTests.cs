using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class RequirementExtractorTests
{
    [Fact]
    public void ExtractsPlatformsDurationAndPublishingTokens()
    {
        var result = new HeuristicRequirementExtractor().Extract("Post to TikTok and Instagram Reels. Clips must be 20-45 seconds, 9:16, with #Acme and @brand.");

        Assert.Contains(result, x => x.Key == "platform" && x.Value == "TikTok");
        Assert.Contains(result, x => x.Key == "platform" && x.Value == "InstagramReels");
        Assert.Contains(result, x => x.Key == "duration" && x.Value == "20-45 seconds");
        Assert.Contains(result, x => x.Key == "hashtag" && x.Value == "#Acme");
        Assert.Contains(result, x => x.Key == "mention" && x.Value == "@brand.");
    }

    [Theory]
    [InlineData("{\"all\":\"userOverride\"}", true)]
    [InlineData("{\"duration\":\"extracted\",\"hashtags\":\"userOverride\"}", true)]
    [InlineData("{\"duration\":\"default\",\"resolution\":\"default\"}", false)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    public void RecognizesProfilesThatCarryAUserOverride(string valueOrigins, bool expected)
    {
        var profile = new PlatformProfile { ValueOriginsJson = valueOrigins };

        var overridden = JsonDefaults.Parse(profile.ValueOriginsJson, new Dictionary<string, string>())
            .Any(origin => origin.Value.Equals("userOverride", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, overridden);
    }

    [Theory]
    [InlineData("Platforms: TikTok, Youtube &Instagram", "TikTok", "YouTubeShorts", "InstagramReels")]
    [InlineData("Post on YouTube Shorts only.", "YouTubeShorts")]
    [InlineData("Upload to YT and IG Reels.", "YouTubeShorts", "InstagramReels")]
    [InlineData("Clip for tik-tok.", "TikTok")]
    [InlineData("Share on X, TikTok and Twitter.", "TikTok", "X")]
    public void DetectsPlatformsNamedWithoutTheirShortFormSuffix(string instructions, params string[] expected)
    {
        var platforms = new HeuristicRequirementExtractor().Extract(instructions)
            .Where(x => x.Key == "platform" && x.Confidence > .5).Select(x => x.Value).ToArray();

        Assert.Equal(expected.Order(), platforms.Order());
    }

    [Theory]
    [InlineData("Render at 1920 x 1080 for every platform.")]
    [InlineData("Deliver 2 x clips per week.")]
    public void DoesNotTreatAStrayLetterXAsThePlatformX(string instructions)
    {
        var platforms = new HeuristicRequirementExtractor().Extract(instructions)
            .Where(x => x.Key == "platform" && x.Confidence > .5).Select(x => x.Value);

        Assert.DoesNotContain("X", platforms);
    }

    [Fact]
    public void MarksDefaultsForReviewWhenInstructionsAreSparse()
    {
        var result = new HeuristicRequirementExtractor().Extract("Make it engaging.");
        Assert.Contains(result, x => x.Key == "platform" && x.NeedsReview);
        Assert.Contains(result, x => x.Key == "duration" && x.NeedsReview);
    }

    /// <summary>
    /// A brief stating only a floor previously matched no pattern and fell back to the
    /// application default of 15-60s - a stricter rule than the client asked for, which would
    /// reject clips they would have accepted.
    /// </summary>
    [Fact]
    public void ReadsAMinimumOnlyDurationInsteadOfFallingBackToTheDefault()
    {
        var items = new HeuristicRequirementExtractor()
            .Extract("Format: Minimum 10 seconds, English language only.");
        var duration = Assert.Single(items, x => x.Key == "duration");

        Assert.Equal("at least 10 seconds", duration.Value);
        Assert.False(duration.NeedsReview);
        Assert.True(duration.Confidence > 0.5);
    }

    [Fact]
    public void CombinesASeparateMinimumAndMaximumIntoOneRange()
    {
        var items = new HeuristicRequirementExtractor()
            .Extract("Clips must be a minimum of 10 seconds and a maximum of 45 seconds.");
        var duration = Assert.Single(items, x => x.Key == "duration");

        Assert.Equal("10-45 seconds", duration.Value);
    }}
