using System.ComponentModel.DataAnnotations;

namespace Gozleme.Api.Organizations;

/// <summary>Operator-tunable organization settings (<c>Gozleme:Organizations</c>), validated within safe bounds at start-up.</summary>
internal sealed class OrganizationOptions
{
    public const string Section = "Gozleme:Organizations";

    /// <summary>Lifetime of a single-use invitation code (FR-008; default 7 days).</summary>
    [Range(1, 720)]
    public int InvitationExpiryHours { get; set; } = 168;

    /// <summary>Redemption attempts per account per minute (FR-011, research R7).</summary>
    [Range(1, 100)]
    public int RedeemPerMinute { get; set; } = 10;
}
