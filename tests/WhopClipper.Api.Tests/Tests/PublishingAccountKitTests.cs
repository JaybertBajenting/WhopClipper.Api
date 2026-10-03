using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class PublishingAccountKitTests
{
    [Theory]
    [InlineData(PlatformKind.TikTok, 24)]
    [InlineData(PlatformKind.InstagramReels, 30)]
    [InlineData(PlatformKind.YouTubeShorts, 30)]
    public void DefaultsCreateADistinctValueAddingHighlightsIdentity(PlatformKind platform, int maxHandleLength)
    {
        var campaign = new Campaign { Name = "Kai campaign", Brand = "Kai Cenat", Category = "Entertainment" };

        var fields = PlatformAccountKitService.DefaultFields(campaign, platform);

        Assert.Equal("Clip Notes: Kai Cenat", fields["displayName"]);
        Assert.NotEqual("Kai Cenat", fields["displayName"]);
        Assert.Equal(fields["handle"], fields["username"]);
        Assert.StartsWith("clipnotes", fields["handle"], StringComparison.Ordinal);
        Assert.NotEqual("kaicenat", fields["handle"]);
        Assert.InRange(fields["handle"].Length, 3, maxHandleLength);
        Assert.Contains("Kai Cenat", fields["bio"], StringComparison.Ordinal);
        Assert.Contains("Independent highlights", fields["bio"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("captions", fields["bio"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("added context", fields["bio"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not the creator's official account", fields["bio"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, fields["loginEmail"]);
        Assert.Equal(string.Empty, fields["loginPhone"]);
        Assert.Equal(string.Empty, fields["profileImage"]);
    }

    [Fact]
    public void LongCreatorNamesKeepTheIndependentIdentityMarkerWithinTikTokLimit()
    {
        var campaign = new Campaign
        {
            Name = "Campaign",
            Brand = "A Very Long Featured Creator Name That Cannot Fit In A Handle"
        };

        var handle = PlatformAccountKitService.DefaultFields(campaign, PlatformKind.TikTok)["handle"];

        Assert.StartsWith("clipnotes", handle, StringComparison.Ordinal);
        Assert.True(handle.Length <= 24);
    }

    [Fact]
    public void CampaignNameIsOnlyTheFeaturedSubjectWhenBrandIsMissing()
    {
        var campaign = new Campaign { Name = "The Featured Speaker" };

        var fields = PlatformAccountKitService.DefaultFields(campaign, PlatformKind.YouTubeShorts);

        Assert.Equal("Clip Notes: The Featured Speaker", fields["displayName"]);
        Assert.NotEqual("thefeaturedspeaker", fields["handle"]);
        Assert.Contains("The Featured Speaker", fields["bio"], StringComparison.Ordinal);
    }

    [Fact]
    public void ExactLegacyGeneratedIdentityIsUpgradedButContactEditsArePreserved()
    {
        var campaign = new Campaign { Name = "Kai campaign", Brand = "Kai Cenat" };
        var stored = new Dictionary<string, string>
        {
            ["username"] = "kaicenat",
            ["handle"] = "kaicenat",
            ["displayName"] = "Kai Cenat",
            ["bio"] = "The best moments from Kai Cenat.",
            ["loginEmail"] = "my-highlights@example.test",
            ["contactEmail"] = "collabs@example.test"
        };

        var merged = PlatformAccountKitService.MergeStoredFields(campaign, PlatformKind.TikTok, stored);

        Assert.Equal("Clip Notes: Kai Cenat", merged["displayName"]);
        Assert.StartsWith("clipnotes", merged["handle"], StringComparison.Ordinal);
        Assert.Contains("Not the creator's official account", merged["bio"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("my-highlights@example.test", merged["loginEmail"]);
        Assert.Equal("collabs@example.test", merged["contactEmail"]);
    }

    [Fact]
    public void GenuineCustomHighlightsIdentityIsNeverOverwritten()
    {
        var campaign = new Campaign { Name = "Kai campaign", Brand = "Kai Cenat" };
        var stored = new Dictionary<string, string>
        {
            ["username"] = "deepcutdaily",
            ["handle"] = "deepcutdaily",
            ["displayName"] = "Deep Cut Daily",
            ["bio"] = "Independent commentary and highlights with source credit."
        };

        var merged = PlatformAccountKitService.MergeStoredFields(campaign, PlatformKind.InstagramReels, stored);

        Assert.Equal("deepcutdaily", merged["username"]);
        Assert.Equal("deepcutdaily", merged["handle"]);
        Assert.Equal("Deep Cut Daily", merged["displayName"]);
        Assert.Equal(stored["bio"], merged["bio"]);
    }
}
