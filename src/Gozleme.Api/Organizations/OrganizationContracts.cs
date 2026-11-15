using System.Text.Json.Serialization;

namespace Gozleme.Api.Organizations;

/// <summary>Organizations API v1 request/response shapes (contracts/organizations-api.openapi.yaml).</summary>
internal sealed record DisplayNameRequest(string? DisplayName);

internal sealed record OrganizationResponse(Guid Id, string DisplayName, DateTimeOffset CreatedAt);

internal sealed record OrganizationListResponse(IReadOnlyList<OrganizationResponse> Organizations, Guid? ActiveOrganizationId);

internal sealed record MemberResponse(Guid UserId, string Email, DateTimeOffset JoinedAt, string JoinedVia, bool IsSelf);

internal sealed record MembersResponse(IReadOnlyList<MemberResponse> Members);

internal sealed record InvitationResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? CreatedBy);

internal sealed record InvitationsResponse(IReadOnlyList<InvitationResponse> Invitations);

internal sealed record CreatedInvitationResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? CreatedBy, string Code);

internal sealed record RedeemRequest(string? Code);

internal sealed record RedeemedResponse(Guid OrganizationId, bool AlreadyMember);

internal sealed record OrganizationError(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public const string ValidationFailed = "validation_failed";
    public const string InvalidInvitation = "invalid_invitation";
    public const string LastMember = "last_member";
}
