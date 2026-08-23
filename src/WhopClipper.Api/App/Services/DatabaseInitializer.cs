using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhopClipper.Api.App.Data;

namespace WhopClipper.Api.App.Services;

public sealed record DatabaseReadinessSnapshot(bool Connected, bool MigrationsCurrent, string[] PendingMigrations, string? Error)
{
    public bool Ready => Connected && MigrationsCurrent && Error is null;
}

public sealed class DatabaseInitializationState
{
    private readonly object _gate = new();
    private DatabaseReadinessSnapshot _snapshot = new(false, false, [], "Database initialization has not completed.");
    public DatabaseReadinessSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public void Set(DatabaseReadinessSnapshot snapshot) { lock (_gate) _snapshot = snapshot; }
}

public sealed class DatabaseReadinessService(AppDbContext db, DatabaseInitializationState state)
{
    public async Task<DatabaseReadinessSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return new(false, false, [], state.Snapshot.Error ?? "PostgreSQL is not reachable.");
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            var snapshot = new DatabaseReadinessSnapshot(true, pending.Length == 0, pending,
                pending.Length == 0 ? null : "PostgreSQL has pending application migrations.");
            state.Set(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            var snapshot = new DatabaseReadinessSnapshot(false, false, [], ex.Message);
            state.Set(snapshot);
            return snapshot;
        }
    }
}

public sealed class DatabaseInitializer(IServiceScopeFactory scopeFactory, IOptions<AppOptions> options,
    DatabaseInitializationState state, ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken))
                throw new InvalidOperationException("PostgreSQL is not reachable.");
            if (options.Value.AutoMigrate) await db.Database.MigrateAsync(cancellationToken);
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            var snapshot = new DatabaseReadinessSnapshot(true, pending.Length == 0, pending,
                pending.Length == 0 ? null : "PostgreSQL has pending application migrations.");
            state.Set(snapshot);
            if (snapshot.Ready) logger.LogInformation("PostgreSQL migrations are current.");
            else logger.LogWarning("PostgreSQL has {MigrationCount} pending migrations.", pending.Length);
        }
        catch (Exception ex)
        {
            state.Set(new DatabaseReadinessSnapshot(false, false, [], ex.Message));
            logger.LogWarning(ex, "PostgreSQL is unavailable or migration failed. The API will remain running; database-backed routes will fail until PostgreSQL is ready.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
