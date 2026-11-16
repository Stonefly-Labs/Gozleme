using System.Diagnostics;
using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Organizations;

/// <summary>
/// <c>/api/v1/organizations/*</c> and <c>/api/v1/invitations/redeem</c> (contracts/organizations-api.openapi.yaml).
/// Every organization-scoped call re-reads the caller's membership (research R3, R9); a malformed id,
/// an unknown organization and a non-member answer the same empty 404. Interim role-neutral rule
/// (FR-015): any member may perform every operation here until GOZ-0004 introduces roles.
/// </summary>
internal static class OrganizationEndpoints
{
    private const long MaxBodyBytes = 4 * 1024;

    /// <summary>Maps under the <c>/api/v1</c> group, which requires a session and the web-client header by default.</summary>
    public static RouteGroupBuilder MapOrganizationEndpoints(this RouteGroupBuilder api)
    {
        var orgs = api.MapGroup("/organizations").AddEndpointFilter(NoStore);
        var body = new RequestSizeLimitAttribute(MaxBodyBytes);

        orgs.MapGet("", ListAsync);
        orgs.MapPost("", CreateAsync).WithMetadata(body);
        orgs.MapGet("/{organizationId}", GetAsync);
        orgs.MapPatch("/{organizationId}", RenameAsync).WithMetadata(body);
        orgs.MapPost("/{organizationId}/select", SelectAsync);
        orgs.MapGet("/{organizationId}/members", MembersAsync);
        orgs.MapDelete("/{organizationId}/members/{userId}", RemoveMemberAsync);
        orgs.MapGet("/{organizationId}/invitations", InvitationsAsync);
        orgs.MapPost("/{organizationId}/invitations", CreateInvitationAsync);
        orgs.MapDelete("/{organizationId}/invitations/{invitationId}", RevokeInvitationAsync);

        api.MapGroup("/invitations").AddEndpointFilter(NoStore)
            .MapPost("/redeem", RedeemAsync)
            .RequireRateLimiting(OrganizationServiceCollectionExtensions.RedeemRateLimit)
            .WithMetadata(body, new RateLimitedOperation(activity => OrganizationTelemetry.Failed(activity, OrgOperation.InviteRedeem, OrgFailure.RateLimited)));
        return orgs;
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static async Task<IResult> ListAsync(HttpContext http, GozlemeDbContext db, CancellationToken ct)
    {
        var userId = OrganizationAccess.CurrentUserId(http.User);
        var rows = await db.Memberships.Where(m => m.UserId == userId)
            .Join(db.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { Membership = m, Organization = o })
            .ToListAsync(ct);

        // Context preference (research R4): last selected, else last joined, else none. Never authority.
        var active = rows.OrderByDescending(r => r.Membership.SelectedAt.HasValue)
            .ThenByDescending(r => r.Membership.SelectedAt)
            .ThenByDescending(r => r.Membership.JoinedAt)
            .Select(r => (Guid?)r.Organization.Id)
            .FirstOrDefault();
        var organizations = rows.Select(r => r.Organization)
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(o => o.Id)
            .Select(Response)
            .ToList();
        return TypedResults.Ok(new OrganizationListResponse(organizations, active));
    }

    private static async Task<IResult> CreateAsync(DisplayNameRequest request, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.Create);
        var (name, error) = DisplayNames.Validate(request.DisplayName);
        if (name is null)
        {
            return ValidationFailed(activity, OrgOperation.Create, error!);
        }

        var userId = OrganizationAccess.CurrentUserId(http.User)!.Value;
        var now = time.GetUtcNow();
        var organization = new Organization { Id = Guid.CreateVersion7(now), DisplayName = name, CreatedAt = now, CreatedBy = userId, UpdatedAt = now };
        db.Organizations.Add(organization);
        db.Memberships.Add(new Membership { OrganizationId = organization.Id, UserId = userId, JoinedAt = now, JoinedVia = Membership.ViaCreator, SelectedAt = now });
        await db.SaveChangesAsync(ct); // one transaction: never an organization without its first member

        OrganizationTelemetry.Organization(activity, organization.Id);
        OrganizationTelemetry.Succeeded(activity, OrgOperation.Create);
        audit.OrganizationCreated(organization.Id, userId);
        return TypedResults.Created((string?)null, Response(organization));
    }

    private static async Task<IResult> GetAsync(string organizationId, HttpContext http, GozlemeDbContext db, CancellationToken ct)
    {
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return TypedResults.NotFound();
        }

