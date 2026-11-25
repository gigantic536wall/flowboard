using System.Collections.Concurrent;
using System.Collections.Immutable;
using Flowboard.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace Flowboard.Web.Realtime;

/// <summary>
/// Fans board changes out to everyone watching the board: to open Blazor pages (each browser already
/// holds a SignalR connection to its circuit) and to the <see cref="BoardHub"/> group for other clients.
/// </summary>
public sealed partial class BoardBroadcaster(IHubContext<BoardHub> hub, ILogger<BoardBroadcaster> logger) : IBoardEvents
{
    private readonly ConcurrentDictionary<Guid, ImmutableList<Func<BoardChange, Task>>> _subscribers = new();

    /// <summary>Calls <paramref name="handler"/> for every change of the board until the result is disposed.</summary>
    public IDisposable Subscribe(Guid boardId, Func<BoardChange, Task> handler)
    {
        _subscribers.AddOrUpdate(boardId, _ => [handler], (_, list) => list.Add(handler));
        return new Subscription(() => _subscribers.AddOrUpdate(boardId, _ => [], (_, list) => list.Remove(handler)));
    }

    public async Task PublishAsync(BoardChange change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_subscribers.TryGetValue(change.BoardId, out var handlers))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    await handler(change);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One broken page must not stop the others from updating.
                    LogHandlerFailed(e, change.BoardId);
                }
            }
        }

        await hub.Clients.Group(BoardHub.GroupFor(change.BoardId)).SendAsync("BoardChanged", change, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A subscriber of board {BoardId} failed to handle a change")]
    private partial void LogHandlerFailed(Exception e, Guid boardId);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
