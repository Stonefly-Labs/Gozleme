using System.Text.Json.Serialization;

namespace Gozleme.Api.Organizations;

/// <summary>Organizations API v1 request/response shapes (contracts/organizations-api.openapi.yaml; GOZ-0004 delta contracts/rbac-api.openapi.yaml).</summary>
internal sealed record DisplayNameRequest(string? DisplayName);

internal sealed record OrganizationResponse(Guid Id, string DisplayName, DateTimeOffset CreatedAt);

internal sealed record OrganizationListResponse(IReadOnlyList<OrganizationResponse> Organizations, Guid? ActiveOrganizationId);

/// <summary><c>GET /organizations/{id}</c>: the caller's sorted roles and permitted actions, a UI hint only (GOZ-0004 R9).</summary>
internal sealed record OrganizationDetailResponse(Guid Id, string DisplayName, DateTimeOffset CreatedAt, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

internal sealed record MemberResponse(Guid UserId, string Email, DateTimeOffset JoinedAt, string JoinedVia, bool IsSelf, IReadOnlyList<string> Roles);

internal sealed record RolesRequest(string?[]? Roles);

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
    public const string LastAdmin = "last_admin";
    public const string Forbidden = "forbidden";
}
