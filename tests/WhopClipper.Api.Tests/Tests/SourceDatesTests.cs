using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Endpoints;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests.Tests;

public sealed class SourceDatesTests
{
    [Fact]
    public void ThePublishDayIsReadFromWhateverTheInspectorWrote()
    {
        Assert.Equal(new DateOnly(2026, 9, 15), SourceDates.PublishedOn("""{"id":"2Hr-6Ku3Dho","upload_date":"20260915"}"""));
        // Some sites give only a moment in time.
        Assert.Equal(new DateOnly(2025, 1, 2), SourceDates.PublishedOn("""{"timestamp":1735819200}"""));
        // Missing, unreadable, or not an object: no date, and never an error.
        Assert.Null(SourceDates.PublishedOn("""{"upload_date":"NA"}"""));
        Assert.Null(SourceDates.PublishedOn("{}"));
        Assert.Null(SourceDates.PublishedOn("not json"));
        Assert.Null(SourceDates.PublishedOn("[1,2]"));
        Assert.Null(SourceDates.PublishedOn(null));
    }

    [Fact]
    public void AddingTheDayKeepsTheRestAndRemembersTheLookup()
    {
        var metadata = SourceDates.WithUploadDate("""{"driveId":"abc","contentType":"video/mp4"}""", "20260915");
        using var document = JsonDocument.Parse(metadata);
        Assert.Equal("abc", document.RootElement.GetProperty("driveId").GetString());
        Assert.Equal("video/mp4", document.RootElement.GetProperty("contentType").GetString());
        Assert.Equal(new DateOnly(2026, 9, 15), SourceDates.PublishedOn(metadata));
        Assert.True(SourceDates.WasChecked(metadata));

        // A video with no date is looked up once, not on every start.
        var none = SourceDates.WithUploadDate("{}", "NA");
        Assert.Null(SourceDates.PublishedOn(none));
        Assert.True(SourceDates.WasChecked(none));
        Assert.False(SourceDates.WasChecked("{}"));
        // Metadata that was never an object starts afresh rather than failing.
        Assert.Equal(new DateOnly(2024, 3, 1), SourceDates.PublishedOn(SourceDates.WithUploadDate("garbage", "20240301")));
    }

    [Fact]
    public void SourcesAreListedNewestVideoFirst()
    {
        SourceAsset Source(string name, string? day, int addedDaysAgo) => new()
        {
            Url = name, Title = name, CreatedAt = DateTimeOffset.UtcNow.AddDays(-addedDaysAgo),
            MetadataJson = day is null ? "{}" : SourceDates.WithUploadDate("{}", day),
        };
        var order = SourceDates.NewestFirst([
            Source("Paris 2025", "20250410", 0), Source("NYC 2026", "20260915", 5), Source("no date, added today", null, 0),
            Source("Ibiza 2026", "20260801", 1), Source("no date, added last week", null, 7),
        ]).Select(x => x.Title);

        Assert.Equal(["NYC 2026", "Ibiza 2026", "Paris 2025", "no date, added today", "no date, added last week"], order);
    }

    [Fact]
    public void YouTubeThrottlingIsToldApartFromMissingCaptions()
    {
        // What five vlogs that all had captions failed with on 2026-10-03.
        Assert.True(YouTubeCaptions.IsRateLimited(
            "ERROR: Unable to download video subtitles for 'en-orig': HTTP Error 429: Too Many Requests"));
        Assert.False(YouTubeCaptions.IsRateLimited("WARNING: There are no subtitles for the requested languages"));
        Assert.False(YouTubeCaptions.IsRateLimited(null));
    }

    [Fact]
    public void ClipsAreListedByTheirVideosDateThenByRender()
    {
        RenderedClipDto Clip(string name, DateOnly? day, int renderedHoursAgo) => new(name, name, "tiktok", 1,
            DateTimeOffset.UtcNow.AddHours(-renderedHoursAgo), null, null, null, SourcePublishedOn: day);
        var order = ApiEndpoints.NewestSourceFirst([
            Clip("old vlog, rendered now", new DateOnly(2025, 4, 10), 0),
            Clip("new vlog, rendered yesterday", new DateOnly(2026, 9, 15), 24),
            Clip("new vlog, rendered today", new DateOnly(2026, 9, 15), 1),
            Clip("unknown video", null, 0),
        ]).Select(x => x.FileName);

        Assert.Equal(["new vlog, rendered today", "new vlog, rendered yesterday", "old vlog, rendered now", "unknown video"], order);
    }
}
