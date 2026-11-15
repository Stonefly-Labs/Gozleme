using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Identity;

/// <summary>
/// Per-request check that the session's account still exists and its security stamp is current
/// (defence in depth behind session-row revocation). Unlike Identity's stock validator it never
/// re-issues the cookie, so an ordinary request writes nothing.
/// </summary>
internal static class SessionPrincipalValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var users = services.GetRequiredService<UserManager<GozlemeUser>>();
        var stampClaim = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var user = context.Principal is { } principal ? await users.GetUserAsync(principal) : null;
        var stamp = context.Principal?.FindFirstValue(stampClaim);
        if (user is null || stamp is null || !string.Equals(stamp, await users.GetSecurityStampAsync(user), StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }
}
