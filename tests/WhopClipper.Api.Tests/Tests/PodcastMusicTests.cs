using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Services;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Endpoints;

namespace WhopClipper.Api.Tests;

public sealed class PodcastMusicTests
{
    [Theory]
    [InlineData(null, null, 0d)]
    [InlineData(null, 131d, 131d)]
    [InlineData(42d, 131d, 42d)]
    [InlineData(0d, 131d, 0d)]
    public void PreviewAndRenderResolveTheSameSongEntry(double? chosen, double? library, double expected)
        => Assert.Equal(expected, PodcastMusic.ResolveStart(chosen, library));

    [Fact]
    public void EntryNearTheEndIsPreservedInsteadOfSilentlyBackingUp()
        => Assert.Equal(131, PodcastMusic.ResolveStart(131, 12, 135));

    [Theory]
    [InlineData(101, 95.314, true)]
    [InlineData(170, 139.687, true)]
    [InlineData(6, 13, true)]
    [InlineData(131, 131, false)]
    public void DetectsExistingAffectedFilesEvenBeforePendingFlagExisted(double selected, double baked, bool needsRender)
    {
        var args = JsonSerializer.Serialize(new[] { "-ss", "900", "-i", "span.mp4", "-ss",
            baked.ToString(System.Globalization.CultureInfo.InvariantCulture), "-stream_loop", "-1", "-i", "song.mp3",
            "-filter_complex", "[1:a]atrim=0:40,asetpts=PTS-STARTPTS,volume=0.2[musicbase]" });
        Assert.Equal(needsRender, PodcastMusic.DiffersFromRenderedFile(args, "song.mp3", selected));
        Assert.True(PodcastMusic.DiffersFromRenderedFile(args, "different-song.mp3", selected));
        Assert.True(PodcastMusic.DiffersFromRenderedFile(args, null, selected));
    }

    [Fact]
    public void NewFilterBasedEntryMatchesTheSelectedSegment()
    {
        var args = JsonSerializer.Serialize(new[] { "-ss", "900", "-i", "span.mp4", "-stream_loop", "-1", "-t", "150",
            "-i", "song.mp3", "-filter_complex", "[1:a]atrim=start=131:duration=19,asetpts=PTS-STARTPTS,volume=0.2[musicbase]" });
        Assert.False(PodcastMusic.DiffersFromRenderedFile(args, "song.mp3", 131));
        Assert.True(PodcastMusic.DiffersFromRenderedFile(args, "song.mp3", 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(135)]
    [InlineData(136)]
    public void InvalidEntriesAreRejectedInsteadOfMovingToAnotherPartOfTheSong(double start)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PodcastMusic.ResolveStart(start, 12, 135));

    [Fact]
    public void CustomSongEntrySurvivesAQueuedRerenderAndRestart()
    {
        var story = new ImportedStoryDto("Same highlight", [new StoryPieceDto(10, 30)],
            MusicAssetId: Guid.NewGuid(), VoiceVolume: 1.25, MusicVolume: 0.18,
            SupersedesEditPlanId: Guid.NewGuid(), MusicStartSeconds: 42.75);
        var restored = JsonSerializer.Deserialize<ImportedStoryDto>(
            JsonSerializer.Serialize(story, JsonDefaults.Serializer), JsonDefaults.Serializer)!;
        Assert.Equal(42.75, restored.MusicStartSeconds);
        Assert.Equal(story.MusicAssetId, restored.MusicAssetId);
        Assert.Equal(story.VoiceVolume, restored.VoiceVolume);
        Assert.Equal(story.MusicVolume, restored.MusicVolume);
    }

    [Fact]
    public void PendingSongChangeSurvivesClosingTheReviewDialogAndRestarting()
    {
        var extras = new EditPlanExtrasDto("PodcastSplit", null, "Seam", -10, [], [],
            MusicStartSeconds: 37, MusicNeedsRender: true);
        var restored = JsonSerializer.Deserialize<EditPlanExtrasDto>(
            JsonSerializer.Serialize(extras, JsonDefaults.Serializer), JsonDefaults.Serializer)!;
        Assert.True(restored.MusicNeedsRender);
        Assert.Equal(37, restored.MusicStartSeconds);
    }

    [Fact]
    public void MusicOnlyEditClonesTheExistingVisualPlanWithoutReselectingFootageOrCaptions()
    {
        var split = new PodcastSplitPlanDto("span.mp4", 1920, 1080, [], [], [], [], [], [], 900, "Original story");
        var extras = new EditPlanExtrasDto("PodcastSplit", null, "Seam", -10, [], [],
            PodcastSplit: split, MusicStartSeconds: 131, VoiceVolume: 1.5, Caption: "Written caption");
        var original = new EditPlan { CampaignId = Guid.NewGuid(), SourceAssetId = Guid.NewGuid(),
            StartSeconds = 900, EndSeconds = 930, OutputFileName = "original.mp4", MusicAssetId = Guid.NewGuid(),
            ExtraSettingsJson = JsonSerializer.Serialize(extras, JsonDefaults.Serializer) };
        var replacement = PodcastSplitEndpoints.CloneForMusicRemix(original, extras, 1.25, 0.3);
        var stored = JsonSerializer.Deserialize<EditPlanExtrasDto>(replacement.ExtraSettingsJson!, JsonDefaults.Serializer)!;
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal(original.SourceAssetId, replacement.SourceAssetId);
        Assert.Equal(original.StartSeconds, replacement.StartSeconds);
        Assert.Equal(original.EndSeconds, replacement.EndSeconds);
        Assert.Equal(JsonSerializer.Serialize(split), JsonSerializer.Serialize(stored.PodcastSplit));
        Assert.Equal(extras.Caption, stored.Caption);
        Assert.Equal(131, stored.MusicStartSeconds);
        Assert.Equal(1.25, stored.VoiceVolume);
        Assert.Equal(0.3, stored.MusicVolume);
        Assert.True(stored.MusicNeedsRender);
    }

    [Fact]
    public void GainOfLegacyVoiceStemComesFromTheActualRenderArguments()
    {
        var json = JsonSerializer.Serialize(new[] { "-filter_complex", "[voicemain]loudnorm=I=-14:TP=-1.5:LRA=11,volume=1.75[voicenorm]" });
        Assert.Equal(1.75, PodcastMusic.RenderedVoiceVolume(json, 1));
    }

    [Fact]
    public void RemixCopiesTheVideoAndCompensatesExistingVoiceGain()
    {
        var args = FfmpegArgumentBuilder.BuildPodcastRemix("original.mp4", "voice.mp4", "song.mp3",
            "updated.mp4", "updated.preview.mp4", 30, 131, 1.25 / 1.5, .2, -10, true).ToList();
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("volume=0.833333,asplit=2[voicemix][voicepreview]", graph);
        Assert.DoesNotContain("[1:a]loudnorm", graph);
        Assert.Contains("[2:a]atrim=start=131:duration=30", graph);
        Assert.Equal(2, args.Count(x => x == "copy"));
        Assert.Equal(2, args.Count(x => x == "0:v:0"));
        Assert.False(PodcastMusic.DiffersFromRenderedFile(JsonSerializer.Serialize(args), "song.mp3", 131));
    }
}
