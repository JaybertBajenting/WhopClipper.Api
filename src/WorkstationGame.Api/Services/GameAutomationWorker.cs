namespace WorkstationGame.Api.Services;

public sealed class GameAutomationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<GameAutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<GameLifecycleService>();
                await lifecycle.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automated game lifecycle tick failed.");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
