namespace Gozleme.Api.Identity;

/// <summary>
/// Security audit events (GOZ-0001 FR-029; organization events GOZ-0002 FR-023; role events GOZ-0004 FR-019; publisher events GOZ-0005 FR-026; revision and signing-key events GOZ-0007 FR-022): category <c>Gozleme.Security.Audit</c>, opaque account id only —
/// never an email, password, cookie or session key. Exported through the normal log pipeline.
/// </summary>
internal sealed partial class SecurityAudit(ILoggerFactory loggers)
{
    public const string Category = "Gozleme.Security.Audit";

    private readonly ILogger _logger = loggers.CreateLogger(Category);

    [LoggerMessage(EventId = 1001, EventName = "gozleme.audit.registered", Level = LogLevel.Information, Message = "Account registered {UserId}")]
    public partial void Registered(Guid userId);

    [LoggerMessage(EventId = 1002, EventName = "gozleme.audit.registration_refused", Level = LogLevel.Information, Message = "Registration refused ({Category})")]
    public partial void RegistrationRefused(string category);

    [LoggerMessage(EventId = 1003, EventName = "gozleme.audit.signed_in", Level = LogLevel.Information, Message = "Signed in {UserId}")]
    public partial void SignedIn(Guid userId);

    [LoggerMessage(EventId = 1004, EventName = "gozleme.audit.sign_in_failed", Level = LogLevel.Warning, Message = "Sign-in failed ({Category}) {UserId}")]
    public partial void SignInFailed(string category, Guid? userId);

    [LoggerMessage(EventId = 1005, EventName = "gozleme.audit.locked_out", Level = LogLevel.Warning, Message = "Account locked out; sessions revoked {UserId}")]
    public partial void LockedOut(Guid userId);

    [LoggerMessage(EventId = 1006, EventName = "gozleme.audit.signed_out", Level = LogLevel.Information, Message = "Signed out {UserId}")]
    public partial void SignedOut(Guid? userId);

    [LoggerMessage(EventId = 1007, EventName = "gozleme.audit.signed_out_all", Level = LogLevel.Information, Message = "Signed out everywhere {UserId}")]
    public partial void SignedOutAll(Guid userId);

    [LoggerMessage(EventId = 1008, EventName = "gozleme.audit.operator_reset", Level = LogLevel.Warning, Message = "Operator password reset ({Outcome}) {UserId}")]
    public partial void OperatorReset(string outcome, Guid? userId);

    [LoggerMessage(EventId = 1009, EventName = "gozleme.audit.operator_unlock", Level = LogLevel.Warning, Message = "Operator unlock ({Outcome}) {UserId}")]
    public partial void OperatorUnlock(string outcome, Guid? userId);

    [LoggerMessage(EventId = 1010, EventName = "gozleme.audit.seeded", Level = LogLevel.Information, Message = "Demo account seeded {UserId}")]
    public partial void Seeded(Guid userId);

