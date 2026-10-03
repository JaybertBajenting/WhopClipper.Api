using WhopClipper.Api.App.Domain;

namespace WhopClipper.Api.App.Contracts;

/// <summary>
/// The two ways a campaign's clips can be cut. Streamer is the Fexr house style: a full-frame
/// crop, a hook card, sound cues and colour washes. PodcastSplit is the Double Coverage TV edit:
/// the guest's camera across the top half, footage of whatever is being said across the bottom,
/// and one word at a time on the line between them.
/// </summary>
public static class EditStyles
{
    public const string Streamer = "Streamer";
    public const string PodcastSplit = "PodcastSplit";
    /// <summary>The @mambafx.luxury edit: a narrator sets the story up over a fast montage, then the vlog moment
    /// plays in a window under a meme title, ending on a desaturated freeze with a skull.</summary>
    public const string NarratedLuxury = "NarratedLuxury";
    public static bool IsKnown(string? value) => value is Streamer or PodcastSplit or NarratedLuxury;
}

/// <summary>
/// Per-campaign edit style. The defaults are measured from the 25 Double Coverage TV shorts:
/// the channel's logo sits 80px in from the right and 136px down (0.074 and 0.071 of the frame),
/// and B-roll changes shot about every 2.2 seconds.
/// </summary>
/// <param name="MusicVolume">Level of the bed before ducking. The reference shorts keep music
/// audible in the pauses, 0-9 dB under the speech.</param>
/// <param name="CameraSeats">Who sits where on an episode's wide couch shot, left to right, keyed by the
/// YouTube video id. The camera half holds on the seat of whoever tells the story. Without it a couch shot
/// holds on the person seen most, which put Jingle Jared on screen through Mike Majlak's own stories.</param>
/// <param name="SoundLocationId">The sound-effect folder a narrated clip takes its bell and camera shutter from.
/// Without it, any enabled sound-effect folder is searched, the newest first.</param>
public sealed record CampaignEditStyleDto(string Style = EditStyles.Streamer, double MusicVolume = 0.2,
    double MusicDuckingDb = -14, string CaptionFont = "Segoe UI Black", int CaptionFontSize = 84,
    double LogoWidthFraction = 0.2, double LogoRightMarginFraction = 0.074, double LogoTopMarginFraction = 0.071,
    double BrollShotSeconds = 2.2, Guid? MusicLocationId = null, IReadOnlyList<string>? ExcludedMusic = null,
    double VoiceVolume = 1.0, IReadOnlyDictionary<string, IReadOnlyList<string>>? CameraSeats = null,
    bool SkullEnding = true, bool SoundEffects = true, Guid? SoundLocationId = null);

public sealed record StoryPieceDto(double StartSeconds, double EndSeconds);

/// <summary>A story whose pieces were chosen by hand rather than by the AI - used to recreate a
/// reference short from the exact moments it was cut from.</summary>
/// <param name="MusicAssetId">Forces this clip's music bed instead of the campaign's rotation - used to
/// re-render a clip with a different song, for example to replace one that drowned the speech.</param>
/// <param name="VoiceVolume">Per-clip voice gain, overriding the campaign default. Null follows the campaign.</param>
/// <param name="MusicVolume">Per-clip music gain, overriding the campaign default. Null follows the campaign.</param>
/// <param name="SupersedesEditPlanId">The cut this one replaces. Retired only once this clip renders, so a
/// failed re-render still leaves the old clip in place.</param>
/// <param name="Caption">The post's own sentence. Carried through a re-render so a clip does not lose
/// the caption it was written with and fall back to repeating its title.</param>
/// <param name="Teller">Who tells the story, by name, so the camera half holds on their seat
/// (<see cref="CampaignEditStyleDto.CameraSeats"/>).</param>
public sealed record ImportedStoryDto(string Title, IReadOnlyList<StoryPieceDto> Pieces, Guid? MusicAssetId = null,
    double? VoiceVolume = null, double? MusicVolume = null, Guid? SupersedesEditPlanId = null, string Caption = "",
    double? MusicStartSeconds = null, IReadOnlyList<BrollShotDto>? ReviewedBroll = null,
    IReadOnlyList<string>? Emphasis = null, string? Teller = null);

/// <param name="Url">Registers the episode on the campaign first when it is not already a source.</param>
public sealed record ImportStoriesRequest(IReadOnlyList<ImportedStoryDto> Stories, Guid? SourceAssetId = null,
    string? Url = null, PlatformKind Platform = PlatformKind.TikTok, AiMode Mode = AiMode.Claude);

public sealed record CropRectDto(int X, int Y, int Width, int Height);

/// <summary>One stretch of the podcast camera, in seconds of the downloaded span file.</summary>
public sealed record TopShotDto(double InSeconds, double DurationSeconds, CropRectDto Crop,
    IReadOnlyList<CropRectDto>? Blur = null, bool Fit = false);

/// <param name="Blur">Logo boxes that could not be cropped away, in bottom-half output pixels.</param>
/// <param name="FootageId">For a shot placed by hand in the B-roll editor: the footage it was cut from, and where in
/// that footage it starts. The editor shows which parts of a video are already used.</param>
public sealed record BrollShotDto(string RelativePath, double InSeconds, double DurationSeconds, CropRectDto Crop,
    int SourceWidth, int SourceHeight, IReadOnlyList<CropRectDto>? Blur, string Query,
    string? SourceUrl = null, string? SourceTitle = null, Guid? FootageId = null, double? FootageInSeconds = null);

/// <summary>A moment where no usable footage was found. Reported, never silently filled with the
/// wrong shot.</summary>
public sealed record BrollGapDto(double AtSeconds, double DurationSeconds, string Query, string Reason);

public sealed record ClipWordDto(double Start, double End, string Word);

/// <summary>How a podcast-split clip was put together, so its footage can be checked before posting.</summary>
public sealed record RenderedClipStyleDto(string Style, int Pieces, int CameraShots, int BrollShots,
    IReadOnlyList<BrollGapDto> Gaps, IReadOnlyList<RenderedClipFootageDto> Footage,
    double? VoiceVolume = null, double? MusicVolume = null);

public sealed record RenderedClipFootageDto(string Query, string? Title, string? Url);

/// <summary>
/// Everything a podcast-split render needs, stored on the edit plan. Times in
/// <paramref name="Pieces"/> and <paramref name="TopShots"/> are seconds of the span file;
/// add <paramref name="EpisodeOffsetSeconds"/> for the episode clock. Words are already on the
/// clip clock.
/// </summary>
public sealed record PodcastSplitPlanDto(string SpanRelativePath, int SpanWidth, int SpanHeight,
    IReadOnlyList<StoryPieceDto> Pieces, IReadOnlyList<TopShotDto> TopShots, IReadOnlyList<BrollShotDto> Broll,
    IReadOnlyList<BrollGapDto> Gaps, IReadOnlyList<ClipWordDto> Words, IReadOnlyList<string> Emphasis,
    double EpisodeOffsetSeconds, string Title = "");

/// <summary>Point one clip at a specific bed. Null <paramref name="MusicStartSeconds"/> falls back to the
/// asset's own segment start from the folder guide, so each song still enters at its marked hook.</summary>
public sealed record SetPlanMusicRequest(Guid? MusicAssetId, double? MusicStartSeconds);

/// <summary>Rewrite the title and caption a finished clip is uploaded with.</summary>
public sealed record SetPlanCopyRequest(string? Title, string? Caption);
