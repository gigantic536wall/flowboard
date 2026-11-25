using System.Threading.RateLimiting;
using Flowboard.Core.Domain;
using Flowboard.Core.Services;
using Microsoft.AspNetCore.Identity;

namespace Flowboard.Web;

/// <summary>"Try it without signing up" and invite links. Plain endpoints, because signing in needs an HTTP response.</summary>
public static class DemoEndpoints
{
    public const string RateLimitPolicy = "demo";

    public static IServiceCollection AddDemo(this IServiceCollection services)
    {
        // Creating demo data costs a few inserts: at most 10 per minute from one address.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });
        services.AddHostedService<DemoCleanup>();
        return services;
    }

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/demo", async (DemoService demo, SignInManager<ApplicationUser> signIn, CancellationToken ct) =>
        {
            var guest = await demo.CreateGuestAsync(ct);
            var board = await demo.CreateDemoBoardAsync(guest.Id, ct);
            await signIn.SignInAsync(guest, isPersistent: true);
            return Results.Redirect($"/boards/{board}");
        }).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();

        app.MapGet("/join/{code}", async (string code, HttpContext http, BoardService boards, DemoService demo,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, CancellationToken ct) =>
        {
            var userId = users.GetUserId(http.User);
            if (userId is null)
            {
                if (!await boards.IsDemoInviteAsync(code, ct))
                {
                    // Real teams: sign in (or register) first, then come back to this link.
                    return Results.Redirect($"/Account/Login?ReturnUrl={Uri.EscapeDataString($"/join/{code}")}");
                }

                // Demo teams: join as a new guest, so the live updates can be tried with a second browser.
                var guest = await demo.CreateGuestAsync(ct);
                await signIn.SignInAsync(guest, isPersistent: true);
                userId = guest.Id;
            }

            var result = await boards.JoinByInviteAsync(userId, code, ct);
            if (!result.IsOk)
            {
                return Results.Redirect("/?invite=invalid");
            }

            return Results.Redirect(result.Value is { } board ? $"/boards/{board}" : "/");
        }).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
    }
}

/// <summary>Removes demo workspaces and guests older than a day, once an hour.</summary>
public sealed partial class DemoCleanup(IServiceScopeFactory scopes, ILogger<DemoCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<DemoService>().CleanupAsync(stoppingToken);
                if (removed > 0)
                {
                    LogRemoved(removed);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} expired demo accounts")]
    private partial void LogRemoved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo cleanup failed")]
    private partial void LogFailed(Exception e);
}
