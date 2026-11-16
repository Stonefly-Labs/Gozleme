using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.RateLimiting;
using Gozleme.Api.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Gozleme.Api.Identity;

/// <summary>
/// Local identity composition (GOZ-0001): Identity core over the user-only EF store, the
/// <c>gozleme_session</c> cookie backed by <see cref="DbTicketStore"/>, a database key ring,
/// forwarded-client resolution and auth rate limits. No external authentication scheme is registered.
/// </summary>
internal static class IdentityServiceCollectionExtensions
{
    public const string CookieName = "gozleme_session";
    public const string SignInRateLimit = "sign-in";
    public const string RegisterRateLimit = "register";

    /// <summary>Accounts without HTTP: options, store, key ring, password policy. Shared by the API and operator commands.</summary>
    public static IdentityBuilder AddGozlemeAccounts(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection(AuthOptions.Section))
            .Validate(o => IsValid(o) && IsValid(o.Lockout) && IsValid(o.RateLimit), $"{AuthOptions.Section} values are outside their safe bounds.")
            .ValidateOnStart();
        services.AddOptions<NetworkOptions>().Bind(builder.Configuration.GetSection(NetworkOptions.Section));
        var auth = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();

        services.AddDbContextFactory<GozlemeDbContext>((sp, options) => GozlemeDbContext.Configure(options, sp.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        services.AddDataProtection().SetApplicationName("gozleme").PersistKeysToDbContext<GozlemeDbContext>();

        var identity = services.AddIdentityCore<GozlemeUser>(options =>
            {
                options.Password.RequiredLength = GozlemePasswordValidator.MinLength;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty; // the email is validated separately
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = auth.Lockout.MaxFailedAttempts;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(auth.Lockout.Minutes);
                options.SignIn.RequireConfirmedEmail = false; // email is unverified in this slice (FR-005)
            })
            .AddEntityFrameworkStores<GozlemeDbContext>()
            .AddPasswordValidator<GozlemePasswordValidator>();

        services.AddScoped<SessionRevoker>();
        services.AddSingleton<SecurityAudit>();
        return identity;
    }

    /// <summary>Accounts plus the session cookie, sign-in manager, forwarded-client resolution and rate limits.</summary>
    public static WebApplicationBuilder AddGozlemeIdentity(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var auth = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        builder.AddGozlemeAccounts().AddSignInManager<GozlemeSignInManager>();

        services.AddSingleton<DbTicketStore>();

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.Cookie.SecurePolicy = auth.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.None;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(auth.IdleTimeoutMinutes);
                options.SlidingExpiration = true;
                options.Events = new CookieAuthenticationEvents
                {
                    // An internal API: answer with status codes, never redirects.
                    OnRedirectToLogin = context => Status(context.Response, StatusCodes.Status401Unauthorized),
                    OnRedirectToAccessDenied = context => Status(context.Response, StatusCodes.Status403Forbidden),
                    OnValidatePrincipal = SessionPrincipalValidator.ValidateAsync,
                };
            });
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<DbTicketStore>((options, store) => options.SessionStore = store);
        services.AddAuthorization();

        services.Configure<ForwardedHeadersOptions>(options => ConfigureForwardedHeaders(options, builder.Configuration));
        services.AddRateLimiter(options => ConfigureRateLimits(options, auth.RateLimit));
        return builder;
    }

    private static bool IsValid(object? settings) =>
        settings is not null && Validator.TryValidateObject(settings, new ValidationContext(settings), null, validateAllProperties: true);

    internal static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1; // only the web tier's own hop is honoured
        var trusted = configuration.GetSection(NetworkOptions.Section).Get<NetworkOptions>()?.TrustedProxies ?? string.Empty;
        foreach (var cidr in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        }
    }

    private static void ConfigureRateLimits(RateLimiterOptions options, AuthOptions.RateLimitSettings limits)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? (int)Math.Ceiling(retryAfter.TotalSeconds) : 60;
            context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<RateLimitedOperation>() is { } labelled)
            {
                labelled.Record(System.Diagnostics.Activity.Current);
            }
            else
            {
                var operation = context.HttpContext.Request.Path.StartsWithSegments("/api/v1/auth/register", StringComparison.Ordinal)
                    ? AuthOperation.Register
                    : AuthOperation.SignIn;
                AuthTelemetry.Failed(System.Diagnostics.Activity.Current, operation, AuthFailure.RateLimited);
            }

            await context.HttpContext.Response.WriteAsJsonAsync(new AuthError(AuthFailure.RateLimited), cancellationToken);
        };

        // Backstop across all clients of this instance, so spoofed client addresses cannot evade limits entirely.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => IsCredentialEndpoint(http.Request.Path)
            ? RateLimitPartition.GetFixedWindowLimiter("auth-global", _ => Window(limits.GlobalPerMinute))
            : RateLimitPartition.GetNoLimiter("none"));
        options.AddPolicy(SignInRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => Window(limits.SignInPerMinute)));
        options.AddPolicy(RegisterRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => Window(limits.RegisterPerMinute)));
    }

    private static bool IsCredentialEndpoint(PathString path) =>
        path.StartsWithSegments("/api/v1/auth/sign-in", StringComparison.Ordinal) || path.StartsWithSegments("/api/v1/auth/register", StringComparison.Ordinal);

    private static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? IPAddress.None.ToString();

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private static Task Status(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }
}

/// <summary>Endpoint metadata: how a rate-limit rejection on this endpoint is recorded in telemetry.</summary>
internal sealed record RateLimitedOperation(Action<System.Diagnostics.Activity?> Record);
