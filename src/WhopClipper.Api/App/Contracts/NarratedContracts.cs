namespace WhopClipper.Api.App.Contracts;

/// <summary>One shot shown in the window: a stretch of a downloaded file, framed around <paramref name="FocusX"/>.</summary>
/// <param name="FocusX">Where the subject sits across the source frame, 0 left to 1 right. The window is narrower than
/// the vlog, so this decides what stays in view.</param>
/// <param name="Shows">What the shot was picked to show, as the AI described it.</param>
/// <param name="Payoff">A piece of the vlog moment itself, whose sound plays. Montage shots are silent under the
/// narrator.</param>
/// <param name="EpisodeSeconds">Where the shot starts in the source video.</param>
public sealed record NarratedShotDto(string RelativePath, double InSeconds, double DurationSeconds, int SourceWidth,
    int SourceHeight, double FocusX, string Shows, bool Payoff = false, double? EpisodeSeconds = null);

/// <summary>A shot laid over the moment while its sound carries on: the picture leaves the speaker for a beat to show
/// what he is talking about.</summary>
/// <param name="AtSeconds">When it starts, counted from the moment's first frame.</param>
/// <param name="EpisodeSeconds">Where the file it is cut from starts in the source video, to fetch it again.</param>
public sealed record NarratedCutawayDto(string RelativePath, double InSeconds, double AtSeconds, double DurationSeconds,
    double FocusX, string Shows, double? EpisodeSeconds = null);

/// <summary>A point on a shot as fractions of its frame, and a size as a fraction of its width: where the opening red
/// arrow points, or where the closing skull covers a face.</summary>
public sealed record NarratedMarkDto(double X, double Y, double Size);

/// <summary>
/// A narrated-luxury clip, the @mambafx.luxury edit. A narrator sets the story up over a fast montage from the same
/// vlog, then the moment itself plays with its own sound. The picture sits in a window under a fixed meme title.
/// </summary>
/// <param name="Pieces">The payoff's pieces on the span's clock.</param>
/// <param name="Shots">Montage shots, then payoff pieces, in the order they play.</param>
/// <param name="Words">Every caption word on the clip clock: the narration first, then the payoff.</param>
/// <param name="OverlayTitle">The meme line burned above the window ("POV: ..."), not the upload title.</param>
/// <param name="Arrow">Where the red arrow on the first shot points; null leaves it out.</param>
/// <param name="Skull">The face on the last frame the skull covers; null leaves it out.</param>
/// <param name="Voice">Which voice read the narration, for the record.</param>
/// <param name="SkullOff">Leaves the skull off this clip while keeping where it would go, so it can be put back. For a
/// last frame with no face, where the detector put it on a hand, a kitten or a bare chest.</param>
/// <param name="Cutaways">Shots laid over the moment, in the order they play; null or empty for none.</param>
public sealed record NarratedPlanDto(string SpanRelativePath, int SpanWidth, int SpanHeight,
    IReadOnlyList<StoryPieceDto> Pieces, IReadOnlyList<NarratedShotDto> Shots, string Narration,
    string NarrationRelativePath, double NarrationSeconds, IReadOnlyList<ClipWordDto> Words, string OverlayTitle,
    string Title, NarratedMarkDto? Arrow, NarratedMarkDto? Skull, double EpisodeOffsetSeconds, string Voice,
    bool SkullOff = false, IReadOnlyList<NarratedCutawayDto>? Cutaways = null);

/// <summary>What Settings shows about the narrator voice. The key itself never leaves the backend.</summary>
public sealed record VoiceOverSettingsDto(bool Configured, string? KeyHint, string VoiceId, string ModelId);

/// <param name="ApiKey">Null or empty keeps the saved key.</param>
public sealed record UpdateVoiceOverRequest(string? ApiKey, string? VoiceId, string? ModelId);
