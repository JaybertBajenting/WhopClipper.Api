using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Endpoints;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class PodcastSplitEndpointTests
{
    /// <summary>
    /// Every campaign that existed before this style keeps cutting streamer clips; the podcast
    /// defaults are the positions measured on Double Coverage TV's own shorts.
    /// </summary>
    [Fact]
    public void AnUnsetStyleIsTheStreamerStyleWithTheMeasuredPodcastDefaults()
    {
        var style = new CampaignEditStyleDto();

        Assert.Equal(EditStyles.Streamer, style.Style);
        Assert.Equal(0.074, style.LogoRightMarginFraction);
        Assert.Equal(0.071, style.LogoTopMarginFraction);
        Assert.Equal(2.2, style.BrollShotSeconds);
        Assert.Empty(PodcastSplitEndpoints.ValidateStyle(style with { Style = EditStyles.PodcastSplit }));
    }

    [Theory]
    [InlineData("Arial,1")]       // a comma would start the next field of the ASS style line
    [InlineData("")]
    [InlineData("Font{\\b1}")]
    public void RejectsAFontNameThatWouldBreakTheCaptionStyleLine(string font)
    {
        Assert.Contains("captionFont", PodcastSplitEndpoints.ValidateStyle(new CampaignEditStyleDto(CaptionFont: font)).Keys);
    }

    [Fact]
    public void RejectsAnUnknownStyle()
    {
        Assert.Contains("style", PodcastSplitEndpoints.ValidateStyle(new CampaignEditStyleDto("Karaoke")).Keys);
    }

    [Fact]
    public void StoriesMustBeRealPiecesFromOneDownloadableStretch()
    {
        var request = new ImportStoriesRequest(
        [
            new ImportedStoryDto("Fine", [new StoryPieceDto(100, 110), new StoryPieceDto(120, 130)]),
            new ImportedStoryDto("Reversed", [new StoryPieceDto(50, 40)]),
            new ImportedStoryDto("Too far apart", [new StoryPieceDto(0, 10), new StoryPieceDto(1000, 1010)]),
            new ImportedStoryDto("", [new StoryPieceDto(0, 10)]),
        ], Url: "https://www.youtube.com/watch?v=abcdefghijk");

        var errors = PodcastSplitEndpoints.ValidateStories(request);

        Assert.DoesNotContain("stories[0]", errors.Keys);
        Assert.Contains("stories[1]", errors.Keys);
        Assert.Contains("stories[2]", errors.Keys);
        Assert.Contains("stories[3]", errors.Keys);
    }

    [Fact]
    public void AQueuedStoryRunSurvivesARestart()
    {
        var original = new SourcePipelineJobPayload(Guid.NewGuid(), PlatformKind.TikTok, MaxClips: 2,
            DeleteSourceWhenDone: false, EditStyle: EditStyles.PodcastSplit,
            Stories: [new ImportedStoryDto("What Nate Diaz did with his first $15K", [new StoryPieceDto(478.3, 490.1)])]);

        var restored = JsonSerializer.Deserialize<SourcePipelineJobPayload>(
            JsonSerializer.Serialize(original, JsonDefaults.Serializer), JsonDefaults.Serializer)!;

        Assert.Equal(EditStyles.PodcastSplit, restored.EditStyle);
        var story = Assert.Single(restored.Stories!);
        Assert.Equal(478.3, story.Pieces[0].StartSeconds);
    }

    /// <summary>
    /// During a session limit every B-roll plan failed and the clips rendered from a title search, eleven
    /// with no footage at all. An outage has to fail the story instead; a bad plan may still fall back.
    /// </summary>
    [Theory]
    [InlineData("Claude is not available: You've hit your session limit · resets 6:50am (Asia/Manila)", true)]
    [InlineData("Claude failed (exit 1). rate_limit_error", true)]
    [InlineData("Claude returned nothing matching the schema. The CLI produced no output.", false)]
    public void AnAiOutageFailsTheStoryInsteadOfShippingTitleOnlyFootage(string message, bool outage)
    {
        Assert.Equal(outage, JobExecutionService.IsProviderOutage(new InvalidOperationException(message)));
    }

    [Fact]
    public void TheTitleFallbackSearchesWordsNotEmoji()
    {
        Assert.Equal("How Ohtani secretly saved his teammate’s family",
            JobExecutionService.SearchableTitle("How Ohtani secretly saved his teammate’s family 😳"));
        Assert.Equal("When MJ LT Golfed for $38K", JobExecutionService.SearchableTitle("When MJ & LT Golfed for $38K ⛳️"));
    }

    /// <summary>Three renders of one story share a file name prefix; each must list its own plan's footage.</summary>
    [Fact]
    public void ARenderIsMatchedToItsOwnPlanNotTheOldestWithTheSameTitle()
    {
        var oldest = new EditPlan { OutputFileName = "Teofimo's Chilling Warning to Ryan Garcia.mp4", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var middle = new EditPlan { OutputFileName = oldest.OutputFileName, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30) };
        var newest = new EditPlan { OutputFileName = oldest.OutputFileName, CreatedAt = DateTimeOffset.UtcNow };
        var plans = new List<EditPlan> { oldest, middle, newest };

        Assert.Same(middle, ApiEndpoints.MatchPlanForOutput(plans, $"Teofimo's Chilling Warning to Ryan Garcia-{middle.Id:N}"));
        Assert.Same(newest, ApiEndpoints.MatchPlanForOutput(plans, $"Teofimo's Chilling Warning to Ryan Garcia-{Guid.NewGuid():N}"));
    }

    /// <summary>Plans stored before the style existed carry no podcast-split block and must still load.</summary>
    [Fact]
    public void PlanExtrasFromBeforeTheStyleStillParse()
    {
        const string stored = """{"framing":"SmartCrop","ctaOverlayText":null,"subtitleStyle":"Bold White Centered Captions","musicDuckingDb":-12,"soundEffects":[],"overlays":[]}""";

        var extras = JsonDefaults.Parse<EditPlanExtrasDto?>(stored, null);

        Assert.NotNull(extras);
        Assert.Equal("SmartCrop", extras!.Framing);
        Assert.Null(extras.PodcastSplit);
    }
}