        var organization = await db.Organizations.SingleAsync(o => o.Id == membership.OrganizationId, ct);
        return TypedResults.Ok(Response(organization));
    }

    private static async Task<IResult> RenameAsync(string organizationId, DisplayNameRequest request, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.Rename);
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return NotFound(activity, OrgOperation.Rename);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        var (name, error) = DisplayNames.Validate(request.DisplayName);
        if (name is null)
        {
            return ValidationFailed(activity, OrgOperation.Rename, error!);
        }

        var organization = await db.Organizations.SingleAsync(o => o.Id == membership.OrganizationId, ct);
        organization.DisplayName = name;
        organization.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        OrganizationTelemetry.Succeeded(activity, OrgOperation.Rename);
        audit.OrganizationRenamed(organization.Id, membership.UserId);
        return TypedResults.Ok(Response(organization));
    }

    private static async Task<IResult> SelectAsync(string organizationId, HttpContext http, GozlemeDbContext db, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.Select);
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return NotFound(activity, OrgOperation.Select);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        membership.SelectedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        OrganizationTelemetry.Succeeded(activity, OrgOperation.Select);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> MembersAsync(string organizationId, HttpContext http, GozlemeDbContext db, CancellationToken ct)
    {
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return TypedResults.NotFound();
        }

        var members = await db.Memberships.Where(m => m.OrganizationId == membership.OrganizationId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.UserId, u.Email, m.JoinedAt, m.JoinedVia })
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId)
            .ToListAsync(ct);
        return TypedResults.Ok(new MembersResponse(members.Select(m => new MemberResponse(m.UserId, m.Email!, m.JoinedAt, m.JoinedVia, m.UserId == membership.UserId)).ToList()));
    }

    private static async Task<IResult> RemoveMemberAsync(string organizationId, string userId, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        var callerId = OrganizationAccess.CurrentUserId(http.User);
        var leaving = Guid.TryParse(userId, out var targetId) && targetId == callerId;
        var operation = leaving ? OrgOperation.MemberLeave : OrgOperation.MemberRemove;
        using var activity = OrganizationTelemetry.Start(operation);

        // The organization row lock serializes every membership removal in that organization, so
        // concurrent removals can never leave it without a member (FR-016, research R6).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return NotFound(activity, operation);
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM gozleme.organizations WHERE id = {membership.OrganizationId} FOR UPDATE", ct);

        // Re-read after the lock: a concurrent request may have removed the caller meanwhile.
        if (!await db.Memberships.AnyAsync(m => m.OrganizationId == membership.OrganizationId && m.UserId == membership.UserId, ct))
        {
            return NotFound(activity, operation);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        var target = Guid.TryParse(userId, out _)
            ? await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == membership.OrganizationId && m.UserId == targetId, ct)
            : null;
        if (target is null)
        {
            return NotFound(activity, operation);
        }

        if (await db.Memberships.CountAsync(m => m.OrganizationId == membership.OrganizationId, ct) <= 1)
        {
            OrganizationTelemetry.Failed(activity, operation, OrgFailure.LastMember);
            return TypedResults.Json(new OrganizationError(OrganizationError.LastMember), statusCode: StatusCodes.Status409Conflict);
        }

        // A departing account's open invitations go with it, so it cannot rejoin through a code it
        // created while a member (GOZ-0002-IF-04). Locked like redemption, which they serialize with.
        var now = time.GetUtcNow();
        var orphaned = await db.Invitations
            .FromSql($"SELECT * FROM gozleme.invitations WHERE organization_id = {membership.OrganizationId} AND created_by = {targetId} AND used_at IS NULL AND revoked_at IS NULL FOR UPDATE")
            .ToListAsync(ct);
        foreach (var invitation in orphaned.Where(i => i.IsOpen(now)))
        {
            invitation.RevokedAt = now;
            invitation.RevokedBy = membership.UserId;
        }

        db.Memberships.Remove(target);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        OrganizationTelemetry.Succeeded(activity, operation);
        foreach (var invitation in orphaned.Where(i => i.RevokedAt == now))
        {
            audit.InvitationRevoked(invitation.Id, invitation.OrganizationId, membership.UserId);
        }

        if (leaving)
        {
            audit.MemberLeft(membership.OrganizationId, membership.UserId);
        }
        else
        {
            audit.MemberRemoved(membership.OrganizationId, targetId, membership.UserId);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> InvitationsAsync(string organizationId, HttpContext http, GozlemeDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        var open = await db.Invitations
            .Where(i => i.OrganizationId == membership.OrganizationId && i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => new InvitationResponse(i.Id, i.CreatedAt, i.ExpiresAt, i.CreatedBy))
            .ToListAsync(ct);
        return TypedResults.Ok(new InvitationsResponse(open));
    }

    private static async Task<IResult> CreateInvitationAsync(
        string organizationId, HttpContext http, GozlemeDbContext db, IOptions<OrganizationOptions> options, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.InviteCreate);
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return NotFound(activity, OrgOperation.InviteCreate);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        var now = time.GetUtcNow();
        var code = InvitationCodes.Generate();
        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = membership.OrganizationId,
            CodeHash = InvitationCodes.Hash(InvitationCodes.Normalize(code)!),
            CreatedBy = membership.UserId,
            CreatedAt = now,
            ExpiresAt = now.AddHours(options.Value.InvitationExpiryHours),
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        OrganizationTelemetry.Succeeded(activity, OrgOperation.InviteCreate);
        audit.InvitationCreated(invitation.Id, invitation.OrganizationId, membership.UserId);
        return TypedResults.Created((string?)null, new CreatedInvitationResponse(invitation.Id, invitation.CreatedAt, invitation.ExpiresAt, invitation.CreatedBy, code));
    }

    private static async Task<IResult> RevokeInvitationAsync(string organizationId, string invitationId, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.InviteRevoke);
        if (await OrganizationAccess.FindMembershipAsync(db, organizationId, http.User, ct) is not { } membership)
        {
            return NotFound(activity, OrgOperation.InviteRevoke);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        var now = time.GetUtcNow();
        var invitation = Guid.TryParse(invitationId, out var id)
            ? await db.Invitations.FirstOrDefaultAsync(i => i.Id == id && i.OrganizationId == membership.OrganizationId, ct)
            : null;
        if (invitation is null || !invitation.IsOpen(now))
        {
            return NotFound(activity, OrgOperation.InviteRevoke);
        }

        invitation.RevokedAt = now;
        invitation.RevokedBy = membership.UserId;
        await db.SaveChangesAsync(ct);
        OrganizationTelemetry.Succeeded(activity, OrgOperation.InviteRevoke);
        audit.InvitationRevoked(invitation.Id, invitation.OrganizationId, membership.UserId);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RedeemAsync(RedeemRequest request, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.InviteRedeem);
        var userId = OrganizationAccess.CurrentUserId(http.User)!.Value;
        if (InvitationCodes.Normalize(request.Code) is not { } code)
        {
            return InvalidInvitation(activity, audit, userId);
        }

        var hash = InvitationCodes.Hash(code);
        var now = time.GetUtcNow();

        // The row lock makes redemption single-use under concurrency (FR-008, research R6).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invitation = await db.Invitations.FromSql($"SELECT * FROM gozleme.invitations WHERE code_hash = {hash} FOR UPDATE").FirstOrDefaultAsync(ct);
        if (invitation is null || !invitation.IsOpen(now))
        {
            return InvalidInvitation(activity, audit, userId);
        }

        OrganizationTelemetry.Organization(activity, invitation.OrganizationId);
        if (await db.Memberships.AnyAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == userId, ct))
        {
            // Already a member: nothing changes and the code stays usable (FR-009).
            OrganizationTelemetry.Succeeded(activity, OrgOperation.InviteRedeem);
            return TypedResults.Ok(new RedeemedResponse(invitation.OrganizationId, AlreadyMember: true));
        }

        invitation.UsedAt = now;
        invitation.UsedBy = userId;
        db.Memberships.Add(new Membership
        {
            OrganizationId = invitation.OrganizationId,
            UserId = userId,
            JoinedAt = now,
            JoinedVia = Membership.ViaInvitation,
            InvitationId = invitation.Id,
            SelectedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent redemption of another code made this account a member first.
            await transaction.RollbackAsync(ct);
            OrganizationTelemetry.Succeeded(activity, OrgOperation.InviteRedeem);
            return TypedResults.Ok(new RedeemedResponse(invitation.OrganizationId, AlreadyMember: true));
        }

        await transaction.CommitAsync(ct);
        OrganizationTelemetry.Succeeded(activity, OrgOperation.InviteRedeem);
        audit.InvitationRedeemed(invitation.Id, invitation.OrganizationId, userId);
        return TypedResults.Ok(new RedeemedResponse(invitation.OrganizationId, AlreadyMember: false));
    }

    private static OrganizationResponse Response(Organization organization) => new(organization.Id, organization.DisplayName, organization.CreatedAt);

    private static NotFound NotFound(Activity? activity, string operation)
    {
        OrganizationTelemetry.Failed(activity, operation, OrgFailure.NotFound);
        return TypedResults.NotFound();
    }

    private static JsonHttpResult<OrganizationError> ValidationFailed(Activity? activity, string operation, string error)
    {
        OrganizationTelemetry.Failed(activity, operation, OrgFailure.Validation);
        return TypedResults.Json(
            new OrganizationError(OrganizationError.ValidationFailed, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["displayName"] = [error] }),
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>One generic refusal for unknown, used, revoked, expired and malformed codes (FR-010).</summary>
    private static JsonHttpResult<OrganizationError> InvalidInvitation(Activity? activity, SecurityAudit audit, Guid userId)
    {
        OrganizationTelemetry.Failed(activity, OrgOperation.InviteRedeem, OrgFailure.InvalidInvitation);
        audit.InvitationRefused(OrgFailure.InvalidInvitation, userId);
        return TypedResults.Json(new OrganizationError(OrganizationError.InvalidInvitation), statusCode: StatusCodes.Status400BadRequest);
    }
}
