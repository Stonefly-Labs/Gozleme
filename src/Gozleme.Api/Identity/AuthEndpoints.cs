using System.Globalization;
using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Identity;

/// <summary>
/// <c>/api/v1/auth/*</c> (contracts/auth-api.openapi.yaml). Failures never reveal whether an
/// account exists or is locked: wrong password, unknown email and lockout answer identically.
/// </summary>
internal static class AuthEndpoints
{
    public const int MaxEmailLength = 254;
    private const long MaxBodyBytes = 4 * 1024;

    // Verifying against a throwaway hash equalises timing when no real hash is checked.
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<GozlemeUser>().HashPassword(new GozlemeUser(), Guid.NewGuid().ToString("N")));

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        auth.MapGet("/options", (IOptions<AuthOptions> options) =>
            TypedResults.Ok(new AuthOptionsResponse(options.Value.RegistrationEnabled, [])));

        auth.MapPost("/register", RegisterAsync).AddEndpointFilter<WebClientHeaderFilter>()
            .RequireRateLimiting(IdentityServiceCollectionExtensions.RegisterRateLimit).WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        auth.MapPost("/sign-in", SignInAsync).AddEndpointFilter<WebClientHeaderFilter>()
            .RequireRateLimiting(IdentityServiceCollectionExtensions.SignInRateLimit).WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        auth.MapPost("/sign-out", SignOutAsync).AddEndpointFilter<WebClientHeaderFilter>();
        auth.MapPost("/sign-out-all", SignOutAllAsync).AddEndpointFilter<WebClientHeaderFilter>().RequireAuthorization();
        auth.MapGet("/session", SessionAsync).RequireAuthorization();
        return app;
    }

    private static async Task<Results<Created<SessionResponse>, JsonHttpResult<AuthError>>> RegisterAsync(
        CredentialsRequest request,
        HttpContext http,
        UserManager<GozlemeUser> users,
        SignInManager<GozlemeUser> signIn,
        DbTicketStore sessions,
        IOptions<AuthOptions> options,
        SecurityAudit audit,
        TimeProvider time)
    {
        using var activity = AuthTelemetry.Start(AuthOperation.Register);
        if (!options.Value.RegistrationEnabled)
        {
            AuthTelemetry.Failed(activity, AuthOperation.Register, AuthFailure.RegistrationClosed);
            audit.RegistrationRefused(AuthFailure.RegistrationClosed);
            return Error(StatusCodes.Status403Forbidden, AuthError.RegistrationClosed);
        }

        var email = NormalizeEmail(request.Email);
        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (email is null)
        {
            fields["email"] = ["Enter a valid email address."];
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < GozlemePasswordValidator.MinLength)
        {
            fields["password"] = [$"Use at least {GozlemePasswordValidator.MinLength} characters."];
        }
        else if (request.Password.Length > GozlemePasswordValidator.MaxLength)
        {
            fields["password"] = [$"Use at most {GozlemePasswordValidator.MaxLength} characters."];
        }

        if (fields.Count > 0)
        {
            return ValidationFailed(activity, audit, fields);
        }

        var now = time.GetUtcNow();
        var user = new GozlemeUser { Id = Guid.CreateVersion7(now), Email = email, UserName = email, CreatedAt = now, LockoutEnabled = true };
        IdentityResult result;
        try
        {
            // Hashes and validates the password before the uniqueness check, so timing does not single out duplicates.
            result = await users.CreateAsync(user, request.Password!);
        }
        catch (DbUpdateException)
        {
            result = IdentityResult.Failed(users.ErrorDescriber.DuplicateEmail(email!)); // concurrent duplicate
        }

        if (!result.Succeeded)
        {
            var passwordErrors = result.Errors.Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal)).Select(e => e.Description).ToArray();
            if (passwordErrors.Length > 0)
            {
                return ValidationFailed(activity, audit, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["password"] = passwordErrors });
            }

            if (result.Errors.Any(e => e.Code is "InvalidEmail" or "InvalidUserName"))
            {
                return ValidationFailed(activity, audit, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["email"] = ["Enter a valid email address."] });
            }

            // Duplicate (or any other store failure): generic, does not confirm the email is registered (FR-007).
            AuthTelemetry.Failed(activity, AuthOperation.Register, AuthFailure.Duplicate);
            audit.RegistrationRefused(AuthFailure.Duplicate);
            return Error(StatusCodes.Status409Conflict, AuthError.RegistrationFailed);
        }

        await sessions.RemoveCurrentAsync(http); // session fixation: never carry a prior session forward
        await signIn.SignInAsync(user, isPersistent: false);
        AuthTelemetry.Succeeded(activity, AuthOperation.Register);
        audit.Registered(user.Id);
        return TypedResults.Created((string?)null, Session(user, now, options.Value));
    }

    private static async Task<Results<Ok<SessionResponse>, JsonHttpResult<AuthError>>> SignInAsync(
        CredentialsRequest request,
        HttpContext http,
        DbTicketStore sessions,
        UserManager<GozlemeUser> users,
        SignInManager<GozlemeUser> signIn,
        IPasswordHasher<GozlemeUser> hasher,
        SessionRevoker revoker,
        IOptions<AuthOptions> options,
        SecurityAudit audit,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        using var activity = AuthTelemetry.Start(AuthOperation.SignIn);
        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? string.Empty;
        var user = email is null || password.Length > GozlemePasswordValidator.MaxLength ? null : await users.FindByEmailAsync(email);
        if (user is null)
        {
            hasher.VerifyHashedPassword(new GozlemeUser(), DummyHash.Value, password);
            return InvalidCredentials(activity, audit, email is null ? AuthFailure.Validation : AuthFailure.InvalidCredentials, null);
        }

        if (await users.IsLockedOutAsync(user))
        {
            hasher.VerifyHashedPassword(user, DummyHash.Value, password);
            return InvalidCredentials(activity, audit, AuthFailure.LockedOut, user.Id);
        }

        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            await sessions.RemoveCurrentAsync(http); // session fixation: a new session key on every sign-in
            await signIn.SignInAsync(user, isPersistent: false);
        }

        if (result.Succeeded)
        {
            var now = time.GetUtcNow();
            user.LastSignInAt = now;
            await users.UpdateAsync(user);
            AuthTelemetry.Succeeded(activity, AuthOperation.SignIn);
            audit.SignedIn(user.Id);
            return TypedResults.Ok(Session(user, now, options.Value));
        }

        if (result.IsLockedOut)
        {
            // This failure crossed the threshold: end every existing session of the account too.
            await revoker.RevokeAllAsync(user, cancellationToken);
            audit.LockedOut(user.Id);
            return InvalidCredentials(activity, audit, AuthFailure.LockedOut, user.Id);
        }

        return InvalidCredentials(activity, audit, AuthFailure.InvalidCredentials, user.Id);
    }

    private static async Task<NoContent> SignOutAsync(HttpContext http, SecurityAudit audit)
    {
        using var activity = AuthTelemetry.Start(AuthOperation.SignOut);
        var result = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var userId = UserId(result.Principal);
        await http.SignOutAsync(IdentityConstants.ApplicationScheme); // deletes the session row and the cookie
        AuthTelemetry.Succeeded(activity, AuthOperation.SignOut);
        audit.SignedOut(userId);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult>> SignOutAllAsync(
        HttpContext http, UserManager<GozlemeUser> users, SessionRevoker revoker, SecurityAudit audit, CancellationToken cancellationToken)
    {
        using var activity = AuthTelemetry.Start(AuthOperation.SignOutAll);
        var user = await users.GetUserAsync(http.User);
        if (user is null)
        {
            AuthTelemetry.Failed(activity, AuthOperation.SignOutAll, AuthFailure.Unauthenticated);
            return TypedResults.Unauthorized();
        }

        await revoker.RevokeAllAsync(user, cancellationToken);
        await http.SignOutAsync(IdentityConstants.ApplicationScheme);
        AuthTelemetry.Succeeded(activity, AuthOperation.SignOutAll);
        audit.SignedOutAll(user.Id);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<SessionResponse>, UnauthorizedHttpResult>> SessionAsync(HttpContext http, UserManager<GozlemeUser> users)
    {
        var user = await users.GetUserAsync(http.User);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var result = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var expires = result.Properties?.Items.TryGetValue(DbTicketStore.AbsoluteExpiryItem, out var value) == true
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : result.Properties?.ExpiresUtc ?? DateTimeOffset.MinValue;
        return TypedResults.Ok(new SessionResponse(user.Id, user.Email!, false, expires));
    }

    /// <summary>Trimmed, syntactically valid, at most 254 characters, no control characters; otherwise null.</summary>
    internal static string? NormalizeEmail(string? raw)
    {
        var email = raw?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > MaxEmailLength || email.Any(char.IsControl) || email.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        return MailAddress.TryCreate(email, out var address) && address.Address == email && address.Host.Contains('.', StringComparison.Ordinal) ? email : null;
    }

    private static SessionResponse Session(GozlemeUser user, DateTimeOffset now, AuthOptions options) =>
        new(user.Id, user.Email!, false, now.AddHours(options.AbsoluteTimeoutHours));

    private static Guid? UserId(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static JsonHttpResult<AuthError> InvalidCredentials(System.Diagnostics.Activity? activity, SecurityAudit audit, string category, Guid? userId)
    {
        AuthTelemetry.Failed(activity, AuthOperation.SignIn, category);
        audit.SignInFailed(category, userId);
        return Error(StatusCodes.Status401Unauthorized, AuthError.InvalidCredentials);
    }

    private static JsonHttpResult<AuthError> ValidationFailed(System.Diagnostics.Activity? activity, SecurityAudit audit, Dictionary<string, string[]> fields)
    {
        AuthTelemetry.Failed(activity, AuthOperation.Register, AuthFailure.Validation);
        audit.RegistrationRefused(AuthFailure.Validation);
        return TypedResults.Json(new AuthError(AuthError.ValidationFailed, fields), statusCode: StatusCodes.Status400BadRequest);
    }

    private static JsonHttpResult<AuthError> Error(int status, string error) => TypedResults.Json(new AuthError(error), statusCode: status);
}
