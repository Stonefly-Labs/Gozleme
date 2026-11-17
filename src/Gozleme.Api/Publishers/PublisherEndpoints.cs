using System.Diagnostics;
using System.Globalization;
using Gozleme.Api.Identity;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;
using Gozleme.Api.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Gozleme.Api.Publishers;

/// <summary>
/// <c>/api/v1/organizations/{organizationId}/publishers/*</c> (contracts/publishers-api.openapi.yaml). Mapped on the
/// organizations group, so every call runs in one <see cref="TenantTransaction"/> with <c>no-store</c>. Each endpoint
/// declares one action, and the order is membership (empty 404) → permission (403) → validation (400) → target
/// (404) → suspension (409) → write. A malformed, unknown or other-organization publisher or capability id, and a
/// capability under the wrong publisher, are the same empty 404. Handles, names and display names never appear
/// in a URL, span or metric label. Row-level security and the guards enforce the same rules again.
/// </summary>
internal static class PublisherEndpoints
{
    private const long MaxBodyBytes = 4 * 1024;

    public static RouteGroupBuilder MapPublisherEndpoints(this RouteGroupBuilder orgs)
    {
        var body = new RequestSizeLimitAttribute(MaxBodyBytes);
        const string publisher = "/{organizationId}/publishers/{publisherId}";
        const string capability = publisher + "/capabilities/{capabilityId}";

        orgs.MapGet("/{organizationId}/publishers", ListAsync).WithMetadata(new OrgActionAttribute(OrgAction.PublisherList));
        orgs.MapPost("/{organizationId}/publishers", RegisterAsync).WithMetadata(body, new OrgActionAttribute(OrgAction.PublisherRegister));
        orgs.MapGet(publisher, GetAsync).WithMetadata(new OrgActionAttribute(OrgAction.PublisherRead));
        orgs.MapPatch(publisher, RenameAsync).WithMetadata(body, new OrgActionAttribute(OrgAction.PublisherRename));
        orgs.MapGet(publisher + "/capabilities", CapabilitiesAsync).WithMetadata(new OrgActionAttribute(OrgAction.CapabilityList));
        orgs.MapPost(publisher + "/capabilities", CreateCapabilityAsync).WithMetadata(body, new OrgActionAttribute(OrgAction.CapabilityCreate));
        orgs.MapGet(capability, GetCapabilityAsync).WithMetadata(new OrgActionAttribute(OrgAction.CapabilityRead));
        orgs.MapPatch(capability, RenameCapabilityAsync).WithMetadata(body, new OrgActionAttribute(OrgAction.CapabilityRename));
        return orgs;
    }

    private static async Task<IResult> ListAsync(string organizationId, HttpContext http, GozlemeDbContext db, PublisherStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.PublisherList, ct);
        if (access.Membership is not { } membership)
        {
            return TypedResults.NotFound();
        }

        if (!access.Allowed)
        {
            return Forbidden(null, null);
        }

