using System.Security.Claims;
using Gozleme.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Organizations;

/// <summary>
/// The application organization authorization check (research R3, R9): the caller's membership row,
/// read from the database on every request. A malformed id, an unknown organization and a non-member
/// all yield null, which every endpoint answers with the same empty 404. Since GOZ-0003 PostgreSQL
/// row-level security enforces the same boundary as a second, independent layer; it never replaces this check.
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
}
