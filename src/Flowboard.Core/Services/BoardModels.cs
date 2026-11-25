using Flowboard.Core.Domain;

namespace Flowboard.Core.Services;

public sealed record PersonView(string Id, string Name);

public sealed record CardView(
    Guid Id, Guid ColumnId, string Title, string Description, string Rank, PersonView? Assignee, DateOnly? DueDate,
    Labels Labels, int Version, DateTimeOffset UpdatedAt);

public sealed record ColumnView(Guid Id, string Name, string Rank, int? WipLimit, IReadOnlyList<CardView> Cards)
{
    public bool OverLimit => WipLimit is { } limit && Cards.Count > limit;
}

public sealed record MemberView(string UserId, string Name, string? Email, Role Role);

/// <summary>Everything a client needs to draw a board, in display order.</summary>
public sealed record BoardView(
    Guid Id, Guid WorkspaceId, string Name, string WorkspaceName, Role MyRole, IReadOnlyList<ColumnView> Columns,
    IReadOnlyList<MemberView> Members);

public sealed record BoardSummary(Guid Id, string Name, Guid WorkspaceId, string WorkspaceName, Role MyRole, int CardCount);

public sealed record WorkspaceSummary(Guid Id, string Name, Role MyRole, int MemberCount);

public sealed record ActivityView(long Id, Guid? CardId, string ActorName, ActivityKind Kind, string Text, DateTimeOffset At);

/// <summary>Changes to a card. Null fields are left as they are.</summary>
public sealed record CardEdit(
    string? Title = null, string? Description = null, string? AssigneeId = null, bool ClearAssignee = false,
    DateOnly? DueDate = null, bool ClearDueDate = false, Labels? Labels = null);

/// <summary>A change that other viewers of the board should see right away.</summary>
public sealed record BoardChange(Guid BoardId, ActivityKind Kind, Guid? CardId, string ActorId, string ActorName, string Text);

/// <summary>Publishes board changes to connected clients (implemented by the web layer).</summary>
public interface IBoardEvents
{
    Task PublishAsync(BoardChange change, CancellationToken ct = default);
}

/// <summary>Used where no live clients exist (tests, background jobs).</summary>
public sealed class NoBoardEvents : IBoardEvents
{
    public Task PublishAsync(BoardChange change, CancellationToken ct = default) => Task.CompletedTask;
}
