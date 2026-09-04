using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// The copy that goes with a finished clip when it is uploaded by hand. The hashtag is the part
/// that matters most: campaigns reject clips for missing one, and by the time anyone is filling in
/// an upload form the render is already done.
/// </summary>
public sealed class ClipCaptionComposerTests
{
    private static EditPlan Plan(string hook, string? cta = null, string outputFileName = "clip.mp4") => new()
    {
        Platform = PlatformKind.TikTok,
        OutputFileName = outputFileName,
        ExtraSettingsJson = JsonSerializer.Serialize(new EditPlanExtrasDto(
            "SmartCrop", cta, null, -12, [],
            [new OverlayDto(hook, 0, 3.2)]), JsonDefaults.Serializer),
    };

    private static PlatformProfile Profile(string[]? hashtags = null, string[]? mentions = null,
        string caption = "", string? disclosure = null, string? cta = null) => new()
    {
        Platform = PlatformKind.TikTok,
        CaptionTemplate = caption,
        RequiredHashtagsJson = JsonSerializer.Serialize(hashtags ?? []),
        RequiredMentionsJson = JsonSerializer.Serialize(mentions ?? []),
        DisclosureText = disclosure,
        CallToAction = cta,
    };

    [Fact]
    public void TakesTheTitleFromTheHookBurnedOntoTheClip()
    {
        var copy = ClipCaptionComposer.Compose(
            Plan("He beat him 1v1... with a torn ACL"), Profile(), "whatever.mp4");
        Assert.Equal("He beat him 1v1... with a torn ACL", copy.Title);
    }

    [Fact]
    public void CarriesTheCampaignsRequiredHashtagIntoTheCaption()
    {
        var copy = ClipCaptionComposer.Compose(
            Plan("Made $33K in 2 weeks"), Profile(hashtags: ["#DoubleCoverage"]), "x.mp4");
        Assert.Contains("Made $33K in 2 weeks", copy.Caption);
        Assert.Contains("#DoubleCoverage", copy.Caption);
    }

    [Fact]
    public void PutsMentionsAndHashtagsOnOneLineAtTheEnd()
    {
        var copy = ClipCaptionComposer.Compose(
            Plan("Hook"), Profile(hashtags: ["#One", "#Two"], mentions: ["@doublecoverage"]), "x.mp4");
        // Mentions lead, then the campaign's tags in order. The platform tag and anything read out
        // of the hook follow, so this checks the opening of the line rather than the whole of it.
        var lastLine = copy.Caption.Split('\n')[^1].Trim();
        Assert.StartsWith("@doublecoverage #One #Two", lastLine);
        Assert.DoesNotContain("\n", lastLine);
    }

    [Fact]
    public void KeepsTheDisclosureAboveTheTags()
    {
        var copy = ClipCaptionComposer.Compose(
            Plan("Hook"), Profile(hashtags: ["#Ad"], disclosure: "Paid partnership."), "x.mp4");
        // Buried under a wall of hashtags is exactly how a disclosure stops counting as one.
        Assert.True(copy.Caption.IndexOf("Paid partnership.", StringComparison.Ordinal)
            < copy.Caption.IndexOf("#Ad", StringComparison.Ordinal));
    }

    [Fact]
    public void PrefersThePlansOwnCallToActionOverTheProfileDefault()
    {
        var copy = ClipCaptionComposer.Compose(
            Plan("Hook", cta: "Full episode in bio"), Profile(cta: "Generic profile CTA"), "x.mp4");
        Assert.Contains("Full episode in bio", copy.Caption);
        Assert.DoesNotContain("Generic profile CTA", copy.Caption);
    }

    [Fact]
    public void StripsTheQuotesAroundAQuotedHook()
    {
        var copy = ClipCaptionComposer.Compose(
            Plan("“You're the Patrick Mahomes of streaming”"), Profile(), "x.mp4");
        Assert.Equal("You're the Patrick Mahomes of streaming", copy.Title);
    }

    /// <summary>Renders are named "&lt;slug&gt;-&lt;job id&gt;.mp4"; the id is not a title.</summary>
    [Fact]
    public void FallsBackToAReadableNameWhenThereIsNoHook()
    {
        var copy = ClipCaptionComposer.Compose(null, Profile(hashtags: ["#DoubleCoverage"]),
            "Made-$33K-in-2-weeks-88ba7ca3aa404374bf149b33c8f09811.mp4");
        Assert.Equal("Made $33K in 2 weeks", copy.Title);
        Assert.Contains("#DoubleCoverage", copy.Caption);
    }

    [Fact]
    public void SurvivesAClipWithNoPlanAndNoProfileAtAll()
    {
        var copy = ClipCaptionComposer.Compose(null, null, "orphan-render.mp4");
        Assert.Equal("orphan render", copy.Title);
        Assert.Equal("orphan render", copy.Caption);
    }

