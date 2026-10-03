using System.Text.Json;
using WhopClipper.Api.App.Services;

namespace WhopClipper.Api.Tests;

public sealed class BrollVisualReviewTests
{
    [Fact]
    public void UfcCanIllustrateMmaButBoxingIsNotAnMmaAlias()
    {
        BrollSearchEntry[] entries = [new("aaaaaaaaaaa", "Justin Gaethje UFC highlights", 300, "UFC", null, 0),
            new("bbbbbbbbbbb", "Justin Gaethje boxing highlights", 300, "Sports", null, 1)];
        Assert.Equal("aaaaaaaaaaa", Assert.Single(BrollCandidates.Rank(entries, "Justin Gaethje MMA highlights")).Id);
    }
    [Fact]
    public void MatchingTitleCannotAcceptAnUninspectedClip()
    {
        using var doc = JsonDocument.Parse("""{"framesInspected":false,"shots":[]}""");
        Assert.Throws<InvalidOperationException>(() => BrollVisualReview.Parse(doc.RootElement, 1));
    }

    [Fact]
    public void ReviewerRejectsUnrelatedIntroWhileKeepingTheActualFightInterval()
    {
        using var doc = JsonDocument.Parse("""
          {"framesInspected":true,"shots":[
            {"shotIndex":0,"approved":false,"visibleContent":"City buildings and graffiti","reason":"Intro does not show Gaethje fighting"},
            {"shotIndex":1,"approved":true,"visibleContent":"Gaethje exchanges kicks inside the octagon","reason":"Illustrates the described MMA striking"}
          ]}
          """);
        var decisions = BrollVisualReview.Parse(doc.RootElement, 2);
        Assert.Equal(1, Assert.Single(decisions.Where(x => x.Approved)).ShotIndex);
        Assert.False(decisions[0].Approved);
    }

    [Theory]
    [InlineData("{\"framesInspected\":true,\"shots\":[]}")]
    [InlineData("{\"framesInspected\":true,\"shots\":[{\"shotIndex\":9,\"approved\":true,\"visibleContent\":\"fight\",\"reason\":\"matched\"}]}")]
    [InlineData("{\"framesInspected\":true,\"shots\":[{\"shotIndex\":0,\"approved\":true,\"visibleContent\":\"\",\"reason\":\"matched title\"}]}")]
    [InlineData("{\"framesInspected\":true,\"shots\":[{\"shotIndex\":0,\"approved\":true,\"visibleContent\":\"fight\",\"reason\":\"matched\"},{\"shotIndex\":0,\"approved\":false,\"visibleContent\":\"intro\",\"reason\":\"wrong\"}]}")]
    public void MissingAmbiguousOrOutOfBoundsReviewsFailClosed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => BrollVisualReview.Parse(doc.RootElement, 1));
    }

    [Fact]
    public void ACorrectPlanDrawnFromCoarseTimesIsKeptAndCutsOnTheQuotedWords()
    {
        // The Gaethje "Infinity Stones" plan was right but rejected: the AI sees a timestamp every few
        // words, so its boundaries sat ~0.3s early, and whisper heard "intern" where it wrote "interim".
        var said = "look what he did in wsof look how he won the bmf belt look how he won the intern belt twice"
            .Split(' ');
        var words = said.Select((w, i) => new TranscriptWord(i * 0.3, i * 0.3 + 0.28, w)).ToList();
        BrollMoment Moment(double start, double end, string quote) =>
            new(start, end, "Justin Gaethje highlights", "Justin Gaethje", "Gaethje winning a title fight", quote);
        var plan = new BrollPlan([Moment(0, 1.5, "Look what he did in WSOF."),
            Moment(1.5, 3.6, "Look how he won the BMF belt."),
            Moment(3.6, 6.3, "Look how he won the interim belt twice")], [], "Justin Gaethje", "Justin Gaethje highlights");

        Assert.True(BrollVisualReview.IsGrounded(plan, words));
        var snapped = BrollTiming.SnapToQuotes(plan, words);

        Assert.Equal(new[] { 0, 1.75, 3.85 }, snapped.Moments.Select(x => x.StartSeconds));
        Assert.Equal(new[] { 1.75, 3.85, 6.3 }, snapped.Moments.Select(x => x.EndSeconds));
    }

    [Fact]
    public void VisualIntentMustBeGroundedInTheWordsOfThatMoment()
    {
        TranscriptWord[] words = [new(0, 0.7, "Gaethje"), new(0.7, 1.2, "fights"), new(1.2, 1.7, "smart"),
            new(5, 6, "training")];
        var moment = new BrollMoment(0, 2, "Justin Gaethje MMA highlights", "Justin Gaethje",
            "Gaethje exchanging strikes in the octagon to illustrate fight intelligence", "Gaethje fights smart");
        Assert.True(BrollVisualReview.HasGroundedIntent(moment, words));
        Assert.False(BrollVisualReview.HasGroundedIntent(moment with { SpokenText = "training" }, words));
        Assert.False(BrollVisualReview.HasGroundedIntent(moment with { Subject = "" }, words));
        Assert.False(BrollVisualReview.HasGroundedIntent(moment with { SpokenText = "fights smart computer coding" }, words));
    }
}
