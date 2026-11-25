using Flowboard.Core;
using Flowboard.Core.Data;
using Flowboard.Core.Domain;
using Flowboard.Core.Services;
using Flowboard.Web;
using Flowboard.Web.Components;
using Flowboard.Web.Components.Account;
using Flowboard.Web.Realtime;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");
// Live updates: registered before AddFlowboardCore, which only adds a no-op publisher when none exists.
builder.Services.AddSignalR();
builder.Services.AddSingleton<BoardBroadcaster>();
builder.Services.AddSingleton<IBoardEvents>(sp => sp.GetRequiredService<BoardBroadcaster>());
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddFlowboardCore(connectionString)
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<FlowboardClaimsFactory>();
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddDemo();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<FlowboardDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();
app.MapHub<BoardHub>("/hubs/boards");
app.MapDemoEndpoints();
app.MapGet("/healthz", () => Results.Ok(new { ok = true })).ExcludeFromDescription();

await app.RunAsync();

/// <summary>Entry point, public so integration tests can start the app with WebApplicationFactory.</summary>
public partial class Program;
