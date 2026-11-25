using System.Collections.Concurrent;

namespace Flowboard.Web.Realtime;

/// <summary>Someone looking at a board right now, and the card they are editing, if any.</summary>
public sealed record Viewer(string UserId, string Name, Guid? EditingCardId);

/// <summary>
/// Who is on which board. Every open board page registers a session; a person with two tabs open is
/// shown once. Changes are announced so pages can redraw their "who's here" bar and editing badges.
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Viewer>> _boards = new();

    /// <summary>Raised with the board id whenever someone joins, leaves or starts/stops editing.</summary>
    public event Func<Guid, Task>? Changed;

    public Task JoinAsync(Guid boardId, Guid session, string userId, string name)
    {
        _boards.GetOrAdd(boardId, _ => new())[session] = new Viewer(userId, name, null);
        return RaiseAsync(boardId);
    }

    public Task LeaveAsync(Guid boardId, Guid session)
    {
        if (_boards.TryGetValue(boardId, out var sessions))
        {
            sessions.TryRemove(session, out _);
        }

        return RaiseAsync(boardId);
    }

    public Task SetEditingAsync(Guid boardId, Guid session, Guid? cardId)
    {
        if (_boards.TryGetValue(boardId, out var sessions) && sessions.TryGetValue(session, out var viewer))
        {
            sessions[session] = viewer with { EditingCardId = cardId };
        }

        return RaiseAsync(boardId);
    }

    /// <summary>People on the board, one entry per person, editing cards merged across their tabs.</summary>
    public IReadOnlyList<Viewer> ViewersOf(Guid boardId)
    {
        if (!_boards.TryGetValue(boardId, out var sessions))
        {
            return [];
        }

        return [.. sessions.Values
            .GroupBy(v => v.UserId)
            .Select(g => new Viewer(g.Key, g.First().Name, g.Select(v => v.EditingCardId).FirstOrDefault(id => id is not null)))
            .OrderBy(v => v.Name, StringComparer.CurrentCulture)];
    }

    private async Task RaiseAsync(Guid boardId)
    {
        if (Changed is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<Guid, Task>>())
            {
                await handler(boardId);
            }
        }
    }
}
