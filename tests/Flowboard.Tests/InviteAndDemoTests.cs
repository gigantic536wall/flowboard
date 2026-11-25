using Flowboard.Core.Domain;
using Flowboard.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Flowboard.Tests;

public class InviteAndDemoTests(DatabaseFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task An_invite_link_adds_people_as_members_and_can_be_turned_off()
    {
        var ana = await PersonAsync("Ana");
        var ben = await PersonAsync("Ben");
        var (workspace, boardId, _) = await BoardAsync(ana);

        Assert.Equal(ResultStatus.NotFound, (await Service.GetOrCreateInviteAsync(ben, workspace)).Status); // not in the workspace
        var code = (await Service.GetOrCreateInviteAsync(ana, workspace)).Value!;
        Assert.Equal(code, (await Service.GetOrCreateInviteAsync(ana, workspace)).Value); // stable until revoked

        var joined = await Service.JoinByInviteAsync(ben, code);
        Assert.Equal(boardId, joined.Value);
        Assert.Equal(Role.Member, (await Service.GetBoardAsync(ben, boardId)).Value!.MyRole);
        Assert.True((await Service.JoinByInviteAsync(ben, code)).IsOk); // joining twice is harmless

        await Service.RevokeInviteAsync(ana, workspace);
        var eve = await PersonAsync("Eve");
        Assert.Equal(ResultStatus.NotFound, (await Service.JoinByInviteAsync(eve, code)).Status);
        Assert.Equal(ResultStatus.NotFound, (await Service.JoinByInviteAsync(eve, "")).Status);
    }

    [Fact]
    public async Task The_demo_gives_a_guest_a_full_board_with_an_invite_for_a_second_guest()
    {
        var demo = new DemoService(Fixture.Factory, TimeProvider.System);
        var guest = await demo.CreateGuestAsync();
        var boardId = await demo.CreateDemoBoardAsync(guest.Id);

        var board = (await Service.GetBoardAsync(guest.Id, boardId)).Value!;
        Assert.Equal(Role.Owner, board.MyRole);
        Assert.Equal(["Backlog", "In progress", "Review", "Done"], board.Columns.Select(c => c.Name));
        Assert.Equal(8, board.Columns.Sum(c => c.Cards.Count));
        Assert.Equal(3, board.Members.Count);
        Assert.NotNull(board.DemoInviteCode);
        Assert.True(await Service.IsDemoInviteAsync(board.DemoInviteCode));

        var second = await demo.CreateGuestAsync();
        Assert.Equal(boardId, (await Service.JoinByInviteAsync(second.Id, board.DemoInviteCode)).Value);
        Assert.NotEqual(guest.DisplayName + guest.Id, second.DisplayName + second.Id);
    }

    [Fact]
    public async Task Real_workspaces_are_not_demo_invites()
    {
        var ana = await PersonAsync("Ana");
        var (workspace, _, _) = await BoardAsync(ana);
        var code = (await Service.GetOrCreateInviteAsync(ana, workspace)).Value!;
        Assert.False(await Service.IsDemoInviteAsync(code));
    }

    [Fact]
    public async Task Demo_data_is_removed_after_a_day_but_real_data_stays()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var demo = new DemoService(Fixture.Factory, clock);
        var guest = await demo.CreateGuestAsync();
        await demo.CreateDemoBoardAsync(guest.Id);
        // A guest's own workspace is demo data as well.
        await Service.CreateWorkspaceAsync(guest.Id, "Guest's own");
        var ana = await PersonAsync("Ana");
        await BoardAsync(ana);

        await using (var db = Db())
        {
            // The guest's own workspace was created "now" by the real clock; age everything demo.
            await db.Workspaces.Where(w => w.IsDemo).ExecuteUpdateAsync(s => s.SetProperty(w => w.CreatedAt, clock.GetUtcNow()));
        }

        Assert.Equal(0, await demo.CleanupAsync()); // too young
        clock.Advance(DemoService.Lifetime + TimeSpan.FromMinutes(1));
        Assert.Equal(3, await demo.CleanupAsync()); // guest + two sample colleagues

        await using var check = Db();
        Assert.Equal(["Acme"], await check.Workspaces.Select(w => w.Name).ToListAsync());
        Assert.Equal(["Ana"], await check.Users.Select(u => u.DisplayName).ToListAsync());
        Assert.Equal(1, await check.Boards.CountAsync());
    }
}
