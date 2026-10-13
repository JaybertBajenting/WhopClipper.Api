using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

/// <summary>
/// The stills on the phone's cards ride inside the schedule, which is one Firestore document: when they do not all
/// fit, the clips coming up soonest keep theirs.
/// </summary>
public sealed class PhoneThumbnailsTests
{
    [Fact]
    public void EveryStillIsSentWhenTheyFit() =>
        Assert.Equal(["aaaa", null, "bbbb"], PhoneThumbnails.WithinBudget(["aaaa", null, "bbbb"], maxCharacters: 8));

    [Fact]
    public void TheSoonestClipsKeepTheirStillWhenNotAllFit()
    {
        // In schedule order. The third does not fit after the first two; the smaller fourth still does.
        var sent = PhoneThumbnails.WithinBudget(["aaaa", "bbbb", "cccccc", "dd"], maxCharacters: 10);
        Assert.Equal(["aaaa", "bbbb", null, "dd"], sent);
    }

    /// <summary>A month of three clips a day, each still at its largest, stays under the schedule's own limit.</summary>
    [Fact]
    public void TheBudgetLeavesRoomForTheScheduleItself()
    {
        var still = new string('a', PhoneThumbnails.MaxBytes * 4 / 3);
        var sent = PhoneThumbnails.WithinBudget(Enumerable.Repeat<string?>(still, 90).ToList());
        Assert.InRange(sent.Sum(x => x?.Length ?? 0), 1, PhoneThumbnails.MaxTotalCharacters);
        Assert.True(PhoneThumbnails.MaxTotalCharacters < 700_000 - 200_000);
    }
}
