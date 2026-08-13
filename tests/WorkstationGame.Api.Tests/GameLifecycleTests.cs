using WorkstationGame.Api.Domain;
using WorkstationGame.Api.Services;
using Xunit;

namespace WorkstationGame.Api.Tests;

public sealed class GameLifecycleTests
{
    [Fact]
    public void RevealThenAdvance_OpensNextPendingRound()
    {
        var now = DateTimeOffset.UtcNow;
        var current = Round(1, RoundStatus.Open);
        var next = Round(2, RoundStatus.Pending);
        var game = Game(current, next);

        GameLifecycleService.Reveal(current, now);
        GameLifecycleService.AdvanceAfterReveal(game, now.AddSeconds(8));

        Assert.Equal(RoundStatus.Revealed, current.Status);
        Assert.Equal(now, current.RevealedAt);
        Assert.Equal(RoundStatus.Open, next.Status);
        Assert.Equal(now.AddSeconds(8), next.OpenedAt);
        Assert.Equal(GameStatus.Live, game.Status);
    }

    [Fact]
    public void AdvanceAfterFinalReveal_FinishesGame()
    {
        var game = Game(Round(1, RoundStatus.Revealed));

        GameLifecycleService.AdvanceAfterReveal(game, DateTimeOffset.UtcNow);

        Assert.Equal(GameStatus.Finished, game.Status);
    }

    private static GameSession Game(params GameRound[] rounds) => new()
    {
        Code = "ABC123",
        Title = "Test game",
        Status = GameStatus.Live,
        Rounds = [.. rounds]
    };

    private static GameRound Round(int position, RoundStatus status) => new()
    {
        Position = position,
        CorrectAnswer = "Alex",
        Status = status
    };
}
