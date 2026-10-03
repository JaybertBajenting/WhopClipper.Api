using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// The segment guide Matthew keeps beside his songs: which part of each track plays under a clip.
/// The text here is the shape of the real file, headings, Shazam links and all.
/// </summary>
public sealed class MusicSegmentGuideTests
{
    private const string Guide = """
        # Segments to use

        ## Kid Rock - All Summer Long.mp3
        Song: **All Summer Long** by **Kid Rock**
        Shazam: https://www.shazam.com/track/45267950/all-summer-long

        Used in:
        - `Inside Kid Rock’s “White House” Mansion 👀 [OU-mCK_xcAU]` -> use **0:06-0:23** (clip length 16.9s)

        ## Lil Baby - Dead Fresh.mp3
        Song: **Dead Fresh** by **Lil Baby**

        Used in:
        - `What Cris Carter did with his first NFL check 😂 [uvj1A3IpykM]` -> use **2:11-2:39** (clip length 28.3s)

        ## Hensonn - Sahara.mp3

        Used in:
        - `Cris Carter goes off on DK Metcalf grabbing a fan 😳 [Xt1NU_76IPA]` -> use **0:18-0:42** (clip length 24.1s)
        - `When Kobe said he was better than Jordan 😳🏀 [FV78GLXBnSc]` -> use **0:24-0:43** (clip length 19.6s)

        ## peaky 420 - peaky blinders beat.mp3
        Song: **peaky blinders beat** by **peaky 420**
        """;

    [Fact]
    public void ReadsTheMinutesAndSecondsOfEachSong()
    {
        var segments = MusicSegmentGuide.Parse(Guide);

        Assert.Equal(new MusicSegment(6, 23), segments["Kid Rock - All Summer Long.mp3"]);
        Assert.Equal(new MusicSegment(131, 159), segments["Lil Baby - Dead Fresh.mp3"]);
    }

    /// <summary>
    /// Two shorts using one song are alternative placements of the same hook, not a sequence, so the
    /// first range wins rather than the last one read overwriting it.
    /// </summary>
    [Fact]
    public void ASongUsedInTwoShortsKeepsTheFirstRange()
    {
        var segments = MusicSegmentGuide.Parse(Guide);

        Assert.Equal(new MusicSegment(18, 42), segments["Hensonn - Sahara.mp3"]);
    }

    /// <summary>A song listed with no range keeps no segment, and plays from its beginning.</summary>
    [Fact]
    public void ASongWithoutARangeIsNotListed()
    {
        var segments = MusicSegmentGuide.Parse(Guide);

        Assert.False(segments.ContainsKey("peaky 420 - peaky blinders beat.mp3"));
        Assert.Equal(3, segments.Count);
    }

    /// <summary>The scanner compares file names the way Windows does, so case must not lose a match.</summary>
    [Fact]
    public void FileNamesMatchRegardlessOfCase()
    {
        var segments = MusicSegmentGuide.Parse(Guide);

        Assert.True(segments.ContainsKey("kid rock - all summer long.MP3"));
    }

    [Fact]
    public void AFolderWithNoGuideHasNoSegments()
    {
        var folder = Directory.CreateTempSubdirectory("whopclipper-guide").FullName;

        var segments = MusicSegmentGuide.ReadAsync(folder, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Empty(segments);
    }
}
