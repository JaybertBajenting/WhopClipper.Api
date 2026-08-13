using Microsoft.AspNetCore.SignalR;

namespace WorkstationGame.Api.Hubs;

public sealed class GameHub : Hub
{
    public Task SubscribeGame(string code) =>
        Groups.AddToGroupAsync(Context.ConnectionId, Normalize(code));

    public Task UnsubscribeGame(string code) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Normalize(code));

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();
}

