using System.Diagnostics;
using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;
using Gozleme.Api.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gozleme.Api.Organizations;

/// <summary>
/// <c>/api/v1/organizations/*</c> and <c>/api/v1/invitations/redeem</c> (contracts/organizations-api.openapi.yaml).
/// Every organization-scoped call re-reads the caller's membership (research R3, R9); a malformed id,
/// an unknown organization and a non-member answer the same empty 404. GOZ-0004 then asks
/// <see cref="OrganizationAccess.AuthorizeAsync"/> whether the caller's built-in roles permit the
/// endpoint's declared <see cref="OrgActionAttribute"/> action; a member who lacks it gets 403
/// (contracts/permissions.md). Since GOZ-0003 every call runs in one <see cref="TenantTransaction"/>, so
/// PostgreSQL row-level security enforces the same boundary, and since GOZ-0004 the admin-only writes,
/// as a second, independent layer.
/// </summary>
internal static class OrganizationEndpoints
{
    private const long MaxBodyBytes = 4 * 1024;

    /// <summary>Maps under the <c>/api/v1</c> group, which requires a session and the web-client header by default.</summary>
    public static RouteGroupBuilder MapOrganizationEndpoints(this RouteGroupBuilder api)
    {
        var orgs = api.MapGroup("/organizations").AddEndpointFilter(NoStore).AddEndpointFilter<TenantTransaction>();
        var body = new RequestSizeLimitAttribute(MaxBodyBytes);

        orgs.MapGet("", ListAsync);
        orgs.MapPost("", CreateAsync).WithMetadata(body);
        orgs.MapGet("/{organizationId}", GetAsync).WithMetadata(new OrgActionAttribute(OrgAction.OrganizationRead));
        orgs.MapPatch("/{organizationId}", RenameAsync).WithMetadata(body, new OrgActionAttribute(OrgAction.OrganizationRename));
        orgs.MapPost("/{organizationId}/select", SelectAsync).WithMetadata(new OrgActionAttribute(OrgAction.OrganizationSelect));
        orgs.MapGet("/{organizationId}/members", MembersAsync).WithMetadata(new OrgActionAttribute(OrgAction.MembershipList));
        orgs.MapDelete("/{organizationId}/members/{userId}", RemoveMemberAsync)
            .WithMetadata(new OrgActionAttribute(OrgAction.MembershipLeave, OrgAction.MembershipRemove));
        orgs.MapPut("/{organizationId}/members/{userId}/roles", SetRolesAsync).WithMetadata(body, new OrgActionAttribute(OrgAction.MembershipRolesChange));
        orgs.MapGet("/{organizationId}/invitations", InvitationsAsync).WithMetadata(new OrgActionAttribute(OrgAction.InvitationList));
        orgs.MapPost("/{organizationId}/invitations", CreateInvitationAsync).WithMetadata(new OrgActionAttribute(OrgAction.InvitationCreate));
        orgs.MapDelete("/{organizationId}/invitations/{invitationId}", RevokeInvitationAsync).WithMetadata(new OrgActionAttribute(OrgAction.InvitationRevoke));

        api.MapGroup("/invitations").AddEndpointFilter(NoStore).AddEndpointFilter<TenantTransaction>()
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
        db.Memberships.Add(new Membership
        {
            OrganizationId = organization.Id,
            UserId = userId,
            JoinedAt = now,
            JoinedVia = Membership.ViaCreator,
            SelectedAt = now,
            Roles = [OrganizationRole.Admin, OrganizationRole.Member], // the creator manages its organization (FR-011)
        });
        await db.SaveChangesAsync(ct); // one transaction: never an organization without its first member

        OrganizationTelemetry.Organization(activity, organization.Id);
        OrganizationTelemetry.Succeeded(activity, OrgOperation.Create);
        TenantTransaction.AfterCommit(http, () => audit.OrganizationCreated(organization.Id, userId));
        return TypedResults.Created((string?)null, Response(organization));
    }

    private static async Task<IResult> GetAsync(string organizationId, HttpContext http, GozlemeDbContext db, CancellationToken ct)
    {
        if ((await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.OrganizationRead, ct)).Membership is not { } membership)
        {
            return TypedResults.NotFound();
        }

