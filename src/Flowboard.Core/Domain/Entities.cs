using Microsoft.AspNetCore.Identity;

namespace Flowboard.Core.Domain;

/// <summary>A person who can sign in.</summary>
public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Demo accounts are created by "Try it without signing up" and removed after a day.</summary>
    public bool IsDemo { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What a member of a workspace may do. Ordered: every role includes the ones below it.</summary>
public enum Role
{
    /// <summary>Read the boards, see changes live.</summary>
    Viewer = 0,

    /// <summary>Create, edit and move cards and columns.</summary>
    Member = 1,

    /// <summary>Everything, plus managing members, renaming and deleting boards.</summary>
    Owner = 2,
}

/// <summary>A team: boards and the people who work on them.</summary>
public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Membership> Members { get; } = [];
    public List<Board> Boards { get; } = [];
}

public sealed class Membership
{
    public Guid WorkspaceId { get; set; }
    public required string UserId { get; set; }
    public Role Role { get; set; }
    public ApplicationUser? User { get; set; }
}

public sealed class Board
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Workspace? Workspace { get; set; }
    public List<Column> Columns { get; } = [];
}

public sealed class Column
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public required string Name { get; set; }
    public required string Rank { get; set; }

    /// <summary>Optional work-in-progress limit: the board warns when a column holds more cards.</summary>
    public int? WipLimit { get; set; }

    public List<Card> Cards { get; } = [];
}

/// <summary>Colour labels a card can carry (any combination).</summary>
[Flags]
public enum Labels
{
    None = 0,
    Red = 1,
    Orange = 2,
    Yellow = 4,
    Green = 8,
    Blue = 16,
    Purple = 32,
}

public sealed class Card
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Guid ColumnId { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = string.Empty;
    public required string Rank { get; set; }
    public string? AssigneeId { get; set; }
    public DateOnly? DueDate { get; set; }
    public Labels Labels { get; set; }

    /// <summary>Optimistic concurrency: incremented on every change, checked when saving an edit.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ApplicationUser? Assignee { get; set; }
}

public enum ActivityKind
{
    BoardCreated,
    BoardRenamed,
    ColumnCreated,
    ColumnRenamed,
    ColumnDeleted,
    CardCreated,
    CardUpdated,
    CardMoved,
    CardDeleted,
    MemberAdded,
    MemberRoleChanged,
    MemberRemoved,
}

/// <summary>One line of a board's history: who did what, when.</summary>
public sealed class Activity
{
    public long Id { get; set; }
    public Guid BoardId { get; set; }
    public Guid? CardId { get; set; }
    public required string ActorId { get; set; }
    public ActivityKind Kind { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public ApplicationUser? Actor { get; set; }
}
