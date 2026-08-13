using WorkstationGame.Api.Domain;

namespace WorkstationGame.Api.Services;

public static class GameCloneFactory
{
    public static GameSession Clone(GameSession source, string code, string title)
    {
        var duplicate = new GameSession
        {
            Code = code,
            Title = title,
            RevealDurationSeconds = source.RevealDurationSeconds
        };

        duplicate.Rounds = source.Rounds
            .OrderBy(round => round.Position)
            .Select(round => CloneRound(round, duplicate.Id))
            .ToList();

        return duplicate;
    }

    private static GameRound CloneRound(GameRound source, Guid gameId)
    {
        var duplicate = new GameRound
        {
            GameSessionId = gameId,
            Position = source.Position,
            CorrectAnswer = source.CorrectAnswer,
            FunFact = source.FunFact,
            ImagePath = source.ImagePath,
            ExternalImageUrl = source.ExternalImageUrl,
            DurationSeconds = source.DurationSeconds
        };

        duplicate.Options = source.Options
            .OrderBy(option => option.Position)
            .Select(option => new RoundOption
            {
                GameRoundId = duplicate.Id,
                Position = option.Position,
                Label = option.Label
            })
            .ToList();

        return duplicate;
    }
}
