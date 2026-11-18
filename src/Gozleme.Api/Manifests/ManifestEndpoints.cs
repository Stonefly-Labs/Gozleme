using System.Diagnostics;
using System.Globalization;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Gozleme.Api.Manifests;

/// <summary>
/// <c>POST /api/v1/organizations/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}/manifests/validate</c>
/// (contracts/manifest-validation.md, research R11). Mapped on the organizations group, so it runs in one
/// <see cref="TenantTransaction"/> with <c>no-store</c>. The order is exactly: origin verified (503) → membership
/// (empty 404) → permission (403) → target (the same empty 404 for malformed, unknown, foreign or misplaced IDs) →
/// suspension (409) → the account's validation budget (429) → media type (415) → body of at most 1 MiB (413) →
/// validation (200). The body is not read before the media-type step and never parsed before validation. Nothing
/// is written and no audit event is recorded (FR-017, FR-023).
/// </summary>
internal static class ManifestEndpoints
{
    public const int MaxBodyBytes = 1024 * 1024;

    public static RouteGroupBuilder MapManifestEndpoints(this RouteGroupBuilder orgs)
    {
        orgs.MapPost("/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}/manifests/validate", ValidateAsync)
            .WithMetadata(new OrgActionAttribute(OrgAction.ManifestValidate));
        return orgs;
    }

    private static async Task<IResult> ValidateAsync(
        string organizationId, string publisherId, string capabilityId, HttpContext http, GozlemeDbContext db, PublisherStore store,
        RegistryOriginReader origins, ManifestValidationLimiter limiter, ManifestValidator validator, CancellationToken ct)
    {
        using var activity = ManifestTelemetry.Start();
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            ManifestTelemetry.Record(activity, ManifestOutcome.Unavailable);
            return Error(ManifestError.Unavailable, StatusCodes.Status503ServiceUnavailable);
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, OrgAction.ManifestValidate, ct);
        if (access.Membership is not { } membership)
        {
            return NotFound(activity);
        }

        ManifestTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            ManifestTelemetry.Record(activity, ManifestOutcome.Forbidden);
            return Error(ManifestError.Forbidden, StatusCodes.Status403Forbidden);
        }

        if (await store.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher
            || await store.FindCapabilityAsync(publisher, capabilityId, ct) is not { } capability)
        {
            return NotFound(activity);
        }

        ManifestTelemetry.Target(activity, publisher.Id, capability.Id);
        if (!await store.IsActiveAsync(publisher, ct))
        {
            ManifestTelemetry.Record(activity, ManifestOutcome.Suspended);
            return Error(ManifestError.Suspended, StatusCodes.Status409Conflict);
        }

        if (limiter.TryAcquire(membership.UserId) is { } retryAfter)
        {
            ManifestTelemetry.Record(activity, ManifestOutcome.RateLimited);
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Error(ManifestError.RateLimited, StatusCodes.Status429TooManyRequests);
        }

        if (!IsJson(http.Request.ContentType))
        {
            ManifestTelemetry.Record(activity, ManifestOutcome.UnsupportedMediaType);
            return Error(ManifestError.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        }

        if (await ReadBodyAsync(http.Request.Body, ct) is not { } body)
        {
            ManifestTelemetry.Record(activity, ManifestOutcome.TooLarge);
            return Error(ManifestError.TooLarge, StatusCodes.Status413PayloadTooLarge);
        }

        var target = new ManifestTarget(CanonicalId.Capability(origin, capability.Id), $"{publisher.Handle}/{capability.Name}");
        var run = validator.Validate(body, target);
        ManifestTelemetry.Record(activity, run);
        http.Response.StatusCode = StatusOf(run.Result);
        return TypedResults.Bytes(ManifestJson.Serialize(run.Result), "application/json; charset=utf-8");
    }

    /// <summary>
    /// 200 accepted; a rejection answers 400 when the document is malformed, 413 when it is too large and 422 otherwise,
    /// always with the result body (maintainer decision 2026-11-18, "result-status-codes", T098).
    /// </summary>
    internal static int StatusOf(ValidationResult result) => result switch
    {
        RejectedResult { Problems: var problems } when problems.Any(p => p.Category == ManifestRules.Name(ProblemCategory.Malformed)) => StatusCodes.Status400BadRequest,
        RejectedResult { Problems: var problems } when problems.Any(p => p.Category == ManifestRules.Name(ProblemCategory.TooLarge)) => StatusCodes.Status413PayloadTooLarge,
        RejectedResult => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status200OK,
    };

    /// <summary><c>application/json</c>, with or without parameters (case-insensitive media type).</summary>
    internal static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var media)
        && string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The body through a counting read capped at 1 MiB; null when it is larger (research R4).</summary>
    internal static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static NotFound NotFound(Activity? activity)
    {
        ManifestTelemetry.Record(activity, ManifestOutcome.NotFound);
        return TypedResults.NotFound();
    }

    private static JsonHttpResult<ManifestError> Error(string error, int status) => TypedResults.Json(new ManifestError(error), statusCode: status);
}
