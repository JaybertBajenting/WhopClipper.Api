using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Data;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

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
