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
        Assert.EndsWith("@doublecoverage #One #Two", copy.Caption);
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
