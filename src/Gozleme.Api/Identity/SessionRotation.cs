using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Identity;

/// <summary>
/// Signs an account in under a new session key, retiring the session the request arrived with
/// (session fixation protection, GOZ-0003-OBS-01). The request's cookie handler remembers the key it
/// authenticated and, on sign-in, only renews that key, and its sliding refresh would re-issue it at
/// response time. So the presented session is ended through that handler, which also stops its
/// refresh, and the new session is issued through a fresh handler that holds no key.
/// </summary>
internal sealed class SessionRotation(
    SignInManager<GozlemeUser> signIn,
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitor<CookieAuthenticationOptions> cookies)
{
    public async Task SignInWithNewSessionAsync(HttpContext http, GozlemeUser user)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);

        // Deletes the presented session's row and appends a cookie deletion, which the new session
        // cookie replaces below: the response carries exactly one session cookie.
        await http.SignOutAsync(IdentityConstants.ApplicationScheme);
        var cookieName = cookies.Get(IdentityConstants.ApplicationScheme).Cookie.Name!;
        http.Response.Headers.SetCookie = new([.. http.Response.Headers.SetCookie.Where(c => c is not null && !c.StartsWith(cookieName, StringComparison.Ordinal))]);

        var scheme = await schemes.GetSchemeAsync(IdentityConstants.ApplicationScheme)
            ?? throw new InvalidOperationException("The Identity application cookie scheme is not registered.");
        var handler = (IAuthenticationSignInHandler)(http.RequestServices.GetService(scheme.HandlerType)
            ?? ActivatorUtilities.CreateInstance(http.RequestServices, scheme.HandlerType));
        await handler.InitializeAsync(scheme, http);
        await handler.SignInAsync(principal, new AuthenticationProperties { IsPersistent = false });
        http.User = principal;
    }
}
