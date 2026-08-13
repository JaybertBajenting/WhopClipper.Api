namespace WorkstationGame.Api.Domain;

public enum GameStatus
{
    Lobby,
    Live,
    Finished
}

public enum RoundStatus
{
    Pending,
    Open,
    Revealed
}

public sealed class GameSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Title { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Lobby;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Player> Players { get; set; } = [];
    public List<GameRound> Rounds { get; set; } = [];
}

public sealed class Player
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameSessionId { get; set; }
    public required string DisplayName { get; set; }
    public required string NormalizedName { get; set; }
    public required string TokenHash { get; set; }
    public int TotalScore { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public GameSession GameSession { get; set; } = null!;
    public List<PlayerAnswer> Answers { get; set; } = [];
}

public sealed class GameRound
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameSessionId { get; set; }
    public int Position { get; set; }
    public required string CorrectAnswer { get; set; }
    public string? FunFact { get; set; }
    public string? ImagePath { get; set; }
    public string? ExternalImageUrl { get; set; }
    public int DurationSeconds { get; set; } = 30;
    public RoundStatus Status { get; set; } = RoundStatus.Pending;
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? RevealedAt { get; set; }
    public GameSession GameSession { get; set; } = null!;
    public List<RoundOption> Options { get; set; } = [];
    public List<PlayerAnswer> Answers { get; set; } = [];
}

public sealed class RoundOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameRoundId { get; set; }
    public int Position { get; set; }
    public required string Label { get; set; }
    public GameRound GameRound { get; set; } = null!;
}

public sealed class PlayerAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameRoundId { get; set; }
    public Guid PlayerId { get; set; }
    public required string Guess { get; set; }
    public bool IsCorrect { get; set; }
    public int CorrectRank { get; set; }
    public int ScoreAwarded { get; set; }
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public GameRound GameRound { get; set; } = null!;
    public Player Player { get; set; } = null!;
}