        var active = await store.ActiveAsync(membership.OrganizationId, ct);
        var publishers = await store.ListAsync(membership.OrganizationId, ct);
        return TypedResults.Ok(new PublisherListResponse(origin, [.. publishers.Select(p => Response(origin, p, active.Contains(p.Id)))]));
    }

    private static async Task<IResult> RegisterAsync(
        string organizationId, RegisterPublisherRequest request, HttpContext http, GozlemeDbContext db, PublisherStore store, RegistryOriginReader origins,
        PublisherRegistrationLimiter limiter, SecurityAudit audit, CancellationToken ct)
    {
        const string operation = PublisherOperation.Register;
        using var activity = PublisherTelemetry.Start(operation);
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.PublisherRegister, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, operation);
        }

        PublisherTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, operation);
        }

        // After authorization (maintainer decision U1, research R8): a non-member never sees 429, refused attempts
        // spend nothing, and the audit names only a confirmed organization.
        if (await limiter.TryAcquireAsync(membership.UserId, ct) is { } retryAfter)
        {
            PublisherTelemetry.Record(activity, operation, PublisherOutcome.RateLimited);
            audit.PublisherRegistrationRefused(PublisherOutcome.RateLimited, membership.OrganizationId, membership.UserId);
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return TypedResults.Json(new PublisherError(PublisherError.RateLimited), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!NamespaceRules.IsValidHandle(request.Handle))
        {
            fields["handle"] = [NamespaceRules.RuleText.Handle];
        }

        var (displayName, error) = DisplayNames.Validate(request.DisplayName);
        if (displayName is null)
        {
            fields["displayName"] = [error!];
        }

        if (fields.Count > 0)
        {
            return ValidationFailed(activity, operation, fields);
        }

        var (result, created, claimId) = await store.RegisterAsync(membership.OrganizationId, membership.UserId, request.Handle!, displayName!, ct);
        switch (result)
        {
            case WriteResult.HandleUnavailable:
                PublisherTelemetry.Record(activity, operation, PublisherOutcome.HandleUnavailable);
                audit.PublisherRegistrationRefused(PublisherOutcome.HandleUnavailable, membership.OrganizationId, membership.UserId);
                return TypedResults.Json(new PublisherError(PublisherError.HandleUnavailable), statusCode: StatusCodes.Status409Conflict);
            case not WriteResult.Ok:
                return NotFound(activity, operation);
        }

        PublisherTelemetry.Publisher(activity, created!.Id);
        PublisherTelemetry.Record(activity, operation, PublisherOutcome.Success);
        TenantTransaction.AfterCommit(http, () => audit.PublisherRegistered(created.Id, created.OrganizationId, membership.UserId, claimId));
        return TypedResults.Created($"/api/v1/organizations/{created.OrganizationId}/publishers/{created.Id}", Response(origin, created, active: true));
    }

    private static async Task<IResult> GetAsync(string organizationId, string publisherId, HttpContext http, GozlemeDbContext db, PublisherStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.PublisherRead, ct);
        if (access.Membership is not { } membership)
        {
            return TypedResults.NotFound();
        }

        if (!access.Allowed)
        {
            return Forbidden(null, null);
        }

        if (await store.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(Response(origin, publisher, await store.IsActiveAsync(publisher, ct)));
    }

    private static async Task<IResult> RenameAsync(
        string organizationId, string publisherId, DisplayNameRequest request, HttpContext http, GozlemeDbContext db, PublisherStore store,
        RegistryOriginReader origins, SecurityAudit audit, CancellationToken ct)
    {
        const string operation = PublisherOperation.Rename;
        using var activity = PublisherTelemetry.Start(operation);
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.PublisherRename, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, operation);
        }

        PublisherTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, operation);
        }

        var (displayName, error) = DisplayNames.Validate(request.DisplayName);
        if (displayName is null)
        {
            return ValidationFailed(activity, operation, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["displayName"] = [error!] });
        }

        if (await store.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher)
        {
            return NotFound(activity, operation);
        }

        PublisherTelemetry.Publisher(activity, publisher.Id);
        if (!await store.IsActiveAsync(publisher, ct))
        {
            return Suspended(activity, operation);
        }

        if (!await store.RenameAsync(publisher, displayName, ct))
        {
            PublisherTelemetry.Record(activity, operation, PublisherOutcome.Unchanged);
            return TypedResults.Ok(Response(origin, publisher, active: true));
        }

        PublisherTelemetry.Record(activity, operation, PublisherOutcome.Success);
        TenantTransaction.AfterCommit(http, () => audit.PublisherRenamed(publisher.Id, publisher.OrganizationId, membership.UserId));
        return TypedResults.Ok(Response(origin, publisher, active: true));
    }

    private static async Task<IResult> CapabilitiesAsync(
        string organizationId, string publisherId, HttpContext http, GozlemeDbContext db, PublisherStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.CapabilityList, ct);
        if (access.Membership is not { } membership)
        {
            return TypedResults.NotFound();
        }

        if (!access.Allowed)
        {
            return Forbidden(null, null);
        }

        if (await store.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher)
        {
            return TypedResults.NotFound();
        }

        var active = await store.IsActiveAsync(publisher, ct);
        var capabilities = await store.CapabilitiesAsync(publisher, ct);
        return TypedResults.Ok(new CapabilityListResponse(origin, [.. capabilities.Select(c => Response(origin, publisher, c, active))]));
    }

    private static async Task<IResult> CreateCapabilityAsync(
        string organizationId, string publisherId, CreateCapabilityRequest request, HttpContext http, GozlemeDbContext db, PublisherStore store,
        RegistryOriginReader origins, SecurityAudit audit, CancellationToken ct)
    {
        const string operation = PublisherOperation.CapabilityCreate;
        using var activity = PublisherTelemetry.Start(operation);
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.CapabilityCreate, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, operation);
        }

        PublisherTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, operation);
        }

        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!NamespaceRules.IsValidCapabilityName(request.Name))
        {
            fields["name"] = [NamespaceRules.RuleText.CapabilityName];
        }

        var (displayName, error) = DisplayNames.Validate(request.DisplayName);
        if (displayName is null)
        {
            fields["displayName"] = [error!];
        }

        if (fields.Count > 0)
        {
            return ValidationFailed(activity, operation, fields);
        }

        if (await store.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher)
        {
            return NotFound(activity, operation);
        }

        PublisherTelemetry.Publisher(activity, publisher.Id);
        if (!await store.IsActiveAsync(publisher, ct))
        {
            return Suspended(activity, operation);
        }

        var (result, created) = await store.CreateCapabilityAsync(publisher, membership.UserId, request.Name!, displayName!, ct);
        switch (result)
        {
            case WriteResult.Conflict:
                PublisherTelemetry.Record(activity, operation, PublisherOutcome.Conflict);
                return TypedResults.Json(new PublisherError(PublisherError.Conflict), statusCode: StatusCodes.Status409Conflict);
            case not WriteResult.Ok:
                return NotFound(activity, operation);
        }

        PublisherTelemetry.Capability(activity, created!.Id);
        PublisherTelemetry.Record(activity, operation, PublisherOutcome.Success);
        TenantTransaction.AfterCommit(http, () => audit.CapabilityCreated(created.Id, publisher.Id, publisher.OrganizationId, membership.UserId));
        return TypedResults.Created(
            $"/api/v1/organizations/{publisher.OrganizationId}/publishers/{publisher.Id}/capabilities/{created.Id}", Response(origin, publisher, created, active: true));
    }

    private static async Task<IResult> GetCapabilityAsync(
        string organizationId, string publisherId, string capabilityId, HttpContext http, GozlemeDbContext db, PublisherStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.CapabilityRead, ct);
        if (access.Membership is not { } membership)
        {
            return TypedResults.NotFound();
        }

        if (!access.Allowed)
        {
            return Forbidden(null, null);
        }

        if (await store.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher
            || await store.FindCapabilityAsync(publisher, capabilityId, ct) is not { } capability)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(Response(origin, publisher, capability, await store.IsActiveAsync(publisher, ct)));
    }

    private static async Task<IResult> RenameCapabilityAsync(
        string organizationId, string publisherId, string capabilityId, DisplayNameRequest request, HttpContext http, GozlemeDbContext db,
        PublisherStore store, RegistryOriginReader origins, SecurityAudit audit, CancellationToken ct)
    {
        const string operation = PublisherOperation.CapabilityRename;
        using var activity = PublisherTelemetry.Start(operation);
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return Unavailable();
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.CapabilityRename, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity, operation);
        }

        PublisherTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return Forbidden(activity, operation);
        }

        var (displayName, error) = DisplayNames.Validate(request.DisplayName);
        if (displayName is null)
        {
            return ValidationFailed(activity, operation, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["displayName"] = [error!] });
        }

        if (await store.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher
            || await store.FindCapabilityAsync(publisher, capabilityId, ct) is not { } capability)
        {
            return NotFound(activity, operation);
        }

        PublisherTelemetry.Publisher(activity, publisher.Id);
        PublisherTelemetry.Capability(activity, capability.Id);
        if (!await store.IsActiveAsync(publisher, ct))
        {
            return Suspended(activity, operation);
        }

        if (!await store.RenameAsync(capability, displayName, ct))
        {
            PublisherTelemetry.Record(activity, operation, PublisherOutcome.Unchanged);
            return TypedResults.Ok(Response(origin, publisher, capability, active: true));
        }

        PublisherTelemetry.Record(activity, operation, PublisherOutcome.Success);
        TenantTransaction.AfterCommit(http, () => audit.CapabilityRenamed(capability.Id, publisher.Id, publisher.OrganizationId, membership.UserId));
        return TypedResults.Ok(Response(origin, publisher, capability, active: true));
    }

    private static PublisherResponse Response(string origin, Publisher publisher, bool active) => new(
        publisher.Id, CanonicalId.Publisher(origin, publisher.Id), origin, publisher.OrganizationId, publisher.Handle, publisher.DisplayName,
        PublisherState.Of(active), publisher.CreatedAt, publisher.UpdatedAt);

    private static CapabilityResponse Response(string origin, Publisher publisher, Capability capability, bool active) => new(
        capability.Id, CanonicalId.Capability(origin, capability.Id), origin, publisher.Id, $"{publisher.Handle}/{capability.Name}", capability.Name,
        capability.DisplayName, PublisherState.Of(active), capability.CreatedAt, capability.UpdatedAt);

    /// <summary>The registry origin is not verified yet: the same body as a database outage (research R6).</summary>
    private static JsonHttpResult<PublisherError> Unavailable() =>
        TypedResults.Json(new PublisherError(PublisherError.Unavailable), statusCode: StatusCodes.Status503ServiceUnavailable);

    private static NotFound NotFound(Activity? activity, string operation)
    {
        PublisherTelemetry.Record(activity, operation, PublisherOutcome.NotFound);
        return TypedResults.NotFound();
    }

    private static JsonHttpResult<PublisherError> Forbidden(Activity? activity, string? operation)
    {
        if (operation is not null)
        {
            PublisherTelemetry.Record(activity, operation, PublisherOutcome.Forbidden);
        }

        return TypedResults.Json(new PublisherError(PublisherError.Forbidden), statusCode: StatusCodes.Status403Forbidden);
    }

    private static JsonHttpResult<PublisherError> ValidationFailed(Activity? activity, string operation, Dictionary<string, string[]> fields)
    {
        PublisherTelemetry.Record(activity, operation, PublisherOutcome.ValidationFailed);
        return TypedResults.Json(new PublisherError(PublisherError.ValidationFailed, fields), statusCode: StatusCodes.Status400BadRequest);
    }

    private static JsonHttpResult<PublisherError> Suspended(Activity? activity, string operation)
    {
        PublisherTelemetry.Record(activity, operation, PublisherOutcome.Suspended);
        return TypedResults.Json(new PublisherError(PublisherError.Suspended), statusCode: StatusCodes.Status409Conflict);
    }
}
