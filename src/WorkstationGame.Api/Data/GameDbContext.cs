using Microsoft.EntityFrameworkCore;
using WorkstationGame.Api.Domain;

namespace WorkstationGame.Api.Data;

public sealed class GameDbContext(DbContextOptions<GameDbContext> options) : DbContext(options)
{
    public DbSet<GameSession> Games => Set<GameSession>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<GameRound> Rounds => Set<GameRound>();
    public DbSet<RoundOption> RoundOptions => Set<RoundOption>();
    public DbSet<PlayerAnswer> Answers => Set<PlayerAnswer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameSession>(entity =>
        {
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(6);
            entity.Property(x => x.Title).HasMaxLength(120);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<Player>(entity =>
        {
            entity.HasIndex(x => new { x.GameSessionId, x.NormalizedName }).IsUnique();
            entity.Property(x => x.DisplayName).HasMaxLength(40);
            entity.Property(x => x.NormalizedName).HasMaxLength(40);
            entity.Property(x => x.TokenHash).HasMaxLength(64);
        });

        modelBuilder.Entity<GameRound>(entity =>
        {
            entity.HasIndex(x => new { x.GameSessionId, x.Position }).IsUnique();
            entity.Property(x => x.CorrectAnswer).HasMaxLength(80);
            entity.Property(x => x.FunFact).HasMaxLength(280);
            entity.Property(x => x.ImagePath).HasMaxLength(500);
            entity.Property(x => x.ExternalImageUrl).HasMaxLength(1500);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<RoundOption>(entity =>
        {
            entity.HasIndex(x => new { x.GameRoundId, x.Position }).IsUnique();
            entity.Property(x => x.Label).HasMaxLength(80);
        });

        modelBuilder.Entity<PlayerAnswer>(entity =>
        {
            entity.HasIndex(x => new { x.GameRoundId, x.PlayerId }).IsUnique();
            entity.Property(x => x.Guess).HasMaxLength(80);
        });
    }
}
