using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Identity;

/// <summary>
/// Only the application cookie scheme exists (no external or two-factor schemes, FR-028), so
/// sign-out signs out that scheme only.
/// </summary>
internal sealed class GozlemeSignInManager(
    UserManager<GozlemeUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<GozlemeUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<GozlemeUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<GozlemeUser> confirmation)
    : SignInManager<GozlemeUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override Task SignOutAsync() => Context.SignOutAsync(IdentityConstants.ApplicationScheme);
}
