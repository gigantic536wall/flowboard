using System.Collections.Concurrent;
using Flowboard.Core;
using Flowboard.Core.Data;
using Flowboard.Core.Domain;
using Flowboard.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flowboard.Tests;

/// <summary>
/// A real PostgreSQL test database, recreated once per test run.
/// Connection string: ConnectionStrings:Test from the web project's user secrets (local) or the
/// ConnectionStrings__Test environment variable (CI).
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly ServiceProvider _services;

    /// <summary>Contexts configured exactly like the app (same registration).</summary>
    public IDbContextFactory<FlowboardDbContext> Factory { get; }

    public DatabaseFixture()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connection = config.GetConnectionString("Test")
            ?? throw new InvalidOperationException("Set ConnectionStrings:Test (user secrets) or ConnectionStrings__Test (env).");
        _services = new ServiceCollection().AddLogging().AddFlowboardCore(connection).Services.BuildServiceProvider();
        Factory = _services.GetRequiredService<IDbContextFactory<FlowboardDbContext>>();
    }

    public async Task InitializeAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE flowboard."Activities", flowboard."Cards", flowboard."Columns", flowboard."Boards", flowboard."Memberships", flowboard."Workspaces", flowboard."AspNetUsers" CASCADE""");
    }

    Task IAsyncLifetime.DisposeAsync() => _services.DisposeAsync().AsTask();
}

[CollectionDefinition("db")]
public sealed class DatabaseGroup : ICollectionFixture<DatabaseFixture>;

/// <summary>Collects published board changes, so tests can check what live viewers would see.</summary>
public sealed class RecordingEvents : IBoardEvents
{
    public ConcurrentQueue<BoardChange> Changes { get; } = new();

    public Task PublishAsync(BoardChange change, CancellationToken ct = default)
    {
        Changes.Enqueue(change);
        return Task.CompletedTask;
    }
}

/// <summary>Base class: an empty database, a service and helpers to create people and boards.</summary>
[Collection("db")]
public abstract class DatabaseTest(DatabaseFixture fixture) : IAsyncLifetime
{
    protected DatabaseFixture Fixture { get; } = fixture;
    protected RecordingEvents Events { get; } = new();
    protected BoardService Service => new(Fixture.Factory, Events);

    public Task InitializeAsync() => Fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    protected FlowboardDbContext Db() => Fixture.Factory.CreateDbContext();

    protected async Task<string> PersonAsync(string name)
    {
        var email = $"{name.ToLowerInvariant()}@team.example";
        var user = new ApplicationUser
        {
            UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), DisplayName = name,
        };
        await using var db = Db();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A workspace owned by <paramref name="owner"/> with one board (columns To do / In progress / Done).</summary>
    protected async Task<(Guid Workspace, Guid Board, BoardView View)> BoardAsync(string owner, string name = "Launch")
    {
        var workspace = (await Service.CreateWorkspaceAsync(owner, "Acme")).Value;
        var board = (await Service.CreateBoardAsync(owner, workspace, name)).Value;
        return (workspace, board, (await Service.GetBoardAsync(owner, board)).Value!);
    }
}
