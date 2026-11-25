using Flowboard.Core.Domain;
using Flowboard.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Flowboard.Tests;

public class BoardServiceTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    private static string[] Titles(BoardView board, int column) => [.. board.Columns[column].Cards.Select(c => c.Title)];

    [Fact]
    public async Task New_board_has_three_columns_and_its_creator_is_owner()
    {
        var ana = await PersonAsync("Ana");
        var (_, boardId, board) = await BoardAsync(ana);

        Assert.Equal(["To do", "In progress", "Done"], board.Columns.Select(c => c.Name));
        Assert.Equal(Role.Owner, board.MyRole);
        Assert.Single(board.Members);
        var history = (await Service.GetActivityAsync(ana, boardId)).Value!;
        Assert.Equal("created the board “Launch”", Assert.Single(history).Text);
        Assert.Equal(boardId, Assert.Single(Events.Changes).BoardId);
    }

    [Fact]
    public async Task Cards_are_added_at_the_bottom_and_can_be_moved_anywhere()
    {
        var ana = await PersonAsync("Ana");
        var (_, boardId, board) = await BoardAsync(ana);
        var todo = board.Columns[0].Id;
        var done = board.Columns[2].Id;
        foreach (var title in new[] { "Design", "Build", "Test" })
        {
            Assert.True((await Service.AddCardAsync(ana, todo, title)).IsOk);
        }

        board = (await Service.GetBoardAsync(ana, boardId)).Value!;
        Assert.Equal(["Design", "Build", "Test"], Titles(board, 0));

        var test = board.Columns[0].Cards[2];
        Assert.True((await Service.MoveCardAsync(ana, test.Id, todo, 0)).IsOk); // reorder to the top
        var build = board.Columns[0].Cards[1];
        Assert.True((await Service.MoveCardAsync(ana, build.Id, done, 0)).IsOk); // to another column

        board = (await Service.GetBoardAsync(ana, boardId)).Value!;
        Assert.Equal(["Test", "Design"], Titles(board, 0));
        Assert.Equal(["Build"], Titles(board, 2));
        var history = (await Service.GetActivityAsync(ana, boardId)).Value!;
        Assert.Equal("moved “Build” to Done", history[0].Text);
        Assert.Equal("reordered “Test” in To do", history[1].Text);
    }

    [Fact]
    public async Task Moving_to_the_same_spot_over_and_over_renumbers_the_column_without_losing_order()
    {
        var ana = await PersonAsync("Ana");
        var (_, boardId, board) = await BoardAsync(ana);
        var todo = board.Columns[0].Id;
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add((await Service.AddCardAsync(ana, todo, $"Card {i}")).Value!.Id);
        }

        // Always insert between the first two cards: keys grow until the column is renumbered.
        var expected = new List<string> { "Card 0", "Card 1", "Card 2", "Card 3", "Card 4", "Card 5" };
        for (var i = 0; i < 80; i++)
        {
            var card = expected[^1];
            var id = ids[int.Parse(card.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)];
            Assert.True((await Service.MoveCardAsync(ana, id, todo, 1)).IsOk);
            expected.RemoveAt(expected.Count - 1);
            expected.Insert(1, card);
        }

        board = (await Service.GetBoardAsync(ana, boardId)).Value!;
        Assert.Equal(expected, Titles(board, 0));
        Assert.All(board.Columns[0].Cards, c => Assert.True(c.Rank.Length <= Rank.RebalanceLength));
    }

    [Fact]
    public async Task Parallel_inserts_into_one_column_all_succeed()
    {
        var ana = await PersonAsync("Ana");
        var (_, boardId, board) = await BoardAsync(ana);
        var todo = board.Columns[0].Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Service.AddCardAsync(ana, todo, $"Card {i}")));

        Assert.All(results, r => Assert.True(r.IsOk, r.Error));
        board = (await Service.GetBoardAsync(ana, boardId)).Value!;
        Assert.Equal(12, board.Columns[0].Cards.Select(c => c.Rank).Distinct().Count());
    }

    [Fact]
    public async Task Edits_bump_the_version_and_a_stale_edit_is_a_conflict()
    {
        var ana = await PersonAsync("Ana");
        var ben = await PersonAsync("Ben");
        var (workspace, _, board) = await BoardAsync(ana);
        await Service.AddMemberAsync(ana, workspace, "ben@team.example", Role.Member);
        var card = (await Service.AddCardAsync(ana, board.Columns[0].Id, "Draft")).Value!;

        var saved = await Service.UpdateCardAsync(ana, card.Id, card.Version, new CardEdit(Title: "Final", Labels: Labels.Red | Labels.Blue));
        Assert.True(saved.IsOk);
        Assert.Equal(card.Version + 1, saved.Value!.Version);
        Assert.Equal(Labels.Red | Labels.Blue, saved.Value.Labels);

        // Ben still has the old version open.
        var stale = await Service.UpdateCardAsync(ben, card.Id, card.Version, new CardEdit(Title: "Ben's title"));
        Assert.Equal(ResultStatus.Conflict, stale.Status);
        Assert.StartsWith("Ana changed this card", stale.Error, StringComparison.Ordinal);
        await using var db = Db();
        Assert.Equal("Final", (await db.Cards.SingleAsync()).Title);
    }

    [Fact]
    public async Task Of_two_simultaneous_edits_exactly_one_wins()
    {
        var ana = await PersonAsync("Ana");
        var (_, _, board) = await BoardAsync(ana);
        var card = (await Service.AddCardAsync(ana, board.Columns[0].Id, "Draft")).Value!;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Service.UpdateCardAsync(ana, card.Id, card.Version, new CardEdit(Description: $"Edit {i}"))));

        Assert.Single(results, r => r.IsOk);
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(ResultStatus.Conflict, r.Status));
    }

    [Fact]
    public async Task Viewers_can_read_but_not_change_and_outsiders_do_not_even_see_the_board()
    {
        var ana = await PersonAsync("Ana");
        var vic = await PersonAsync("Vic");
        var eve = await PersonAsync("Eve");
        var (workspace, boardId, board) = await BoardAsync(ana);
        await Service.AddMemberAsync(ana, workspace, "vic@team.example", Role.Viewer);

        Assert.True((await Service.GetBoardAsync(vic, boardId)).IsOk);
        Assert.Equal(ResultStatus.Forbidden, (await Service.AddCardAsync(vic, board.Columns[0].Id, "Nope")).Status);
        Assert.Equal(ResultStatus.NotFound, (await Service.GetBoardAsync(eve, boardId)).Status);
        Assert.Equal(ResultStatus.NotFound, (await Service.AddCardAsync(eve, board.Columns[0].Id, "Nope")).Status);
        Assert.Empty(await Service.ListBoardsAsync(eve));
        Assert.Single(await Service.ListBoardsAsync(vic));
    }

    [Fact]
    public async Task Cards_can_only_be_assigned_to_workspace_members()
    {
        var ana = await PersonAsync("Ana");
        var ben = await PersonAsync("Ben");
        var eve = await PersonAsync("Eve");
        var (workspace, _, board) = await BoardAsync(ana);
        await Service.AddMemberAsync(ana, workspace, "ben@team.example", Role.Member);
        var card = (await Service.AddCardAsync(ana, board.Columns[0].Id, "Task")).Value!;

        Assert.Equal(ResultStatus.Invalid, (await Service.UpdateCardAsync(ana, card.Id, card.Version, new CardEdit(AssigneeId: eve))).Status);
        var assigned = await Service.UpdateCardAsync(ana, card.Id, card.Version, new CardEdit(AssigneeId: ben, DueDate: new DateOnly(2026, 12, 1)));
        Assert.Equal("Ben", assigned.Value!.Assignee!.Name);
        Assert.Equal(new DateOnly(2026, 12, 1), assigned.Value.DueDate);
    }

    [Fact]
    public async Task Removing_a_member_unassigns_their_cards_and_the_last_owner_must_stay()
    {
        var ana = await PersonAsync("Ana");
        var ben = await PersonAsync("Ben");
        var (workspace, _, board) = await BoardAsync(ana);
        await Service.AddMemberAsync(ana, workspace, "ben@team.example", Role.Member);
        var card = (await Service.AddCardAsync(ana, board.Columns[0].Id, "Task")).Value!;
        await Service.UpdateCardAsync(ana, card.Id, card.Version, new CardEdit(AssigneeId: ben));

        Assert.Equal(ResultStatus.Forbidden, (await Service.RemoveMemberAsync(ben, workspace, ana)).Status);
        Assert.Equal(ResultStatus.Invalid, (await Service.RemoveMemberAsync(ana, workspace, ana)).Status);
        Assert.Equal(ResultStatus.Invalid, (await Service.ChangeRoleAsync(ana, workspace, ana, Role.Member)).Status);
        Assert.True((await Service.RemoveMemberAsync(ana, workspace, ben)).IsOk);

        await using var db = Db();
        Assert.Null((await db.Cards.SingleAsync()).AssigneeId);
        Assert.Single(await Service.GetMembersAsync(ana, workspace));
    }

    [Fact]
    public async Task Adding_members_needs_an_existing_account_and_an_owner()
    {
        var ana = await PersonAsync("Ana");
        var ben = await PersonAsync("Ben");
        var (workspace, _, _) = await BoardAsync(ana);

        Assert.Equal(ResultStatus.NotFound, (await Service.AddMemberAsync(ana, workspace, "nobody@team.example", Role.Member)).Status);
        Assert.True((await Service.AddMemberAsync(ana, workspace, "  BEN@team.example ", Role.Member)).IsOk);
        Assert.Equal(ResultStatus.Invalid, (await Service.AddMemberAsync(ana, workspace, "ben@team.example", Role.Member)).Status);
        Assert.Equal(ResultStatus.Forbidden, (await Service.AddMemberAsync(ben, workspace, "ana@team.example", Role.Owner)).Status);
    }

    [Fact]
    public async Task Columns_can_be_added_renamed_limited_and_deleted_with_their_cards()
    {
        var ana = await PersonAsync("Ana");
        var (_, boardId, board) = await BoardAsync(ana);
        var review = (await Service.AddColumnAsync(ana, boardId, "Review")).Value;
        await Service.AddCardAsync(ana, review, "PR #1");
        await Service.AddCardAsync(ana, review, "PR #2");

        Assert.Equal(ResultStatus.Invalid, (await Service.UpdateColumnAsync(ana, review, "Review", 0)).Status);
        Assert.True((await Service.UpdateColumnAsync(ana, review, "Code review", 1)).IsOk);
        board = (await Service.GetBoardAsync(ana, boardId)).Value!;
        Assert.Equal("Code review", board.Columns[^1].Name);
        Assert.True(board.Columns[^1].OverLimit);

        Assert.True((await Service.DeleteColumnAsync(ana, review)).IsOk);
        var history = (await Service.GetActivityAsync(ana, boardId)).Value!;
        Assert.Equal("deleted the column “Code review” with 2 cards", history[0].Text);
        await using var db = Db();
        Assert.Equal(0, await db.Cards.CountAsync());
    }

    [Fact]
    public async Task A_board_keeps_at_least_one_column()
    {
        var ana = await PersonAsync("Ana");
        var (_, _, board) = await BoardAsync(ana);
        await Service.DeleteColumnAsync(ana, board.Columns[0].Id);
        await Service.DeleteColumnAsync(ana, board.Columns[1].Id);
        Assert.Equal(ResultStatus.Invalid, (await Service.DeleteColumnAsync(ana, board.Columns[2].Id)).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_titles_are_rejected(string title)
    {
        var ana = await PersonAsync("Ana");
        var (_, _, board) = await BoardAsync(ana);
        Assert.Equal(ResultStatus.Invalid, (await Service.AddCardAsync(ana, board.Columns[0].Id, title)).Status);
        Assert.Equal(ResultStatus.Invalid, (await Service.AddCardAsync(ana, board.Columns[0].Id, new string('x', 201))).Status);
    }

    [Fact]
    public async Task Every_change_is_published_for_live_viewers()
    {
        var ana = await PersonAsync("Ana");
        var (_, boardId, board) = await BoardAsync(ana);
        var card = (await Service.AddCardAsync(ana, board.Columns[0].Id, "Task")).Value!;
        await Service.MoveCardAsync(ana, card.Id, board.Columns[1].Id, 0);
        await Service.DeleteCardAsync(ana, card.Id);

        Assert.Equal(
            [ActivityKind.BoardCreated, ActivityKind.CardCreated, ActivityKind.CardMoved, ActivityKind.CardDeleted],
            Events.Changes.Select(c => c.Kind));
        Assert.All(Events.Changes, c => Assert.Equal("Ana", c.ActorName));
        Assert.All(Events.Changes, c => Assert.Equal(boardId, c.BoardId));
    }
}
