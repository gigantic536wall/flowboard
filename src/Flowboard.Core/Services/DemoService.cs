using Flowboard.Core.Data;
using Flowboard.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Flowboard.Core.Services;

/// <summary>
/// "Try it without signing up": a guest account with a team board full of sample cards, and an invite
/// link that lets a second browser join as another guest to see the live updates. Everything demo is
/// removed after a day.
/// </summary>
public sealed class DemoService(IDbContextFactory<FlowboardDbContext> factory, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private static readonly string[] Adjectives = ["Swift", "Calm", "Bright", "Clever", "Brave", "Lucky", "Quiet", "Sunny"];
    private static readonly string[] Animals = ["Otter", "Falcon", "Panda", "Lynx", "Heron", "Koala", "Fox", "Dolphin"];

    /// <summary>A new guest (not yet signed in) for a fresh demo workspace or for joining one.</summary>
    public async Task<ApplicationUser> CreateGuestAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var guest = NewUser($"{Pick(Adjectives)} {Pick(Animals)}", isGuest: true);
        db.Users.Add(guest);
        await db.SaveChangesAsync(ct);
        return guest;
    }

    /// <summary>A demo workspace for <paramref name="guestId"/> with a sample board. Returns the board id.</summary>
    public async Task<Guid> CreateDemoBoardAsync(string guestId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // Two sample colleagues, so cards can show assignees. They cannot sign in.
        var mia = NewUser("Mia Hoffmann", isGuest: false);
        var leo = NewUser("Leo Rossi", isGuest: false);
        db.Users.AddRange(mia, leo);

        var workspace = new Workspace { Name = "Demo team", IsDemo = true, InviteCode = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()[..20] };
        workspace.Members.Add(new Membership { UserId = guestId, Role = Role.Owner });
        workspace.Members.Add(new Membership { UserId = mia.Id, Role = Role.Member });
        workspace.Members.Add(new Membership { UserId = leo.Id, Role = Role.Member });
        db.Workspaces.Add(workspace);

        var board = new Board { WorkspaceId = workspace.Id, Name = "Website relaunch" };
        db.Boards.Add(board);
        string[] columnNames = ["Backlog", "In progress", "Review", "Done"];
        var columnRanks = Rank.Spread(columnNames.Length);
        var columns = columnNames.Select((name, i) => new Column { BoardId = board.Id, Name = name, Rank = columnRanks[i] }).ToList();
        columns[1].WipLimit = 3;
        db.Columns.AddRange(columns);

        (int Column, string Title, string Description, ApplicationUser? Assignee, int? DueInDays, Labels Labels)[] cards =
        [
            (0, "Collect customer quotes for the homepage", "Ask the sales team for three short quotes.", null, 9, Labels.Yellow),
            (0, "Cookie banner: decline by default", "GDPR: non-essential cookies stay off until the visitor agrees.", leo, null, Labels.Red),
            (0, "German translation of the pricing page", "", mia, 14, Labels.Blue),
            (1, "New navigation on mobile", "Hamburger menu with the three main sections.", mia, 3, Labels.Purple),
            (1, "Optimise hero images", "WebP, max 200 KB each.", leo, -1, Labels.Green),
            (2, "Contact form validation", "Show errors next to the fields, not only at the top.", leo, 1, Labels.Orange),
            (3, "Set up analytics without cookies", "", mia, null, Labels.Green),
            (3, "Choose the new colour palette", "", null, null, Labels.Purple),
        ];
        foreach (var group in cards.GroupBy(c => c.Column))
        {
            var ranks = Rank.Spread(group.Count());
            var i = 0;
            foreach (var c in group)
            {
                db.Cards.Add(new Card
                {
                    BoardId = board.Id,
                    ColumnId = columns[c.Column].Id,
                    Title = c.Title,
                    Description = c.Description,
                    Rank = ranks[i++],
                    AssigneeId = c.Assignee?.Id,
                    DueDate = c.DueInDays is { } d ? today.AddDays(d) : null,
                    Labels = c.Labels,
                });
            }
        }

        db.Activities.Add(new Activity { BoardId = board.Id, ActorId = mia.Id, Kind = ActivityKind.BoardCreated, Text = "created the board “Website relaunch”", At = clock.GetUtcNow().AddMinutes(-42) });
        db.Activities.Add(new Activity { BoardId = board.Id, ActorId = leo.Id, Kind = ActivityKind.CardMoved, Text = "moved “Contact form validation” to Review", At = clock.GetUtcNow().AddMinutes(-7) });
        await db.SaveChangesAsync(ct);
        return board.Id;
    }

    /// <summary>Deletes demo workspaces and demo people older than <see cref="Lifetime"/>.</summary>
    public async Task<int> CleanupAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = clock.GetUtcNow() - Lifetime;
        // Workspaces first (cascades to boards, cards, history), then the people.
        await db.Workspaces.Where(w => w.IsDemo && w.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        return await db.Users.Where(u => u.IsDemo && u.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private ApplicationUser NewUser(string name, bool isGuest)
    {
        var id = Guid.NewGuid().ToString("N");
        // Demo people get an address on a reserved domain (.invalid) that can never receive mail.
        var email = $"{(isGuest ? "guest" : "sample")}-{id[..12]}@demo.flowboard.invalid";
        return new ApplicationUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = name,
            IsDemo = true,
            CreatedAt = clock.GetUtcNow(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
    }

    private static string Pick(string[] items) => items[Random.Shared.Next(items.Length)];
}
