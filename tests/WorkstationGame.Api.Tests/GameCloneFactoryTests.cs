using WorkstationGame.Api.Domain;
using WorkstationGame.Api.Services;
using Xunit;

namespace WorkstationGame.Api.Tests;

public sealed class GameCloneFactoryTests
{
    [Fact]
    public void Clone_CopiesRoundContentButResetsGamePlayState()
    {
        var source = new GameSession
        {
            Code = "OLD123",
            Title = "Workstation Reveal",
            Status = GameStatus.Finished,
            RevealDurationSeconds = 8,
            Players =
            [
                new Player
                {
                    DisplayName = "Player",
                    NormalizedName = "PLAYER",
                    TokenHash = "hash"
                }
            ],
            Rounds =
            [
                new GameRound
                {
                    Position = 1,
                    CorrectAnswer = "Alex",
                    FunFact = "Canada | Developer",
                    ImagePath = "games/desk.jpg",
                    DurationSeconds = 30,
                    Status = RoundStatus.Revealed,
                    OpenedAt = DateTimeOffset.UtcNow,
                    RevealedAt = DateTimeOffset.UtcNow,
                    Options =
                    [
                        new RoundOption { Position = 1, Label = "Alex" },
                        new RoundOption { Position = 2, Label = "Sam" }
                    ]
                }
            ]
        };

        var duplicate = GameCloneFactory.Clone(source, "NEW456", "Workstation Reveal (Copy)");

        Assert.Equal("NEW456", duplicate.Code);
        Assert.Equal(GameStatus.Lobby, duplicate.Status);
        Assert.Empty(duplicate.Players);
        var round = Assert.Single(duplicate.Rounds);
        Assert.Equal(30, round.DurationSeconds);
        Assert.Equal("games/desk.jpg", round.ImagePath);
        Assert.Equal(RoundStatus.Pending, round.Status);
        Assert.Null(round.OpenedAt);
        Assert.Null(round.RevealedAt);
        Assert.Equal(["Alex", "Sam"], round.Options.Select(option => option.Label));
        Assert.NotEqual(source.Rounds[0].Id, round.Id);
    }
}
