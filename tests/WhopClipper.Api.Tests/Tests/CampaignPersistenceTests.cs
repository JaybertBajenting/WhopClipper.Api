using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class ClipStylistTests
{
    private static TranscriptWord[] Sentence(params (double Start, double End, string Word)[] words) =>
        words.Select(w => new TranscriptWord(w.Start, w.End, w.Word)).ToArray();

    /// <summary>
    /// Ending on the widest pause ends a clip on hesitation. The One Piece batch shipped with
    /// ten clips cut while he was still talking - "um", "with", "yeah" - before this existed.
    /// </summary>
    [Fact]
    public void EndsOnAFinishedSentenceRatherThanTheWidestPause()
    {
        var words = Sentence(
            (0, .4, "People"), (.4, .8, "always"), (.8, 1.2, "say"),
            (1.2, 12.6, "..."),                      // filler to clear the minimum length
            (12.6, 13.0, "one"), (13.0, 13.4, "piece"), (13.4, 13.9, "is"),
            (13.9, 14.4, "long."),                   // a real ending, with a breath after it
            (15.2, 15.6, "So"), (15.6, 16.0, "anyway"));

        var (start, end) = ClipStylist.ChooseWindow(words, 0, 16.0);

        Assert.True(end - start is > 14.0 and < 15.2, $"ended at {end - start:0.##}s");
    }

    /// <summary>A pause is only an ending if the last word carries meaning.</summary>
    [Fact]
    public void WillNotEndOnADanglingFunctionWord()
    {
        var words = Sentence(
            (0, .4, "Some"), (.4, 12.8, "..."),
            (12.8, 13.2, "Who"),                     // long pause after, but mid-thought
            (14.6, 15.0, "cares."), (15.6, 16.0, "Next"));

        var (start, end) = ClipStylist.ChooseWindow(words, 0, 16.0);

        Assert.True(end - start > 13.4, $"ended on the dangling word at {end - start:0.##}s");
    }

    [Fact]
    public void NeverOpensPartWayThroughAWord()
    {
        var words = Sentence((9.0, 10.4, "halfspoken"), (10.6, 11.0, "clean"), (11.0, 20.0, "..."));

        var (start, _) = ClipStylist.ChooseWindow(words, 10.0, 24.0);

        Assert.True(start >= 10.3, $"opened at {start:0.##}s, inside the previous word");
    }
}

public sealed class SourcePipelinePayloadTests
{
    /// <summary>
    /// The pipeline is queued once per source and deletes each VOD as soon as its clips exist,
    /// so a clip bank can be worked through without ever holding two multi-gigabyte sources.
    /// The default has to stay "delete", or the disk fills on the third video.
    /// </summary>
    [Fact]
    public void DeletesTheSourceByDefaultSoAClipBankFitsOnDisk()
    {
        var payload = new SourcePipelineJobPayload(Guid.NewGuid());

        Assert.True(payload.DeleteSourceWhenDone);
        Assert.Equal(PlatformKind.TikTok, payload.Platform);
        Assert.Equal("SmartCrop", payload.Framing);
        Assert.Null(payload.HookYFraction);
    }

    /// <summary>
    /// A flat cap cannot transcribe a long stream: faster-whisper runs at roughly a quarter of
    /// realtime on CPU, so a 293 minute VOD died at the 30 minute limit having decoded most of
    /// it. The allowance has to grow with the recording.
    /// </summary>
    [Theory]
    [InlineData(0, 30)]          // nothing known about the media: keep the floor
    [InlineData(60 * 60, 60)]    // one hour of video
    [InlineData(293 * 60, 234)]  // the stream that timed out: 0.75x plus 15 minutes of slack
    public void GivesTranscriptionTimeProportionalToTheRecording(double mediaSeconds, int atLeastMinutes)
    {
        var allowed = JobExecutionService.TranscriptionTimeoutForTests(mediaSeconds, 30);

        Assert.True(allowed >= TimeSpan.FromMinutes(atLeastMinutes),
            $"{mediaSeconds}s of media only got {allowed.TotalMinutes:0} minutes.");
    }

    [Fact]
    public void RoundTripsThroughJsonSoAQueuedPipelineSurvivesARestart()
    {
        var original = new SourcePipelineJobPayload(Guid.NewGuid(), PlatformKind.YouTubeShorts,
            MaxClips: 5, DeleteSourceWhenDone: false, Framing: "CenterCrop", HookYFraction: 0.412);

        var restored = JsonSerializer.Deserialize<SourcePipelineJobPayload>(
            JsonSerializer.Serialize(original, JsonDefaults.Serializer), JsonDefaults.Serializer);

        Assert.Equal(original, restored);
    }
}

public sealed class CampaignPersistenceTests
{
    /// <summary>
    /// Npgsql rejects a DateTimeOffset whose offset is not zero when writing `timestamp with
    /// time zone`, which is what made every campaign with a deadline fail to save. The model
    /// converts on write, so no call site has to remember.
    /// </summary>
    [Fact]
    public void DateTimeOffsetPropertiesConvertToUtcOnWrite()
    {
        using var context = CreateContext();
        var property = context.Model.FindEntityType(typeof(Campaign))!.FindProperty(nameof(Campaign.Deadline))!;
        var converter = property.GetValueConverter();

        Assert.NotNull(converter);
        var local = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.FromHours(8));
        var stored = Assert.IsType<DateTimeOffset>(converter!.ConvertToProvider(local));

        Assert.Equal(TimeSpan.Zero, stored.Offset);
        Assert.Equal(local.UtcDateTime, stored.UtcDateTime);
    }

    [Fact]
    public void EveryDateTimeOffsetPropertyIsCovered()
    {
        using var context = CreateContext();
        var uncovered = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(property => property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                .Where(property => property.GetValueConverter() is null)
                .Select(property => $"{entity.ClrType.Name}.{property.Name}"))
            .ToArray();

        Assert.Empty(uncovered);
    }

    [Fact]
    public void PayoutIsStoredAsAnAmountAndAViewThreshold()
    {
        var campaign = new Campaign { Name = "Plox Media", PayoutAmount = 1m, PayoutPerViews = 1000 };
        var summary = campaign.ToSummary();

        Assert.Equal(1m, summary.PayoutAmount);
        Assert.Equal(1000, summary.PayoutPerViews);
        Assert.Null(summary.Deadline);
    }

    [Theory]
    [InlineData(null, 1000, true)]
    [InlineData(1, 1000, true)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 1000, false)]
    public void PayoutValidationRejectsImpossibleValues(int? amount, int? perViews, bool expectedValid)
    {
        var request = new CreateCampaignRequest("Plox Media", null, "Clipping", null, null,
            amount, perViews, null, null, "Post to TikTok within 30 days.", null);

        Assert.Equal(expectedValid, new CreateCampaignValidator().Validate(request).IsValid);
    }

    [Fact]
    public void ACampaignWithoutADeadlineIsValid()
    {
        var request = new CreateCampaignRequest("Plox Media", null, "Clipping", null, null,
            1, 1000, null, null, "Post to TikTok. No deadline for this one.", null);

        Assert.True(new CreateCampaignValidator().Validate(request).IsValid);
    }

    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=design_time_only;Username=none;Password=none")
        .Options);
}
