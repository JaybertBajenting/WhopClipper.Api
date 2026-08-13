namespace WorkstationGame.Api.Contracts;

public sealed record CreateGameRequest(string Title);
public sealed record JoinGameRequest(string DisplayName);
public sealed record SubmitAnswerRequest(Guid PlayerId, string PlayerToken, Guid RoundId, string Guess);
public sealed record CreateRoundRequest(
    string CorrectAnswer,
    IReadOnlyList<string> Options,
    string? FunFact,
    string? ImagePath,
    string? ImageUrl,
    int DurationSeconds = 30);

