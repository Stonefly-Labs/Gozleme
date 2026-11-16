namespace Gozleme.Api.Organizations;

/// <summary>
/// One account in one organization. <see cref="Roles"/> is its set of built-in roles (GOZ-0004 FR-002),
/// always holding <c>member</c> and read from the database on every request; there is still no owner
/// field. <see cref="SelectedAt"/> is the account's context preference, never authority (GOZ-0002 research R4).
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

    public string[] Roles { get; set; } = [OrganizationRole.Member];
}
