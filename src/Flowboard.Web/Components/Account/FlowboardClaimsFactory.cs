using System.Security.Claims;
using Flowboard.Core.Domain;
using Flowboard.Web.Components.Board;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Flowboard.Web.Components.Account;

/// <summary>Puts the display name into the sign-in cookie, so pages can show it without a database query.</summary>
public sealed class FlowboardClaimsFactory(UserManager<ApplicationUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(Ui.DisplayNameClaim, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? user.UserName ?? "" : user.DisplayName));
        return identity;
    }
}
