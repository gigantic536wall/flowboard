using Flowboard.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Flowboard.Core.Domain;

namespace Flowboard.Web.Realtime;

/// <summary>
/// SignalR endpoint (/hubs/boards) for clients other than the Blazor pages, e.g. a mobile app or a
/// script: call JoinBoard(boardId) and receive "BoardChanged" messages for that board.
/// </summary>
[Authorize]
public sealed class BoardHub(BoardService boards, UserManager<ApplicationUser> users) : Hub
{
    public static string GroupFor(Guid boardId) => $"board:{boardId}";

    /// <summary>Starts receiving changes of a board the caller can see. Returns false otherwise.</summary>
    public async Task<bool> JoinBoard(Guid boardId)
    {
        var userId = users.GetUserId(Context.User!);
        if (userId is null || !(await boards.GetBoardAsync(userId, boardId)).IsOk)
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(boardId));
        return true;
    }

    public Task LeaveBoard(Guid boardId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(boardId));
}
