using System.Text.Json;
using WhopClipper.Api.App.Contracts;
using WhopClipper.Api.App.Domain;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// Which finished clips the "Cars only" switch keeps: a car named in the title or caption, unless the clip was
/// marked by hand.
/// </summary>
public sealed class ClipTopicsTests
{
    private static EditPlan Plan(bool? car) => new()
    {
        Platform = PlatformKind.TikTok,
        OutputFileName = "clip.mp4",
        ExtraSettingsJson = JsonSerializer.Serialize(
            new EditPlanExtrasDto("NarratedLuxury", null, null, -14, [], [], Car: car), JsonDefaults.Serializer),
    };

    [Theory]
    [InlineData("TJR's Hypercar Goes Transformers Mode 🤯")]
    [InlineData("TJR Takes His Rolls Through a Drive-Thru 🍔")]
    [InlineData("Trader Turns $70K Profit Into New Whip 🚗")]
    [InlineData("TJR Manifested His G63 Brabus 🔥")]
    [InlineData("Bank Says No To TJR's New Truck 😳")]
    [InlineData("He Never Even Saw The Car 😳")]
    public void ACarNamedInTheTitleMakesItACarClip(string title) =>
        Assert.True(ClipTopics.IsCar(Plan(null), title, "TJR does it again 🔥\r\n\r\n#TJR"));

    [Theory]
    [InlineData("TJR Only Spent $9 on Ads 💸")]
    [InlineData("TJR Gets A Care Package From Drake 🤯")]
    [InlineData("TJR Flexes His Credit Card Limit 💳")]
    [InlineData("TJR's Payrolls Hit $200K A Month 💰")]
    public void WordsThatOnlyLookLikeCarsDoNot(string title) =>
        Assert.False(ClipTopics.IsCar(Plan(null), title, "TJR does it again 🔥\r\n\r\n#TJR"));

    [Fact]
    public void TheCaptionCountsToo() =>
        Assert.True(ClipTopics.IsCar(Plan(null), "TJR's $100 Burger Chaos 😳",
            "TJR offers $100 for a burger from the seat of his Koenigsegg 😳\r\n\r\n#TJR"));

    /// <summary>The mark is there for what the words get wrong, in both directions.</summary>
    [Fact]
    public void AMarkSetByHandWinsOverTheWords()
    {
        Assert.True(ClipTopics.IsCar(Plan(true), "TJR's $100 Burger Chaos 😳", "#TJR"));
        Assert.False(ClipTopics.IsCar(Plan(false), "TJR Promises To Give Away A Car 🚗", "#TJR"));
    }

    [Fact]
    public void AClipWithNoPlanGoesByItsWords()
    {
        Assert.True(ClipTopics.IsCar(null, "Ferrari day", ""));
        Assert.False(ClipTopics.IsCar(null, "Trading day", ""));
    }
}