        var organization = await db.Organizations.SingleAsync(o => o.Id == membership.OrganizationId, ct);
        var permissions = OrganizationPermissions.For(membership.Roles).Order(StringComparer.Ordinal).ToList();
        return TypedResults.Ok(new OrganizationDetailResponse(organization.Id, organization.DisplayName, organization.CreatedAt, membership.Roles, permissions));
    }

    private static async Task<IResult> RenameAsync(string organizationId, DisplayNameRequest request, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.Rename);
        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.OrganizationRename, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, OrgOperation.Rename);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, OrgOperation.Rename);
        }

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
        TenantTransaction.AfterCommit(http, () => audit.OrganizationRenamed(organization.Id, membership.UserId));
        return TypedResults.Ok(Response(organization));
    }

    private static async Task<IResult> SelectAsync(string organizationId, HttpContext http, GozlemeDbContext db, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.Select);
        if ((await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.OrganizationSelect, ct)).Membership is not { } membership)
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
        if ((await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.MembershipList, ct)).Membership is not { } membership)
        {
            return TypedResults.NotFound();
        }

        var members = await db.Memberships.Where(m => m.OrganizationId == membership.OrganizationId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.UserId, u.Email, m.JoinedAt, m.JoinedVia, m.Roles })
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId)
            .ToListAsync(ct);
        return TypedResults.Ok(new MembersResponse(members.Select(m => new MemberResponse(m.UserId, m.Email!, m.JoinedAt, m.JoinedVia, m.UserId == membership.UserId, m.Roles)).ToList()));
    }

    private static async Task<IResult> RemoveMemberAsync(string organizationId, string userId, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        var callerId = OrganizationAccess.CurrentUserId(http.User);
        var leaving = Guid.TryParse(userId, out var targetId) && targetId == callerId;
        var operation = leaving ? OrgOperation.MemberLeave : OrgOperation.MemberRemove;
        var action = leaving ? OrgAction.MembershipLeave : OrgAction.MembershipRemove;
        using var activity = OrganizationTelemetry.Start(operation);

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, action, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, operation);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, operation);
        }

        // Serialize every membership change in this organization, then re-read under the lock: a
        // concurrent request may have removed or demoted the caller meanwhile (FR-010, research R5).
        if (await LockMembersAsync(db, membership, action, ct) is not { } caller)
        {
            return NotFound(activity, operation);
        }

        if (!OrganizationPermissions.Allows(caller.Roles, action, OrgAction.ResourceOf(action)))
        {
            return Forbidden(activity, operation);
        }

        var target = Guid.TryParse(userId, out _)
            ? await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == caller.OrganizationId && m.UserId == targetId, ct)
            : null;
        if (target is null)
        {
            return NotFound(activity, operation);
        }

        if (await db.Memberships.CountAsync(m => m.OrganizationId == caller.OrganizationId, ct) <= 1)
        {
            OrganizationTelemetry.Failed(activity, operation, OrgFailure.LastMember);
            return TypedResults.Json(new OrganizationError(OrganizationError.LastMember), statusCode: StatusCodes.Status409Conflict);
        }

        if (await IsLastAdminAsync(db, target, ct))
        {
            return LastAdmin(activity, operation);
        }

        // A departing account's open invitations go with it, so it cannot rejoin through a code it
        // created while a member (GOZ-0002-IF-04).
        var revoked = await RevokeOpenInvitationsAsync(db, target, caller.UserId, time.GetUtcNow(), ct);
        db.Memberships.Remove(target);
        await db.SaveChangesAsync(ct);
        OrganizationTelemetry.Succeeded(activity, operation);
        foreach (var invitation in revoked)
        {
            TenantTransaction.AfterCommit(http, () => audit.InvitationRevoked(invitation.Id, invitation.OrganizationId, caller.UserId));
        }

        if (leaving)
        {
            TenantTransaction.AfterCommit(http, () => audit.MemberLeft(caller.OrganizationId, caller.UserId));
        }
        else
        {
            TenantTransaction.AfterCommit(http, () => audit.MemberRemoved(caller.OrganizationId, targetId, caller.UserId));
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> SetRolesAsync(
        string organizationId, string userId, RolesRequest request, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.MemberRoles);
        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.MembershipRolesChange, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, OrgOperation.MemberRoles);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, OrgOperation.MemberRoles);
        }

        if (OrganizationRole.Normalize(request.Roles) is not { } roles)
        {
            return ValidationFailed(activity, OrgOperation.MemberRoles, "roles", "Unknown role.");
        }

        if (await LockMembersAsync(db, membership, OrgAction.MembershipRolesChange, ct) is not { } caller)
        {
            return NotFound(activity, OrgOperation.MemberRoles);
        }

        if (!OrganizationPermissions.Allows(caller.Roles, OrgAction.MembershipRolesChange, OrgResource.Membership))
        {
            return Forbidden(activity, OrgOperation.MemberRoles);
        }

        var target = Guid.TryParse(userId, out var targetId)
            ? await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == caller.OrganizationId && m.UserId == targetId, ct)
            : null;
        if (target is null)
        {
            return NotFound(activity, OrgOperation.MemberRoles);
        }

        var before = target.Roles;
        if (!before.SequenceEqual(roles, StringComparer.Ordinal))
        {
            var losesAdmin = before.Contains(OrganizationRole.Admin) && !roles.Contains(OrganizationRole.Admin);
            if (losesAdmin && await IsLastAdminAsync(db, target, ct))
            {
                return LastAdmin(activity, OrgOperation.MemberRoles);
            }

            // Only admins create invitations, so losing admin revokes the account's open ones (Q2).
            var revoked = losesAdmin ? await RevokeOpenInvitationsAsync(db, target, caller.UserId, time.GetUtcNow(), ct) : [];
            target.Roles = roles;
            await db.SaveChangesAsync(ct);
            foreach (var invitation in revoked)
            {
                TenantTransaction.AfterCommit(http, () => audit.InvitationRevoked(invitation.Id, invitation.OrganizationId, caller.UserId));
            }

            var (from, to) = (string.Join(',', before), string.Join(',', roles));
            TenantTransaction.AfterCommit(http, () => audit.MemberRolesChanged(caller.OrganizationId, targetId, from, to, caller.UserId));
        }

        OrganizationTelemetry.Succeeded(activity, OrgOperation.MemberRoles);
        var email = await db.Users.Where(u => u.Id == targetId).Select(u => u.Email).SingleAsync(ct);
        return TypedResults.Ok(new MemberResponse(target.UserId, email!, target.JoinedAt, target.JoinedVia, target.UserId == caller.UserId, target.Roles));
    }

    /// <summary>
    /// Takes the per-organization membership lock, then (for an action an admin holds) the organization
    /// row lock, so the lock order stays membership lock → organization → invitation (research R5;
    /// redemption takes organization → invitation). Returns the caller's membership re-read under the
    /// lock, or null when it is gone.
    /// </summary>
    private static async Task<Membership?> LockMembersAsync(GozlemeDbContext db, Membership membership, string action, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT gozleme.lock_organization_members({membership.OrganizationId})", ct);
        db.ChangeTracker.Clear();
        var caller = await db.Memberships.FirstOrDefaultAsync(m => m.OrganizationId == membership.OrganizationId && m.UserId == membership.UserId, ct);
        if (caller is not null && caller.Roles.Contains(OrganizationRole.Admin))
        {
            // Row locks need the update policy, which admits admins only; a non-admin leaving holds no invitations.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM gozleme.organizations WHERE id = {membership.OrganizationId} FOR UPDATE", ct);
        }

        return caller;
    }

    /// <summary>Whether <paramref name="target"/> is the organization's only admin (FR-010); call under the membership lock.</summary>
    private static async Task<bool> IsLastAdminAsync(GozlemeDbContext db, Membership target, CancellationToken ct) =>
        target.Roles.Contains(OrganizationRole.Admin)
        && !await db.Memberships.AnyAsync(m => m.OrganizationId == target.OrganizationId && m.UserId != target.UserId && m.Roles.Contains(OrganizationRole.Admin), ct);

    /// <summary>Revokes the open invitations <paramref name="target"/> created, locked like redemption (GOZ-0002-IF-04, IF-20).</summary>
    private static async Task<List<Invitation>> RevokeOpenInvitationsAsync(GozlemeDbContext db, Membership target, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.Invitations
            .FromSql($"SELECT * FROM gozleme.invitations WHERE organization_id = {target.OrganizationId} AND created_by = {target.UserId} AND used_at IS NULL AND revoked_at IS NULL FOR UPDATE")
            .ToListAsync(ct);
        var revoked = open.Where(i => i.IsOpen(now)).ToList();
        foreach (var invitation in revoked)
        {
            invitation.RevokedAt = now;
            invitation.RevokedBy = actorId;
        }

        return revoked;
    }

    private static async Task<IResult> InvitationsAsync(string organizationId, HttpContext http, GozlemeDbContext db, TimeProvider time, CancellationToken ct)
    {
        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.InvitationList, ct);
        if (access.Membership is not { } membership)
        {
            return TypedResults.NotFound();
        }

        if (!access.Allowed)
        {
            return Forbidden(null, null);
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
        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.InvitationCreate, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, OrgOperation.InviteCreate);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, OrgOperation.InviteCreate);
        }

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
        TenantTransaction.AfterCommit(http, () => audit.InvitationCreated(invitation.Id, invitation.OrganizationId, membership.UserId));
        return TypedResults.Created((string?)null, new CreatedInvitationResponse(invitation.Id, invitation.CreatedAt, invitation.ExpiresAt, invitation.CreatedBy, code));
    }

    private static async Task<IResult> RevokeInvitationAsync(string organizationId, string invitationId, HttpContext http, GozlemeDbContext db, SecurityAudit audit, TimeProvider time, CancellationToken ct)
    {
        using var activity = OrganizationTelemetry.Start(OrgOperation.InviteRevoke);
        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.InvitationRevoke, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, OrgOperation.InviteRevoke);
        }

        OrganizationTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, OrgOperation.InviteRevoke);
        }

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
        TenantTransaction.AfterCommit(http, () => audit.InvitationRevoked(invitation.Id, invitation.OrganizationId, membership.UserId));
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

        // The invitation row lock makes redemption single-use under concurrency (FR-008, research R6).
        // Lock order is organization, then invitation, as in member removal, which revokes the
        // departing account's invitations under the organization lock (GOZ-0002-IF-20). The redeemer
        // is not yet a member: presenting the code hash is its capability for this one invitation, and
        // the organization lock is taken by a database helper (GOZ-0003 research R6).
        await TenantContext.PresentInvitationAsync(db, hash, ct);
        if (await TenantContext.LockInvitedOrganizationAsync(db, hash, ct) is null)
        {
            return InvalidInvitation(activity, audit, userId);
        }

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
            Roles = [OrganizationRole.Member], // an invitee joins with least privilege (FR-011)
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_memberships" })
        {
            // A concurrent redemption of another code made this account a member first.
            await db.Database.RollbackTransactionAsync(ct);
            OrganizationTelemetry.Succeeded(activity, OrgOperation.InviteRedeem);
            return TypedResults.Ok(new RedeemedResponse(invitation.OrganizationId, AlreadyMember: true));
        }

        OrganizationTelemetry.Succeeded(activity, OrgOperation.InviteRedeem);
        TenantTransaction.AfterCommit(http, () => audit.InvitationRedeemed(invitation.Id, invitation.OrganizationId, userId));
        return TypedResults.Ok(new RedeemedResponse(invitation.OrganizationId, AlreadyMember: false));
    }

    private static OrganizationResponse Response(Organization organization) => new(organization.Id, organization.DisplayName, organization.CreatedAt);

    private static NotFound NotFound(Activity? activity, string operation)
    {
        OrganizationTelemetry.Failed(activity, operation, OrgFailure.NotFound);
        return TypedResults.NotFound();
    }

    private static JsonHttpResult<OrganizationError> ValidationFailed(Activity? activity, string operation, string error) =>
        ValidationFailed(activity, operation, "displayName", error);

    private static JsonHttpResult<OrganizationError> ValidationFailed(Activity? activity, string operation, string field, string error)
    {
        OrganizationTelemetry.Failed(activity, operation, OrgFailure.Validation);
        return TypedResults.Json(
            new OrganizationError(OrganizationError.ValidationFailed, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [error] }),
            statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>A member whose roles do not permit the action (GOZ-0004 FR-004): 403, nothing changed.</summary>
    private static JsonHttpResult<OrganizationError> Forbidden(Activity? activity, string? operation)
    {
        if (operation is not null)
        {
            OrganizationTelemetry.Failed(activity, operation, OrgFailure.Forbidden);
        }

        return TypedResults.Json(new OrganizationError(OrganizationError.Forbidden), statusCode: StatusCodes.Status403Forbidden);
    }

    private static JsonHttpResult<OrganizationError> LastAdmin(Activity? activity, string operation)
    {
        OrganizationTelemetry.Failed(activity, operation, OrgFailure.LastAdmin);
        return TypedResults.Json(new OrganizationError(OrganizationError.LastAdmin), statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>One generic refusal for unknown, used, revoked, expired and malformed codes (FR-010).</summary>
    private static JsonHttpResult<OrganizationError> InvalidInvitation(Activity? activity, SecurityAudit audit, Guid userId)
    {
        OrganizationTelemetry.Failed(activity, OrgOperation.InviteRedeem, OrgFailure.InvalidInvitation);
        audit.InvitationRefused(OrgFailure.InvalidInvitation, userId);
        return TypedResults.Json(new OrganizationError(OrganizationError.InvalidInvitation), statusCode: StatusCodes.Status400BadRequest);
    }
}
