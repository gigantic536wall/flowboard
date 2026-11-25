using Flowboard.Core.Data;
using Flowboard.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Flowboard.Core.Services;

/// <summary>
/// All board operations. Every method takes the acting user, checks their role in the workspace,
/// writes a history entry and publishes the change to live viewers. A short-lived DbContext per call
/// keeps it safe for Blazor Server, where one component can live for hours.
/// </summary>
public sealed class BoardService(IDbContextFactory<FlowboardDbContext> factory, IBoardEvents events)
{
    public const int MaxNameLength = 100;
    public const int MaxColumnNameLength = 60;
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 4000;

    private static readonly string[] DefaultColumns = ["To do", "In progress", "Done"];

    // ---------------------------------------------------------------------------------------------
    // Workspaces

    public async Task<Result<Guid>> CreateWorkspaceAsync(string actorId, string name, CancellationToken ct = default)
    {
        if (Clean(name, MaxNameLength) is not { } clean)
        {
            return Fail.Invalid($"Name must be 1 to {MaxNameLength} characters.");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        // Guests' workspaces are demo data too, so they disappear together with the guest.
        var isDemo = await db.Users.Where(u => u.Id == actorId).Select(u => u.IsDemo).SingleOrDefaultAsync(ct);
        var workspace = new Workspace { Name = clean, IsDemo = isDemo };
        workspace.Members.Add(new Membership { UserId = actorId, Role = Role.Owner });
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        return Result<Guid>.Ok(workspace.Id);
    }

    public async Task<IReadOnlyList<WorkspaceSummary>> ListWorkspacesAsync(string actorId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Memberships
            .Where(m => m.UserId == actorId)
            .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id, (m, w) => new { m.Role, w })
            .OrderBy(x => x.w.Name)
            .Select(x => new WorkspaceSummary(x.w.Id, x.w.Name, x.Role, x.w.Members.Count))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------------------------------------
    // Boards

    public async Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(string actorId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Memberships
            .Where(m => m.UserId == actorId)
            .Join(db.Boards, m => m.WorkspaceId, b => b.WorkspaceId, (m, b) => new { m.Role, b })
            .OrderBy(x => x.b.Workspace!.Name).ThenBy(x => x.b.Name)
            .Select(x => new BoardSummary(x.b.Id, x.b.Name, x.b.WorkspaceId, x.b.Workspace!.Name, x.Role,
                db.Cards.Count(c => c.BoardId == x.b.Id)))
            .ToListAsync(ct);
    }

    /// <summary>A new board with the usual three columns.</summary>
    public async Task<Result<Guid>> CreateBoardAsync(string actorId, Guid workspaceId, string name, CancellationToken ct = default)
    {
        if (Clean(name, MaxNameLength) is not { } clean)
        {
            return Fail.Invalid($"Name must be 1 to {MaxNameLength} characters.");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var role = await RoleInAsync(db, actorId, workspaceId, ct);
        if (role is null)
        {
            return Fail.NotFound("Workspace");
        }

        if (role < Role.Member)
        {
            return Fail.Forbidden();
        }

        var board = new Board { WorkspaceId = workspaceId, Name = clean };
        var ranks = Rank.Spread(DefaultColumns.Length);
        for (var i = 0; i < DefaultColumns.Length; i++)
        {
            board.Columns.Add(new Column { Name = DefaultColumns[i], Rank = ranks[i] });
        }

        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);
        await LogAsync(db, board.Id, null, actorId, ActivityKind.BoardCreated, $"created the board “{clean}”", ct);
        return Result<Guid>.Ok(board.Id);
    }

    public async Task<Result<Done>> RenameBoardAsync(string actorId, Guid boardId, string name, CancellationToken ct = default)
    {
        if (Clean(name, MaxNameLength) is not { } clean)
        {
            return Fail.Invalid($"Name must be 1 to {MaxNameLength} characters.");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var (board, failure) = await BoardForAsync(db, actorId, boardId, Role.Owner, ct);
        if (board is null)
        {
            return failure;
        }

        var old = board.Name;
        board.Name = clean;
        await db.SaveChangesAsync(ct);
        await LogAsync(db, boardId, null, actorId, ActivityKind.BoardRenamed, $"renamed the board from “{old}” to “{clean}”", ct);
        return Result<Done>.Ok(default);
    }

    public async Task<Result<Done>> DeleteBoardAsync(string actorId, Guid boardId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (board, failure) = await BoardForAsync(db, actorId, boardId, Role.Owner, ct);
        if (board is null)
        {
            return failure;
        }

        db.Boards.Remove(board);
        await db.SaveChangesAsync(ct);
        // Not logged (the history goes with the board), but open pages must learn that it is gone.
        var name = await db.Users.Where(u => u.Id == actorId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? "Someone";
        await events.PublishAsync(new BoardChange(boardId, ActivityKind.BoardDeleted, null, actorId, name, "deleted the board"), ct);
        return Result<Done>.Ok(default);
    }

    public async Task<Result<BoardView>> GetBoardAsync(string actorId, Guid boardId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (board, failure) = await BoardForAsync(db, actorId, boardId, Role.Viewer, ct);
        if (board is null)
        {
            return failure;
        }

        var role = (await RoleInAsync(db, actorId, board.WorkspaceId, ct))!.Value;
        var workspace = await db.Workspaces.AsNoTracking().Where(w => w.Id == board.WorkspaceId)
            .Select(w => new { w.Name, w.IsDemo, w.InviteCode }).SingleAsync(ct);
        var columns = await db.Columns.AsNoTracking().Where(c => c.BoardId == boardId).ToListAsync(ct);
        var cards = await db.Cards.AsNoTracking().Where(c => c.BoardId == boardId)
            .Select(c => new CardView(c.Id, c.ColumnId, c.Title, c.Description, c.Rank,
                c.Assignee == null ? null : new PersonView(c.Assignee.Id, c.Assignee.DisplayName),
                c.DueDate, c.Labels, c.Version, c.UpdatedAt))
            .ToListAsync(ct);
        var members = await MembersAsync(db, board.WorkspaceId, ct);

        var byColumn = cards.ToLookup(c => c.ColumnId);
        var columnViews = columns
            .OrderBy(c => c.Rank, StringComparer.Ordinal)
            .Select(c => new ColumnView(c.Id, c.Name, c.Rank, c.WipLimit,
                [.. byColumn[c.Id].OrderBy(card => card.Rank, StringComparer.Ordinal)]))
            .ToList();
        return Result<BoardView>.Ok(new BoardView(board.Id, board.WorkspaceId, board.Name, workspace.Name, role, columnViews, members,
            workspace.IsDemo ? workspace.InviteCode : null));
    }

    public async Task<Result<IReadOnlyList<ActivityView>>> GetActivityAsync(string actorId, Guid boardId, int take = 50, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (board, failure) = await BoardForAsync(db, actorId, boardId, Role.Viewer, ct);
        if (board is null)
        {
            return failure;
        }

        var items = await db.Activities.AsNoTracking()
            .Where(a => a.BoardId == boardId)
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(take, 1, 200))
            .Select(a => new ActivityView(a.Id, a.CardId, a.Actor!.DisplayName, a.Kind, a.Text, a.At))
            .ToListAsync(ct);
        return Result<IReadOnlyList<ActivityView>>.Ok(items);
    }

    // ---------------------------------------------------------------------------------------------
    // Columns

    public async Task<Result<Guid>> AddColumnAsync(string actorId, Guid boardId, string name, CancellationToken ct = default)
    {
        if (Clean(name, MaxColumnNameLength) is not { } clean)
        {
            return Fail.Invalid($"Column name must be 1 to {MaxColumnNameLength} characters.");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var (board, failure) = await BoardForAsync(db, actorId, boardId, Role.Member, ct);
        if (board is null)
        {
            return failure;
        }

        var last = await db.Columns.Where(c => c.BoardId == boardId).Select(c => c.Rank).ToListAsync(ct);
        var column = new Column
        {
            BoardId = boardId,
            Name = clean,
            Rank = Rank.Between(last.Count == 0 ? null : last.Max(StringComparer.Ordinal), null),
        };
        db.Columns.Add(column);
        await db.SaveChangesAsync(ct);
        await LogAsync(db, boardId, null, actorId, ActivityKind.ColumnCreated, $"added the column “{clean}”", ct);
        return Result<Guid>.Ok(column.Id);
    }

    public async Task<Result<Done>> UpdateColumnAsync(string actorId, Guid columnId, string name, int? wipLimit, CancellationToken ct = default)
    {
        if (Clean(name, MaxColumnNameLength) is not { } clean)
        {
            return Fail.Invalid($"Column name must be 1 to {MaxColumnNameLength} characters.");
        }

        if (wipLimit is <= 0 or > 999)
        {
            return Fail.Invalid("The WIP limit must be between 1 and 999, or empty.");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var column = await db.Columns.SingleOrDefaultAsync(c => c.Id == columnId, ct);
        if (column is null)
        {
            return Fail.NotFound("Column");
        }

        var (board, failure) = await BoardForAsync(db, actorId, column.BoardId, Role.Member, ct);
        if (board is null)
        {
            return failure;
        }

        var old = column.Name;
        column.Name = clean;
        column.WipLimit = wipLimit;
        await db.SaveChangesAsync(ct);
        var text = old == clean ? $"changed the column “{clean}”" : $"renamed the column “{old}” to “{clean}”";
        await LogAsync(db, column.BoardId, null, actorId, ActivityKind.ColumnRenamed, text, ct);
        return Result<Done>.Ok(default);
    }

    /// <summary>Deletes a column together with its cards.</summary>
    public async Task<Result<Done>> DeleteColumnAsync(string actorId, Guid columnId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var column = await db.Columns.SingleOrDefaultAsync(c => c.Id == columnId, ct);
        if (column is null)
        {
            return Fail.NotFound("Column");
        }

        var (board, failure) = await BoardForAsync(db, actorId, column.BoardId, Role.Member, ct);
        if (board is null)
        {
            return failure;
        }

        if (await db.Columns.CountAsync(c => c.BoardId == column.BoardId, ct) == 1)
        {
            return Fail.Invalid("A board needs at least one column.");
        }

        var cardCount = await db.Cards.CountAsync(c => c.ColumnId == columnId, ct);
        db.Columns.Remove(column);
        await db.SaveChangesAsync(ct);
        var text = cardCount == 0
            ? $"deleted the column “{column.Name}”"
            : $"deleted the column “{column.Name}” with {cardCount} card{(cardCount == 1 ? "" : "s")}";
        await LogAsync(db, column.BoardId, null, actorId, ActivityKind.ColumnDeleted, text, ct);
        return Result<Done>.Ok(default);
    }

    // ---------------------------------------------------------------------------------------------
    // Cards

    /// <summary>Adds a card at the bottom of a column.</summary>
    public async Task<Result<CardView>> AddCardAsync(string actorId, Guid columnId, string title, CancellationToken ct = default)
    {
        if (Clean(title, MaxTitleLength) is not { } clean)
        {
            return Fail.Invalid($"Title must be 1 to {MaxTitleLength} characters.");
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var column = await db.Columns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == columnId, ct);
        if (column is null)
        {
            return Fail.NotFound("Column");
        }

        var (board, failure) = await BoardForAsync(db, actorId, column.BoardId, Role.Member, ct);
        if (board is null)
        {
            return failure;
        }

        var card = new Card { BoardId = column.BoardId, ColumnId = columnId, Title = clean, Rank = "" };
        db.Cards.Add(card);
        await SaveInColumnAsync(db, columnId, card, async () =>
        {
            var ranks = await db.Cards.Where(c => c.ColumnId == columnId && c.Id != card.Id).Select(c => c.Rank).ToListAsync(ct);
            return Rank.Between(ranks.Count == 0 ? null : ranks.Max(StringComparer.Ordinal), null);
        }, ct);
        await LogAsync(db, column.BoardId, card.Id, actorId, ActivityKind.CardCreated, $"added “{clean}” to {column.Name}", ct);
        return Result<CardView>.Ok(await CardViewAsync(db, card.Id, ct));
    }

    /// <summary>
    /// Saves an edit only if nobody else changed the card since <paramref name="expectedVersion"/> was read,
    /// so a slow editor never silently overwrites a colleague's work.
    /// </summary>
    public async Task<Result<CardView>> UpdateCardAsync(string actorId, Guid cardId, int expectedVersion, CardEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await using var db = await factory.CreateDbContextAsync(ct);
        var card = await db.Cards.SingleOrDefaultAsync(c => c.Id == cardId, ct);
        if (card is null)
        {
            return Fail.NotFound("Card");
        }

        var (board, failure) = await BoardForAsync(db, actorId, card.BoardId, Role.Member, ct);
        if (board is null)
        {
            return failure;
        }

        if (card.Version != expectedVersion)
        {
            return await ConflictAsync(db, cardId, ct);
        }

        var changes = new List<string>();
        if (edit.Title is not null)
        {
            if (Clean(edit.Title, MaxTitleLength) is not { } title)
            {
                return Fail.Invalid($"Title must be 1 to {MaxTitleLength} characters.");
            }

            if (title != card.Title)
            {
                changes.Add($"renamed it from “{card.Title}”");
                card.Title = title;
            }
        }

        if (edit.Description is not null && edit.Description != card.Description)
        {
            if (edit.Description.Length > MaxDescriptionLength)
            {
                return Fail.Invalid($"The description can have at most {MaxDescriptionLength} characters.");
            }

            card.Description = edit.Description;
            changes.Add("edited the description");
        }

        if (edit.ClearAssignee && card.AssigneeId is not null)
        {
            card.AssigneeId = null;
            changes.Add("removed the assignee");
        }
        else if (edit.AssigneeId is { } assignee && assignee != card.AssigneeId)
        {
            var name = await db.Memberships.Where(m => m.WorkspaceId == board.WorkspaceId && m.UserId == assignee)
                .Select(m => m.User!.DisplayName).SingleOrDefaultAsync(ct);
            if (name is null)
            {
                return Fail.Invalid("Cards can only be assigned to members of the workspace.");
            }

            card.AssigneeId = assignee;
            changes.Add($"assigned it to {name}");
        }

        if (edit.ClearDueDate && card.DueDate is not null)
        {
            card.DueDate = null;
            changes.Add("removed the due date");
        }
        else if (edit.DueDate is { } due && due != card.DueDate)
        {
            card.DueDate = due;
            changes.Add($"set the due date to {due:yyyy-MM-dd}");
        }

        if (edit.Labels is { } labels && labels != card.Labels)
        {
            card.Labels = labels;
            changes.Add("changed the labels");
        }

        if (changes.Count == 0)
        {
            return Result<CardView>.Ok(await CardViewAsync(db, cardId, ct));
        }

        card.Version = expectedVersion + 1;
        card.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone saved between our read and our write.
            return await ConflictAsync(db, cardId, ct);
        }

        await LogAsync(db, card.BoardId, cardId, actorId, ActivityKind.CardUpdated, $"updated “{card.Title}”: {string.Join(", ", changes)}", ct);
        return Result<CardView>.Ok(await CardViewAsync(db, cardId, ct));
    }

    /// <summary>Moves a card to position <paramref name="index"/> (0 = top) of a column on the same board.</summary>
    public async Task<Result<CardView>> MoveCardAsync(string actorId, Guid cardId, Guid toColumnId, int index, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var card = await db.Cards.SingleOrDefaultAsync(c => c.Id == cardId, ct);
        if (card is null)
        {
            return Fail.NotFound("Card");
        }

        var (board, failure) = await BoardForAsync(db, actorId, card.BoardId, Role.Member, ct);
        if (board is null)
        {
            return failure;
        }

        var target = await db.Columns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == toColumnId && c.BoardId == card.BoardId, ct);
        if (target is null)
        {
            return Fail.NotFound("Column");
        }

        var fromColumn = card.ColumnId;
        card.ColumnId = toColumnId;
        card.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveInColumnAsync(db, toColumnId, card, () => RankForPositionAsync(db, card, toColumnId, index, ct), ct);

        var text = fromColumn == toColumnId ? $"reordered “{card.Title}” in {target.Name}" : $"moved “{card.Title}” to {target.Name}";
        await LogAsync(db, card.BoardId, cardId, actorId, ActivityKind.CardMoved, text, ct);
        return Result<CardView>.Ok(await CardViewAsync(db, cardId, ct));
    }

    public async Task<Result<Done>> DeleteCardAsync(string actorId, Guid cardId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var card = await db.Cards.SingleOrDefaultAsync(c => c.Id == cardId, ct);
        if (card is null)
        {
            return Fail.NotFound("Card");
        }

        var (board, failure) = await BoardForAsync(db, actorId, card.BoardId, Role.Member, ct);
        if (board is null)
        {
            return failure;
        }

        db.Cards.Remove(card);
        await db.SaveChangesAsync(ct);
        await LogAsync(db, card.BoardId, cardId, actorId, ActivityKind.CardDeleted, $"deleted “{card.Title}”", ct);
        return Result<Done>.Ok(default);
    }

    // ---------------------------------------------------------------------------------------------
    // Members

    public async Task<IReadOnlyList<MemberView>> GetMembersAsync(string actorId, Guid workspaceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await RoleInAsync(db, actorId, workspaceId, ct) is null ? [] : await MembersAsync(db, workspaceId, ct);
    }

    /// <summary>Adds an existing user (by email) to a workspace. Owners only.</summary>
    public async Task<Result<MemberView>> AddMemberAsync(string actorId, Guid workspaceId, string email, Role role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await OwnerCheckAsync(db, actorId, workspaceId, ct) is { } failure)
        {
            return failure;
        }

        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null)
        {
            return new Failure(ResultStatus.NotFound, "There is no account with this email yet. Ask them to sign up first.");
        }

        if (await db.Memberships.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == user.Id, ct))
        {
            return Fail.Invalid($"{user.DisplayName} is already a member.");
        }

