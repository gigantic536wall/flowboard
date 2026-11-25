using Flowboard.Core.Domain;
using Flowboard.Core.Services;
using Flowboard.Web.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flowboard.Tests;

public class PresenceTrackerTests
{
    private static readonly Guid Board = Guid.NewGuid();

    [Fact]
    public async Task Viewers_are_listed_once_per_person_even_with_several_tabs()
    {
        var presence = new PresenceTracker();
        var raised = 0;
        presence.Changed += _ => { raised++; return Task.CompletedTask; };

        var anaTab1 = Guid.NewGuid();
        var anaTab2 = Guid.NewGuid();
        await presence.JoinAsync(Board, anaTab1, "ana", "Ana");
        await presence.JoinAsync(Board, anaTab2, "ana", "Ana");
        await presence.JoinAsync(Board, Guid.NewGuid(), "ben", "Ben");

        Assert.Equal(["Ana", "Ben"], presence.ViewersOf(Board).Select(v => v.Name));
        Assert.Equal(3, raised);

        await presence.LeaveAsync(Board, anaTab1);
        Assert.Equal(2, presence.ViewersOf(Board).Count); // Ana still has a tab open
        await presence.LeaveAsync(Board, anaTab2);
        Assert.Equal("Ben", Assert.Single(presence.ViewersOf(Board)).Name);
    }

    [Fact]
    public async Task Editing_shows_which_card_someone_has_open()
    {
        var presence = new PresenceTracker();
        var session = Guid.NewGuid();
        var card = Guid.NewGuid();
        await presence.JoinAsync(Board, session, "ana", "Ana");

        await presence.SetEditingAsync(Board, session, card);
        Assert.Equal(card, Assert.Single(presence.ViewersOf(Board)).EditingCardId);

        await presence.SetEditingAsync(Board, session, null);
        Assert.Null(Assert.Single(presence.ViewersOf(Board)).EditingCardId);
    }

    [Fact]
    public void Boards_without_viewers_are_empty()
    {
        Assert.Empty(new PresenceTracker().ViewersOf(Guid.NewGuid()));
    }
}

public class BoardBroadcasterTests
{
    private static BoardChange Change(Guid board) => new(board, ActivityKind.CardCreated, Guid.NewGuid(), "ana", "Ana", "added “X”");

    [Fact]
    public async Task Subscribers_of_a_board_receive_its_changes_until_they_unsubscribe()
    {
        var hub = new RecordingHub();
        var broadcaster = new BoardBroadcaster(hub, NullLogger<BoardBroadcaster>.Instance);
        var board = Guid.NewGuid();
        var other = Guid.NewGuid();
        var received = new List<BoardChange>();

        var subscription = broadcaster.Subscribe(board, c => { received.Add(c); return Task.CompletedTask; });
        await broadcaster.PublishAsync(Change(board));
        await broadcaster.PublishAsync(Change(other));
        subscription.Dispose();
        await broadcaster.PublishAsync(Change(board));

        Assert.Single(received);
        Assert.Equal([$"board:{board}", $"board:{other}", $"board:{board}"], hub.Groups);
    }

    [Fact]
    public async Task One_failing_page_does_not_stop_the_others()
    {
        var broadcaster = new BoardBroadcaster(new RecordingHub(), NullLogger<BoardBroadcaster>.Instance);
        var board = Guid.NewGuid();
        var delivered = false;
        using var broken = broadcaster.Subscribe(board, _ => throw new InvalidOperationException("circuit gone"));
        using var healthy = broadcaster.Subscribe(board, _ => { delivered = true; return Task.CompletedTask; });

        await broadcaster.PublishAsync(Change(board));

        Assert.True(delivered);
    }

    /// <summary>Minimal SignalR hub context that records which groups were messaged.</summary>
    private sealed class RecordingHub : IHubContext<BoardHub>
    {
        public List<string> Groups { get; } = [];
        public IHubClients Clients => new HubClients(this);
        IGroupManager IHubContext<BoardHub>.Groups => throw new NotSupportedException();

        private sealed class HubClients(RecordingHub owner) : IHubClients
        {
            public IClientProxy Group(string groupName)
            {
                owner.Groups.Add(groupName);
                return new NoProxy();
            }

            public IClientProxy All => new NoProxy();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new NoProxy();
            public IClientProxy Client(string connectionId) => new NoProxy();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoProxy();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new NoProxy();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoProxy();
            public IClientProxy User(string userId) => new NoProxy();
            public IClientProxy Users(IReadOnlyList<string> userIds) => new NoProxy();
        }

        private sealed class NoProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
