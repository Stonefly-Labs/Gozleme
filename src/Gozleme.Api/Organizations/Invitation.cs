namespace Gozleme.Api.Organizations;

/// <summary>
/// A single-use invitation (FR-007, FR-008). Only the SHA-256 hash of the code is stored. State is
/// derived from the timestamps: open, used, revoked or expired.
/// </summary>
internal sealed class Invitation
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public byte[] CodeHash { get; set; } = [];

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public Guid? UsedBy { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public bool IsOpen(DateTimeOffset now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;
}