    [Fact]
    public void ShortensATitleThatWouldNotFitAnUploadForm()
    {
        var copy = ClipCaptionComposer.Compose(Plan(new string('a', 240)), Profile(), "x.mp4");
        Assert.True(copy.Title.Length <= 101);
        Assert.EndsWith("…", copy.Title);
    }
}

/// <summary>
/// A brief's required hashtag is a floor, not a ceiling. Competing clips on the same campaign carry
/// the subject, the niche and the platform tag too, and the reach difference is the whole point.
/// </summary>
public sealed class ClipHashtagTests
{
    private static PlatformProfile Profile(PlatformKind platform, params string[] hashtags) => new()
    {
        Platform = platform,
        RequiredHashtagsJson = JsonSerializer.Serialize(hashtags),
        RequiredMentionsJson = "[]",
    };

    private static EditPlan Plan(PlatformKind platform) => new() { Platform = platform, ExtraSettingsJson = "{}" };

    [Fact]
    public void KeepsTheCampaignsRequiredTagFirst()
    {
        var tags = ClipCaptionComposer.HashtagsFor(Plan(PlatformKind.TikTok),
            Profile(PlatformKind.TikTok, "#DoubleCoverage", "#lacy"), "Some clip");
        // The brief writes it "#DoubleCoverage"; a reviewer checks the post against the brief.
        Assert.Equal("#DoubleCoverage", tags[0]);
    }

    [Theory]
    [InlineData(PlatformKind.YouTubeShorts, "#shorts")]
    [InlineData(PlatformKind.InstagramReels, "#reels")]
    [InlineData(PlatformKind.TikTok, "#fyp")]
    public void AddsTheTagThePlatformExpects(PlatformKind platform, string expected) =>
        Assert.Contains(expected, ClipCaptionComposer.HashtagsFor(Plan(platform), Profile(platform), "Clip"));

    [Fact]
    public void TakesNamesOutOfTheHook()
    {
        var tags = ClipCaptionComposer.HashtagsFor(Plan(PlatformKind.TikTok),
            Profile(PlatformKind.TikTok, "#DoubleCoverage"), "Why Lacy said NO to Mark Wahlberg's team");
        Assert.Contains("#lacy", tags);            // names lifted from a hook are lower-cased
        Assert.Contains("#markwahlberg", tags);
        // "Why" opens the sentence; gluing it on produced "#whylacy" before this was handled.
        Assert.DoesNotContain("#whylacy", tags);
    }

    /// <summary>
    /// Measured on a real batch: pulling ordinary words out of these hooks offered "#butt" and
    /// "#thong". Names are the only part of a hook safe to publish unreviewed.
    /// </summary>
    [Theory]
    [InlineData("We're all scratching our butt cracks and smelling our fingers")]
    [InlineData("He paid $4,000 for a picture in a thong... then got refunded")]
    [InlineData("My mom thinks you're a dumbass")]
    public void NeverInventsATagFromAnOrdinaryWord(string hook)
    {
        var subject = ClipCaptionComposer.SubjectTags(hook);
        Assert.Empty(subject);
    }

    [Fact]
    public void ReadsAnAcronymAsAName() =>
        Assert.Contains("ACL", ClipCaptionComposer.SubjectTags("He beat him 1v1... with a torn ACL"));

    [Fact]
    public void NormalisesWhateverTheUserTyped()
    {
        var tags = ClipCaptionComposer.HashtagsFor(Plan(PlatformKind.TikTok),
            Profile(PlatformKind.TikTok, "Double Coverage", "#FaZe", "  #streamer  "), "Clip");
        Assert.Contains("#DoubleCoverage", tags);   // spaces stripped, capitals kept
        Assert.Contains("#FaZe", tags);
        Assert.Contains("#streamer", tags);       // surrounding whitespace trimmed
    }

    [Fact]
    public void DropsDuplicatesHoweverTheyWereSpelled()
    {
        var tags = ClipCaptionComposer.HashtagsFor(Plan(PlatformKind.TikTok),
            Profile(PlatformKind.TikTok, "#Clix", "#clix", "clix"), "He trolled in Clix's chat");
        // Written three ways, kept once, in the casing it was first written with.
        Assert.Single(tags, x => string.Equals(x, "#clix", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StopsBeforeTheTagListReadsAsSpam()
    {
        var many = Enumerable.Range(1, 30).Select(i => $"#tag{i}").ToArray();
        Assert.True(ClipCaptionComposer.HashtagsFor(Plan(PlatformKind.TikTok),
            Profile(PlatformKind.TikTok, many), "Clip").Count <= 12);
    }
}