        db.Memberships.Add(new Membership { WorkspaceId = workspaceId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync(ct);
        await LogForWorkspaceAsync(db, workspaceId, actorId, ActivityKind.MemberAdded, $"added {user.DisplayName} as {role.ToString().ToLowerInvariant()}", ct);
        return Result<MemberView>.Ok(new MemberView(user.Id, user.DisplayName, user.Email, role));
    }

    public async Task<Result<Done>> ChangeRoleAsync(string actorId, Guid workspaceId, string userId, Role role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await OwnerCheckAsync(db, actorId, workspaceId, ct) is { } failure)
        {
            return failure;
        }

        var membership = await db.Memberships.Include(m => m.User).SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);
        if (membership is null)
        {
            return Fail.NotFound("Member");
        }

        if (membership.Role == Role.Owner && role != Role.Owner && await IsLastOwnerAsync(db, workspaceId, ct))
        {
            return Fail.Invalid("A workspace needs at least one owner.");
        }

        membership.Role = role;
        await db.SaveChangesAsync(ct);
        await LogForWorkspaceAsync(db, workspaceId, actorId, ActivityKind.MemberRoleChanged,
            $"made {membership.User!.DisplayName} {(role == Role.Owner ? "an owner" : "a " + role.ToString().ToLowerInvariant())}", ct);
        return Result<Done>.Ok(default);
    }

    public async Task<Result<Done>> RemoveMemberAsync(string actorId, Guid workspaceId, string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Everyone may leave a workspace; removing others needs an owner.
        if (userId != actorId && await OwnerCheckAsync(db, actorId, workspaceId, ct) is { } failure)
        {
            return failure;
        }

        var membership = await db.Memberships.Include(m => m.User).SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);
        if (membership is null)
        {
            return Fail.NotFound("Member");
        }

        if (membership.Role == Role.Owner && await IsLastOwnerAsync(db, workspaceId, ct))
        {
            return Fail.Invalid("A workspace needs at least one owner.");
        }

        db.Memberships.Remove(membership);
        // Their cards stay, unassigned.
        await db.Cards.Where(c => c.AssigneeId == userId && db.Boards.Any(b => b.Id == c.BoardId && b.WorkspaceId == workspaceId))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AssigneeId, (string?)null), ct);
        await db.SaveChangesAsync(ct);
        await LogForWorkspaceAsync(db, workspaceId, actorId, ActivityKind.MemberRemoved, $"removed {membership.User!.DisplayName}", ct);
        return Result<Done>.Ok(default);
    }

    // ---------------------------------------------------------------------------------------------
    // Invite links

    /// <summary>The workspace's invite code, created on first use. Owners only.</summary>
    public async Task<Result<string>> GetOrCreateInviteAsync(string actorId, Guid workspaceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await OwnerCheckAsync(db, actorId, workspaceId, ct) is { } failure)
        {
            return failure;
        }

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, ct);
        workspace.InviteCode ??= NewInviteCode();
        await db.SaveChangesAsync(ct);
        return Result<string>.Ok(workspace.InviteCode);
    }

    /// <summary>Switches the invite link off; a new one can be created later.</summary>
    public async Task<Result<Done>> RevokeInviteAsync(string actorId, Guid workspaceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await OwnerCheckAsync(db, actorId, workspaceId, ct) is { } failure)
        {
            return failure;
        }

        await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.InviteCode, (string?)null), ct);
        return Result<Done>.Ok(default);
    }

    /// <summary>Joins the workspace behind an invite code as a member (or does nothing if already in it).</summary>
    /// <returns>The first board of the workspace, if it has one.</returns>
    public async Task<Result<Guid?>> JoinByInviteAsync(string actorId, string code, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var workspace = string.IsNullOrWhiteSpace(code) ? null : await db.Workspaces.SingleOrDefaultAsync(w => w.InviteCode == code, ct);
        if (workspace is null)
        {
            return new Failure(ResultStatus.NotFound, "This invite link is not valid anymore. Ask for a new one.");
        }

        if (!await db.Memberships.AnyAsync(m => m.WorkspaceId == workspace.Id && m.UserId == actorId, ct))
        {
            db.Memberships.Add(new Membership { WorkspaceId = workspace.Id, UserId = actorId, Role = Role.Member });
            await db.SaveChangesAsync(ct);
            await LogForWorkspaceAsync(db, workspace.Id, actorId, ActivityKind.MemberAdded, "joined the workspace via the invite link", ct);
        }

        var firstBoard = await db.Boards.Where(b => b.WorkspaceId == workspace.Id).OrderBy(b => b.CreatedAt).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        return Result<Guid?>.Ok(firstBoard);
    }

    /// <summary>Whether the invite belongs to a demo workspace (then visitors may join as guests without an account).</summary>
    public async Task<bool> IsDemoInviteAsync(string code, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Workspaces.AnyAsync(w => w.InviteCode == code && w.IsDemo, ct);
    }

    private static string NewInviteCode() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');

    // ---------------------------------------------------------------------------------------------
    // Helpers

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength ? null : trimmed;
    }

    private static async Task<Role?> RoleInAsync(FlowboardDbContext db, string actorId, Guid workspaceId, CancellationToken ct) =>
        await db.Memberships.Where(m => m.WorkspaceId == workspaceId && m.UserId == actorId)
            .Select(m => (Role?)m.Role).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Loads a board the actor may access with at least <paramref name="needed"/>. Boards in workspaces
    /// the actor is not part of are reported as "not found", so their existence is not revealed.
    /// </summary>
    private static async Task<(Board? Board, Failure Failure)> BoardForAsync(
        FlowboardDbContext db, string actorId, Guid boardId, Role needed, CancellationToken ct)
    {
        var board = await db.Boards.SingleOrDefaultAsync(b => b.Id == boardId, ct);
        var role = board is null ? null : await RoleInAsync(db, actorId, board.WorkspaceId, ct);
        if (board is null || role is null)
        {
            return (null, Fail.NotFound("Board"));
        }

        return role < needed ? (null, Fail.Forbidden()) : (board, default);
    }

    private static async Task<Failure?> OwnerCheckAsync(FlowboardDbContext db, string actorId, Guid workspaceId, CancellationToken ct)
    {
        var role = await RoleInAsync(db, actorId, workspaceId, ct);
        if (role is null)
        {
            return Fail.NotFound("Workspace");
        }

        return role == Role.Owner ? null : Fail.Forbidden("Only owners can manage members.");
    }

    private static async Task<bool> IsLastOwnerAsync(FlowboardDbContext db, Guid workspaceId, CancellationToken ct) =>
        await db.Memberships.CountAsync(m => m.WorkspaceId == workspaceId && m.Role == Role.Owner, ct) <= 1;

    private static async Task<IReadOnlyList<MemberView>> MembersAsync(FlowboardDbContext db, Guid workspaceId, CancellationToken ct) =>
        await db.Memberships.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderByDescending(m => m.Role).ThenBy(m => m.User!.DisplayName)
            .Select(m => new MemberView(m.UserId, m.User!.DisplayName, m.User.Email, m.Role))
            .ToListAsync(ct);

    private static async Task<string> RankForPositionAsync(FlowboardDbContext db, Card card, Guid columnId, int index, CancellationToken ct)
    {
        var others = await db.Cards.AsNoTracking()
            .Where(c => c.ColumnId == columnId && c.Id != card.Id)
            .Select(c => new { c.Id, c.Rank })
            .ToListAsync(ct);
        var ordered = others.OrderBy(c => c.Rank, StringComparer.Ordinal).ToList();
        index = Math.Clamp(index, 0, ordered.Count);
        var rank = Rank.Between(index > 0 ? ordered[index - 1].Rank : null, index < ordered.Count ? ordered[index].Rank : null);
        if (rank.Length <= Rank.RebalanceLength)
        {
            return rank;
        }

        // Keys grew long after many inserts at the same spot: renumber the column, then place the card.
        var fresh = Rank.Spread(ordered.Count + 1);
        var ids = ordered.Select(c => c.Id).ToList();
        ids.Insert(index, card.Id);
        // Park the existing keys first (the moving card too, if it is already in this column), so the unique
        // index never sees a duplicate mid-update.
        await db.Cards.Where(c => c.ColumnId == columnId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Rank, c => "~" + c.Id.ToString()), ct);
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] != card.Id)
            {
                var id = ids[i];
                var key = fresh[i];
                await db.Cards.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Rank, key), ct);
            }
        }

        return fresh[index];
    }

    /// <summary>
    /// Places a card in a column. The column row is locked for the length of the transaction, so people
    /// adding or moving cards into the same column at the same moment take turns: each one computes its
    /// rank from the order the previous one left behind, and no two cards can get the same rank.
    /// </summary>
    private static async Task SaveInColumnAsync(FlowboardDbContext db, Guid columnId, Card card, Func<Task<string>> rank, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Columns" WHERE "Id" = {columnId} FOR UPDATE""", ct);
        card.Rank = await rank();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<CardView> CardViewAsync(FlowboardDbContext db, Guid cardId, CancellationToken ct) =>
        await db.Cards.AsNoTracking().Where(c => c.Id == cardId)
            .Select(c => new CardView(c.Id, c.ColumnId, c.Title, c.Description, c.Rank,
                c.Assignee == null ? null : new PersonView(c.Assignee.Id, c.Assignee.DisplayName),
                c.DueDate, c.Labels, c.Version, c.UpdatedAt))
            .SingleAsync(ct);

    private static async Task<Result<CardView>> ConflictAsync(FlowboardDbContext db, Guid cardId, CancellationToken ct)
    {
        var who = await db.Activities.Where(a => a.CardId == cardId).OrderByDescending(a => a.Id)
            .Select(a => a.Actor!.DisplayName).FirstOrDefaultAsync(ct);
        return Fail.Conflict($"{who ?? "Someone"} changed this card while you were editing. Your changes were not saved; the card now shows the latest version.");
    }

    private async Task LogAsync(FlowboardDbContext db, Guid boardId, Guid? cardId, string actorId, ActivityKind kind, string text, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        db.Activities.Add(new Activity { BoardId = boardId, CardId = cardId, ActorId = actorId, Kind = kind, Text = Truncate(text, 500) });
        await db.SaveChangesAsync(ct);
        var name = await db.Users.Where(u => u.Id == actorId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? "Someone";
        await events.PublishAsync(new BoardChange(boardId, kind, cardId, actorId, name, text), ct);
    }

    private async Task LogForWorkspaceAsync(FlowboardDbContext db, Guid workspaceId, string actorId, ActivityKind kind, string text, CancellationToken ct)
    {
        foreach (var boardId in await db.Boards.Where(b => b.WorkspaceId == workspaceId).Select(b => b.Id).ToListAsync(ct))
        {
            await LogAsync(db, boardId, null, actorId, kind, text, ct);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}
