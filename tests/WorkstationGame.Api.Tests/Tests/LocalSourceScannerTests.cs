using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class LocalSourceScannerTests
{
    [Theory]
    [InlineData("https://www.youtube.com/live/0xkq-BbzKrw?is=MskrQ-Ty7MhArPSF", "0xkq-BbzKrw")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/abcdefghijk", "abcdefghijk")]
    [InlineData("https://www.twitch.tv/videos/123456789", "123456789")]
    [InlineData("https://www.tiktok.com/@creator/video/7300000000000000000", "7300000000000000000")]
    [InlineData("", "")]
    [InlineData("not a url", "")]
    public void ReadsTheVideoIdOutOfASourceUrl(string url, string expected)
    {
        Assert.Equal(expected, LocalSourceScanner.VideoIdFromUrl(url));
    }

    [Fact]
    public void NormalizesAwayEmojiAndPunctuationSoRenamedFilesStillMatch()
    {
        // A YouTube title carries emoji that no file name survives intact.
        var title = LocalSourceScanner.Normalize("🔴MASSIVE ONE PIECE CARD OPENING🔴we are now broke🔴");
        var file = LocalSourceScanner.Normalize("MASSIVE ONE PIECE CARD OPENING - we are now broke [0xkq-BbzKrw]");

        Assert.Equal("massiveonepiececardopeningwearenowbroke", title);
        Assert.Contains(title, file);
    }

    [Fact]
    public void NormalizationIgnoresCasingAndSpacing()
    {
        Assert.Equal(
            LocalSourceScanner.Normalize("One Piece Debate Stream"),
            LocalSourceScanner.Normalize("one_piece-debate  STREAM"));
    }
}
