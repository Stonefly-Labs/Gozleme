using System.Security.Claims;
using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;
using Gozleme.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Organizations;

/// <summary>
/// The application organization authorization check (research R3, R9): the caller's membership row,
/// read from the database on every request. A malformed id, an unknown organization and a non-member
/// all yield null, which every endpoint answers with the same empty 404. Since GOZ-0003 PostgreSQL
/// row-level security enforces the same boundary as a second, independent layer; it never replaces this check.
/// GOZ-0004 adds the single authorization decision point after it (<see cref="AuthorizeAsync"/>).
/// </summary>
internal static class OrganizationAccess
{
    public static Guid? CurrentUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static async Task<Membership?> FindMembershipAsync(GozlemeDbContext db, string? organizationId, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(organizationId, out var orgId) || CurrentUserId(user) is not { } userId)
        {
            return null;
        }

        return await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == userId, ct);
    }

    /// <summary>
    /// The decision point (GOZ-0004 FR-004, research R6): membership first (null → the empty 404, no
    /// decision recorded), then whether the caller's roles, read in this request's transaction, permit
    /// <paramref name="action"/>. Inputs are only (roles, action); GOZ-0018 replaces the evaluation here.
    /// A denial is traced, counted and audited (1301) after commit.
    /// </summary>
    public static async Task<Access> AuthorizeAsync(GozlemeDbContext db, string? organizationId, HttpContext http, string action, CancellationToken ct)
    {
        if (await FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return new Access(null, false);
        }

        var allowed = OrganizationPermissions.Allows(membership.Roles, action, OrgAction.ResourceOf(action));
        AuthorizationTelemetry.Record(action, allowed);
        if (!allowed)
        {
            var audit = http.RequestServices.GetRequiredService<SecurityAudit>();
            TenantTransaction.AfterCommit(http, () => audit.AuthorizationDenied(action, membership.OrganizationId, membership.UserId));
        }

        return new Access(membership, allowed);
    }
}

/// <summary>The caller's membership (null: answer the empty 404) and whether the action is allowed (false: 403).</summary>
internal sealed record Access(Membership? Membership, bool Allowed);
