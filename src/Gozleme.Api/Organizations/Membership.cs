namespace Gozleme.Api.Organizations;

/// <summary>
/// One account in one organization. Role-neutral by design (FR-018): there is no role, owner or
/// admin field. <see cref="SelectedAt"/> is the account's context preference, never authority (research R4).
/// </summary>
internal sealed class Membership
{
    public const string ViaCreator = "creator";
    public const string ViaInvitation = "invitation";

    public Guid OrganizationId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset JoinedAt { get; set; }

    public string JoinedVia { get; set; } = ViaCreator;

    public Guid? InvitationId { get; set; }

    public DateTimeOffset? SelectedAt { get; set; }
}
