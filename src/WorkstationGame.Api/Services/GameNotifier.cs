using Microsoft.AspNetCore.SignalR;
using WorkstationGame.Api.Hubs;

namespace WorkstationGame.Api.Services;

public sealed class GameNotifier(IHubContext<GameHub> hub)
{
    public Task NotifyAsync(string code, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(GameHub.Normalize(code)).SendAsync("game-updated", new { code }, cancellationToken);
}

