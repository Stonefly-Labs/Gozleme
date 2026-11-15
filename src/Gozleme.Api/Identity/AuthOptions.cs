using System.ComponentModel.DataAnnotations;

namespace Gozleme.Api.Identity;

/// <summary>Operator-tunable identity settings (<c>Gozleme:Auth</c>), validated within safe bounds at start-up.</summary>
internal sealed class AuthOptions
{
    public const string Section = "Gozleme:Auth";

    /// <summary>Self-registration (FR-006); on by default, operators may close it.</summary>
    public bool RegistrationEnabled { get; set; } = true;

    /// <summary>Mark the session cookie <c>Secure</c>; only plain-HTTP local Compose turns this off.</summary>
    public bool SecureCookies { get; set; } = true;

    [Range(5, 120)]
    public int IdleTimeoutMinutes { get; set; } = 30;

    [Range(1, 24)]
    public int AbsoluteTimeoutHours { get; set; } = 12;

    [Required]
    public LockoutSettings Lockout { get; set; } = new();

    [Required]
    public RateLimitSettings RateLimit { get; set; } = new();

    internal sealed class LockoutSettings
    {
        [Range(3, 10)]
        public int MaxFailedAttempts { get; set; } = 5;

        [Range(1, 1440)]
        public int Minutes { get; set; } = 15;
    }

    internal sealed class RateLimitSettings
    {
        [Range(1, 1000)]
        public int SignInPerMinute { get; set; } = 20;

        [Range(1, 1000)]
        public int RegisterPerMinute { get; set; } = 10;

        [Range(1, 100_000)]
        public int GlobalPerMinute { get; set; } = 300;
    }
}

/// <summary>Reverse-proxy trust (<c>Gozleme:Network</c>): only these peers may supply <c>X-Forwarded-For</c>.</summary>
internal sealed class NetworkOptions
{
    public const string Section = "Gozleme:Network";

    /// <summary>Comma-separated CIDR list. Empty means loopback only, so all clients share the proxy's address (fails safe).</summary>
    public string TrustedProxies { get; set; } = string.Empty;
}