    [LoggerMessage(EventId = 1101, EventName = "gozleme.audit.org_created", Level = LogLevel.Information, Message = "Organization created {OrganizationId} by {UserId}")]
    public partial void OrganizationCreated(Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1102, EventName = "gozleme.audit.org_renamed", Level = LogLevel.Information, Message = "Organization renamed {OrganizationId} by {UserId}")]
    public partial void OrganizationRenamed(Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1103, EventName = "gozleme.audit.org_invitation_created", Level = LogLevel.Information, Message = "Invitation created {InvitationId} for {OrganizationId} by {UserId}")]
    public partial void InvitationCreated(Guid invitationId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1104, EventName = "gozleme.audit.org_invitation_revoked", Level = LogLevel.Information, Message = "Invitation revoked {InvitationId} for {OrganizationId} by {UserId}")]
    public partial void InvitationRevoked(Guid invitationId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1105, EventName = "gozleme.audit.org_invitation_redeemed", Level = LogLevel.Information, Message = "Invitation redeemed {InvitationId}; {UserId} joined {OrganizationId}")]
    public partial void InvitationRedeemed(Guid invitationId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1106, EventName = "gozleme.audit.org_invitation_refused", Level = LogLevel.Warning, Message = "Invitation redemption refused ({Category}) {UserId}")]
    public partial void InvitationRefused(string category, Guid userId);

    [LoggerMessage(EventId = 1107, EventName = "gozleme.audit.org_member_removed", Level = LogLevel.Information, Message = "Member {TargetUserId} removed from {OrganizationId} by {UserId}")]
    public partial void MemberRemoved(Guid organizationId, Guid targetUserId, Guid userId);

    [LoggerMessage(EventId = 1108, EventName = "gozleme.audit.org_member_left", Level = LogLevel.Information, Message = "Member {UserId} left {OrganizationId}")]
    public partial void MemberLeft(Guid organizationId, Guid userId);

    // Tenancy (GOZ-0003): 1201–1210. Reasons are bounded; never the token, key or code hash.
    [LoggerMessage(EventId = 1201, EventName = "gozleme.audit.tenant_context_rejected", Level = LogLevel.Warning, Message = "Tenant context rejected ({Reason}) {UserId}")]
    public partial void TenantContextRejected(string reason, Guid? userId);

    [LoggerMessage(EventId = 1202, EventName = "gozleme.audit.tenant_write_refused", Level = LogLevel.Warning, Message = "Row-level security refused a write {UserId}")]
    public partial void TenantWriteRefused(Guid? userId);

    [LoggerMessage(EventId = 1203, EventName = "gozleme.audit.tenant_context_self_check_failed", Level = LogLevel.Error, Message = "Tenant context self-check failed ({Reason}); the context key may differ from the provisioned key")]
    public partial void TenantContextSelfCheckFailed(string reason);

    // Organization roles (GOZ-0004): 1301–1310. Actions and role names are fixed vocabularies; ids are opaque.
    [LoggerMessage(EventId = 1301, EventName = "gozleme.audit.authz_denied", Level = LogLevel.Warning, Message = "Authorization denied ({Action}) in {OrganizationId} {UserId}")]
    public partial void AuthorizationDenied(string action, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1302, EventName = "gozleme.audit.org_member_roles_changed", Level = LogLevel.Information, Message = "Member {TargetUserId} roles changed from {Before} to {After} in {OrganizationId} by {UserId}")]
    public partial void MemberRolesChanged(Guid organizationId, Guid targetUserId, string before, string after, Guid userId);

    // Publishers (GOZ-0005): 1401–1408. Opaque ids and fixed vocabularies only: never a handle, capability
    // name, alias or display name, which are the caller's input.
    [LoggerMessage(EventId = 1401, EventName = "gozleme.audit.publisher_registered", Level = LogLevel.Information, Message = "Publisher registered {PublisherId} in {OrganizationId} by {UserId} with claim {ClaimId}")]
    public partial void PublisherRegistered(Guid publisherId, Guid organizationId, Guid userId, Guid claimId);

    [LoggerMessage(EventId = 1402, EventName = "gozleme.audit.publisher_renamed", Level = LogLevel.Information, Message = "Publisher renamed {PublisherId} in {OrganizationId} by {UserId}")]
    public partial void PublisherRenamed(Guid publisherId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1403, EventName = "gozleme.audit.capability_created", Level = LogLevel.Information, Message = "Capability created {CapabilityId} under {PublisherId} in {OrganizationId} by {UserId}")]
    public partial void CapabilityCreated(Guid capabilityId, Guid publisherId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1404, EventName = "gozleme.audit.capability_renamed", Level = LogLevel.Information, Message = "Capability renamed {CapabilityId} under {PublisherId} in {OrganizationId} by {UserId}")]
    public partial void CapabilityRenamed(Guid capabilityId, Guid publisherId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1405, EventName = "gozleme.audit.publisher_registration_refused", Level = LogLevel.Warning, Message = "Publisher registration refused ({Category}) in {OrganizationId} {UserId}")]
    public partial void PublisherRegistrationRefused(string category, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1406, EventName = "gozleme.audit.handle_reserved", Level = LogLevel.Warning, Message = "Operator reserved handle claim {ClaimId} ({Kind}) for {EarmarkedOrganizationId}")]
    public partial void HandleReserved(Guid claimId, string kind, Guid? earmarkedOrganizationId);

    [LoggerMessage(EventId = 1407, EventName = "gozleme.audit.handle_reservation_released", Level = LogLevel.Warning, Message = "Operator released handle claim {ClaimId}")]
    public partial void HandleReservationReleased(Guid claimId);

    [LoggerMessage(EventId = 1408, EventName = "gozleme.audit.handle_reclaimed", Level = LogLevel.Warning, Message = "Operator reclaimed handle claim {ClaimId}; publisher {PublisherId} of {OrganizationId} suspended; earmark {EarmarkClaimId}")]
    public partial void HandleReclaimed(Guid claimId, Guid publisherId, Guid organizationId, Guid? earmarkClaimId);

    // Revisions and signing keys (GOZ-0007): 1501–1510. Opaque ids and fixed vocabularies only: never a version,
    // digest, statement, signature, key ID or key material. Written after commit.
    [LoggerMessage(EventId = 1501, EventName = "gozleme.audit.revision_published", Level = LogLevel.Information, Message = "Revision published {RevisionId} of {CapabilityId} under {PublisherId} in {OrganizationId} by {UserId}")]
    public partial void RevisionPublished(Guid revisionId, Guid capabilityId, Guid publisherId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1502, EventName = "gozleme.audit.signing_key_created", Level = LogLevel.Information, Message = "Signing key created {KeyId} for {PublisherId} in {OrganizationId} by {UserId}")]
    public partial void SigningKeyCreated(Guid keyId, Guid publisherId, Guid organizationId, Guid userId);

    [LoggerMessage(EventId = 1503, EventName = "gozleme.audit.signing_key_succeeded", Level = LogLevel.Warning, Message = "Operator retired signing key {RetiredKeyId} of {PublisherId} ({Reason}); successor {SuccessorKeyId}")]
    public partial void SigningKeySucceeded(Guid retiredKeyId, Guid successorKeyId, Guid publisherId, string reason);

    [LoggerMessage(EventId = 1504, EventName = "gozleme.audit.signing_key_succession_refused", Level = LogLevel.Warning, Message = "Operator signing key succession refused ({Category})")]
    public partial void SigningKeySuccessionRefused(string category);

    // Child identities and projection generations (GOZ-0008): 1601–1610. Opaque ids, counts and fixed words only: never
    // a name, title, native name, digest or document. Written after commit. The next free range is 1701.
    [LoggerMessage(EventId = 1601, EventName = "gozleme.audit.projection_generation_recorded", Level = LogLevel.Warning, Message = "Operator recorded projection generation {Generation} {GenerationId} of revision {RevisionId}")]
    public partial void ProjectionGenerationRecorded(Guid revisionId, Guid generationId, int generation);

    [LoggerMessage(EventId = 1602, EventName = "gozleme.audit.projection_generation_refused", Level = LogLevel.Warning, Message = "Operator projection generation refused ({Category})")]
    public partial void ProjectionGenerationRefused(string category);

    [LoggerMessage(EventId = 1603, EventName = "gozleme.audit.children_installed", Level = LogLevel.Information, Message = "Children installed for {RevisionCount} revisions of {CapabilityId}")]
    public partial void ChildrenInstalled(Guid capabilityId, int revisionCount);
}
