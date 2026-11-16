using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Gozleme.Api.Organizations;

/// <summary>Organization composition (GOZ-0002): validated options and the per-account redemption rate limit.</summary>
internal static class OrganizationServiceCollectionExtensions
{
    public const string RedeemRateLimit = "invitation-redeem";

    public static WebApplicationBuilder AddGozlemeOrganizations(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<OrganizationOptions>().Bind(builder.Configuration.GetSection(OrganizationOptions.Section))
            .Validate(o => Validator.TryValidateObject(o, new ValidationContext(o), null, validateAllProperties: true), $"{OrganizationOptions.Section} values are outside their safe bounds.")
            .ValidateOnStart();
        var options = builder.Configuration.GetSection(OrganizationOptions.Section).Get<OrganizationOptions>() ?? new OrganizationOptions();

        // Redemption requires a session, so the partition is the account, not the client address.
        builder.Services.Configure<RateLimiterOptions>(limits => limits.AddPolicy(RedeemRateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.RedeemPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            })));
        return builder;
    }
}
