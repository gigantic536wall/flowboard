using Flowboard.Core.Data;
using Flowboard.Core.Domain;
using Flowboard.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flowboard.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Database, Identity stores and board services. Shared by the web app and the tests, so both use
    /// exactly the same model (including the Identity schema version the migrations were made with).
    /// </summary>
    public static IdentityBuilder AddFlowboardCore(this IServiceCollection services, string connectionString)
    {
        // A factory: Blazor Server components live long, so every operation gets its own short-lived context.
        services.AddDbContextFactory<FlowboardDbContext>(options => options.UseNpgsql(ConnectionStrings.Normalize(connectionString)));
        services.TryAddSingleton<IBoardEvents, NoBoardEvents>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<BoardService>();

        return services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false; // no email delivery in the demo
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddEntityFrameworkStores<FlowboardDbContext>();
    }
}
