using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Gozleme.Api.Children;
using Gozleme.Api.Identity;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Adapters;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Revisions;

/// <summary>
/// The revision API (GOZ-0007 contracts/revision-api.openapi.yaml, research R12, R13): publish, list, read, four
/// downloads, the key list and a public-key download, mapped on the organizations group, so every call runs in one
/// <see cref="TenantTransaction"/> with <c>no-store</c>. The order is origin verified (503) → membership (identical
/// empty 404) → permission (403) → target (the same empty 404 for malformed, unknown, foreign or misplaced IDs); a
/// publication then checks suspension (409) → budget (429) → media type (415) → size (413) → GOZ-0006 validation,
/// unchanged (a rejection answers exactly as validate) → duplicate projection entries or U+0000 strings (422
/// <c>publish.duplicate_entry</c>, <c>publish.nul_character</c>, GOZ-0008) → version (409) → signing key, rollout gap
/// or a child-table refusal (503) → 201. The body is never read before the media-type step. There is no update or delete route: the router answers 405 on the revision.
/// </summary>
internal static class RevisionEndpoints
{
    private const string Capability = "/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}";
    private const string Revision = Capability + "/revisions/{revisionId}";
    private const string Keys = "/{organizationId}/publishers/{publisherId}/signing-keys";

    public static readonly IReadOnlyList<string> Parts = ["native", "projection", "statement", "signature"];

    public static RouteGroupBuilder MapRevisionEndpoints(this RouteGroupBuilder orgs)
    {
        orgs.MapPost(Capability + "/revisions", PublishAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestPublish));
        orgs.MapGet(Capability + "/revisions", ListAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestList));
        orgs.MapGet(Revision, ReadAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        foreach (var part in Parts)
        {
            // One fixed route per part (contract): an unknown part matches nothing and answers 404.
            orgs.MapGet(Revision + "/" + part, (string organizationId, string publisherId, string capabilityId, string revisionId, HttpContext http,
                    GozlemeDbContext db, PublisherStore publishers, RevisionStore store, RegistryOriginReader origins, CancellationToken ct) =>
                    DownloadAsync(part, organizationId, publisherId, capabilityId, revisionId, http, db, publishers, store, origins, ct))
                .WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        }

        orgs.MapGet(Keys, KeysAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        orgs.MapGet(Keys + "/{keyId}/public-key", PublicKeyAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        return orgs;
    }

    private static async Task<IResult> PublishAsync(
        string organizationId, string publisherId, string capabilityId, HttpContext http, GozlemeDbContext db, PublisherStore publishers,
        RegistryOriginReader origins, RevisionPublicationLimiter limiter, ManifestValidator validator, RevisionPublisher publisher,
        SecurityAudit audit, CancellationToken ct)
    {
        using var activity = RevisionTelemetry.StartPublish();
        var span = new Publish(activity);
        var target = await ResolveAsync(OrgAction.ManifestPublish, organizationId, publisherId, capabilityId, http, db, publishers, origins, span, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        var (origin, membership, owner, capability) = (target.Origin!, target.Membership!, target.Publisher!, target.Capability!);
        if (!await publishers.IsActiveAsync(owner, ct))
        {
            return Error(span, PublishOutcome.Suspended, RevisionError.Suspended, StatusCodes.Status409Conflict);
        }

        if (await limiter.TryAcquireAsync(membership.UserId, ct) is { } retryAfter)
        {
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Error(span, PublishOutcome.RateLimited, RevisionError.RateLimited, StatusCodes.Status429TooManyRequests);
        }

        if (!ManifestEndpoints.IsJson(http.Request.ContentType))
        {
            return Error(span, PublishOutcome.UnsupportedMediaType, RevisionError.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        }

        if (await ManifestEndpoints.ReadBodyAsync(http.Request.Body, ct) is not { } body)
        {
            return Error(span, PublishOutcome.TooLarge, RevisionError.TooLarge, StatusCodes.Status413PayloadTooLarge);
        }

        // FR-006: GOZ-0006 validation, unchanged; a rejection is answered byte-identically to the validate endpoint.
        var run = validator.Validate(body, new ManifestTarget(CanonicalId.Capability(origin, capability.Id), $"{owner.Handle}/{capability.Name}"));
        if (run.Result is not AcceptedResult accepted)
        {
            RevisionTelemetry.RecordPublish(activity, PublishOutcome.Rejected, run.Family);
            http.Response.StatusCode = ManifestEndpoints.StatusOf(run.Result);
            return TypedResults.Bytes(ManifestJson.Serialize(run.Result), "application/json; charset=utf-8");
        }

        // GOZ-0008 FR-005a: publish-time rules, so validate (unchanged) can accept what publication refuses.
        if ((IReadOnlyList<ManifestProblem>)[
                .. ChildEntries.Duplicates(accepted.Projection.Entries),
                .. ChildEntries.NulCharacters(ManifestJson.ProjectionNode(accepted.Projection)),
                .. ChildEntries.EnvelopeNulCharacters(accepted.Envelope)]
            is { Count: > 0 } problems)
        {
            RevisionTelemetry.RecordPublish(activity, PublishOutcome.Rejected, run.Family);
            http.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return TypedResults.Bytes(ManifestJson.Serialize(ChildEntries.Rejected(problems)), "application/json; charset=utf-8");
        }

        var publication = await publisher.PublishAsync(owner, capability, accepted, origin, membership.UserId, ct);
        switch (publication.Result)
        {
            case PublishResult.VersionExists:
                return Error(span, PublishOutcome.VersionExists, RevisionError.VersionExists, StatusCodes.Status409Conflict, run.Family);
            case PublishResult.Suspended:
                return Error(span, PublishOutcome.Suspended, RevisionError.Suspended, StatusCodes.Status409Conflict, run.Family);
            case PublishResult.Unavailable:
                return Error(span, PublishOutcome.Unavailable, RevisionError.Unavailable, StatusCodes.Status503ServiceUnavailable, run.Family);
        }

        var stored = publication.Revision!;
        RevisionTelemetry.Revision(activity, stored.Revision.Id);
        RevisionTelemetry.RecordPublish(activity, PublishOutcome.Published, run.Family);
        TenantTransaction.AfterCommit(http, () =>
        {
            ChildTelemetry.RecordCommitted(stored.Revision.Family, publication.Assignment!);
            audit.RevisionPublished(stored.Revision.Id, capability.Id, owner.Id, owner.OrganizationId, membership.UserId);
            if (publication.KeyCreated)
            {
                audit.SigningKeyCreated(stored.Key.Id, owner.Id, owner.OrganizationId, membership.UserId);
            }
        });
        var location = $"/api/v1/organizations/{owner.OrganizationId}/publishers/{owner.Id}/capabilities/{capability.Id}/revisions/{stored.Revision.Id}";
        return TypedResults.Created(location, Detail(stored, origin, publisherSuspended: false));
    }

    private static async Task<IResult> ListAsync(
        string organizationId, string publisherId, string capabilityId, string? after, string? limit, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, RevisionStore store, RegistryOriginReader origins, IOptions<ManifestOptions> options, CancellationToken ct)
    {
        var target = await ResolveAsync(OrgAction.ManifestList, organizationId, publisherId, capabilityId, http, db, publishers, origins, null, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        var (origin, owner, capability) = (target.Origin!, target.Publisher!, target.Capability!);
        var from = int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0;
        var take = int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, RevisionStore.MaxPageSize) : options.Value.RevisionPageSize;
        var rows = await store.ListAsync(capability, from, take + 1, ct);
        var page = rows.Take(take).ToList();
        var next = rows.Count > take ? page[^1].Revision.Sequence : (int?)null;
        var suspended = !await publishers.IsActiveAsync(owner, ct);
        return TypedResults.Ok(new RevisionList(suspended, [.. page.Select(r => Summary(r, origin))], next));
    }

    private static async Task<IResult> ReadAsync(
        string organizationId, string publisherId, string capabilityId, string revisionId, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, RevisionStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        var target = await ResolveAsync(OrgAction.ManifestRead, organizationId, publisherId, capabilityId, http, db, publishers, origins, null, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        if (await store.FindAsync(target.Capability!, revisionId, ct) is not { } stored)
        {
            return TypedResults.NotFound();
        }

        var suspended = !await publishers.IsActiveAsync(target.Publisher!, ct);
        return TypedResults.Ok(Detail(stored, target.Origin!, suspended));
    }

    /// <summary>The exact stored bytes, as attachments that a browser never renders (contracts/attestation-statement-v1.md "Downloads").</summary>
    private static async Task<IResult> DownloadAsync(
        string part, string organizationId, string publisherId, string capabilityId, string revisionId, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, RevisionStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        var target = await ResolveAsync(OrgAction.ManifestRead, organizationId, publisherId, capabilityId, http, db, publishers, origins, null, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        if (await store.FindAsync(target.Capability!, revisionId, ct) is not { } stored)
        {
            return TypedResults.NotFound();
        }

        var revision = stored.Revision;
        var (bytes, mediaType, file) = part switch
        {
            "native" => (revision.NativeDocument, NativeMediaType(revision.NativeEncoding), "native." + NativeExtension(revision.NativeEncoding)),
            "projection" => (revision.Projection, "application/json", "projection.json"),
            "statement" => (stored.Attestation.Statement, "application/json", "statement.json"),
            _ => (stored.Attestation.Signature, "application/octet-stream", "statement.sig"),
        };
        return Attachment(http, bytes, mediaType, file);
    }

    private static async Task<IResult> KeysAsync(
        string organizationId, string publisherId, HttpContext http, GozlemeDbContext db, PublisherStore publishers, RevisionStore store,
        RegistryOriginReader origins, CancellationToken ct)
    {
        var target = await ResolveAsync(OrgAction.ManifestRead, organizationId, publisherId, null, http, db, publishers, origins, null, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        var keys = await store.KeysAsync(target.Publisher!, ct);
        return TypedResults.Ok(new SigningKeyList(target.Publisher!.Id, [.. keys.Select(k => new SigningKeyView(
            k.Key.Id, k.Key.KeyId, k.Key.Algorithm, k.Key.Generation, k.Key.CreatedAt,
            k.Retirement is null ? KeyState.Active : KeyState.Retired, k.Retirement?.RetiredAt, k.Retirement?.Reason, k.Retirement?.SuccessorKeyId))]));
    }

    private static async Task<IResult> PublicKeyAsync(
        string organizationId, string publisherId, string keyId, HttpContext http, GozlemeDbContext db, PublisherStore publishers, RevisionStore store,
        RegistryOriginReader origins, CancellationToken ct)
    {
        var target = await ResolveAsync(OrgAction.ManifestRead, organizationId, publisherId, null, http, db, publishers, origins, null, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        if (await store.FindKeyAsync(target.Publisher!, keyId, ct) is not { } key)
        {
            return TypedResults.NotFound();
        }

        return Attachment(http, Encoding.ASCII.GetBytes(Signing.Ed25519Signer.Pem(key.PublicKey)), "application/x-pem-file", "public-key.pem");
    }

    /// <summary>
    /// Origin (503) → membership (identical empty 404) → permission (403) → publisher and, when named, capability
    /// (the same empty 404). The decision point records a denial (audit 1301); IDs go on the span only after membership.
    /// </summary>
    /// <summary>The read form of <see cref="ResolveAsync"/>, shared with the GOZ-0008 child endpoints (no publication span).</summary>
    internal static Task<Target> ResolveReadAsync(
        string action, string organizationId, string publisherId, string capabilityId, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, RegistryOriginReader origins, CancellationToken ct) =>
        ResolveAsync(action, organizationId, publisherId, capabilityId, http, db, publishers, origins, null, ct);

    private static async Task<Target> ResolveAsync(
        string action, string organizationId, string publisherId, string? capabilityId, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, RegistryOriginReader origins, Publish? publish, CancellationToken ct)
    {
        var activity = publish?.Activity;
        if (await origins.GetVerifiedOriginAsync(ct) is not { } origin)
        {
            return new(Error(publish, PublishOutcome.Unavailable, RevisionError.Unavailable, StatusCodes.Status503ServiceUnavailable));
        }

        var access = await OrganizationAccess.AuthorizeAsync(db, organizationId, http, action, ct);
        if (access.Membership is not { } membership)
        {
            return new(NotFound(publish));
        }

        RevisionTelemetry.Organization(activity, membership.OrganizationId);
        if (!access.Allowed)
        {
            return new(Error(publish, PublishOutcome.Forbidden, RevisionError.Forbidden, StatusCodes.Status403Forbidden));
        }

        if (await publishers.FindAsync(membership.OrganizationId, publisherId, ct) is not { } publisher)
        {
            return new(NotFound(publish));
        }

        Capability? capability = null;
        if (capabilityId is not null && (capability = await publishers.FindCapabilityAsync(publisher, capabilityId, ct)) is null)
        {
            return new(NotFound(publish));
        }

        if (capability is not null)
        {
            RevisionTelemetry.Target(activity, publisher.Id, capability.Id);
        }

        return new(null, origin, membership, publisher, capability);
    }

    private static RevisionSummary Summary(StoredRevision stored, string origin)
    {
        var r = stored.Revision;
        return new(r.Id, CanonicalId.Capability(origin, r.CapabilityId), r.Version, r.Sequence, r.PublishedAt, r.PublishedBy, r.NativeDigest, Verify(stored, origin));
    }

    private static RevisionDetail Detail(StoredRevision stored, string origin, bool publisherSuspended)
    {
        var (r, a) = (stored.Revision, stored.Attestation);
        return new RevisionDetail(
            r.Id, CanonicalId.Capability(origin, r.CapabilityId), r.Version, r.Sequence, r.PublishedAt, r.PublishedBy, r.NativeDigest, Verify(stored, origin),
            CanonicalId.Publisher(origin, r.PublisherId), publisherSuspended,
            JsonNode.Parse(r.EnvelopeSummary) ?? new JsonObject(),
            new NativeView(r.NativeFormat, r.NativeFormatVersion, r.NativeEncoding, Encoding.UTF8.GetString(r.NativeDocument), r.NativeDigest, r.NativeByteLength),
            Parse(r.Projection), r.ProjectionDigest, new AdapterView(r.AdapterId, r.AdapterVersion), r.RuleCatalogueVersion, r.SchemaBundleVersion,
            new AttestationView(a.StatementVersion, AttestationStatement.ParseCanonical(a.Statement) ?? new JsonObject(), stored.Key.Algorithm, stored.Key.KeyId,
                AttestationProof.For(origin, r.PublisherId)));
    }

    /// <summary>Computed now from the stored bytes, never read from a flag (FR-010), with a span and count per verification.</summary>
    private static VerificationView Verify(StoredRevision stored, string origin)
    {
        var verification = RevisionVerifier.Verify(stored.Revision, stored.Attestation, stored.Key, stored.Retirement, origin);
        RevisionTelemetry.RecordVerification(stored.Revision.Id, verification);
        return VerificationView.From(verification);
    }

    /// <summary>The stored projection as a tree; tampered bytes that are not JSON are shown as an empty object (verification fails).</summary>
    private static JsonNode Parse(byte[] json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    internal static FileContentHttpResult Attachment(HttpContext http, byte[] bytes, string mediaType, string file)
    {
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{file}\"";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Bytes(bytes, mediaType);
    }

    internal static string NativeMediaType(string encoding) => encoding switch
    {
        NativeEncoding.Json => "application/json; charset=utf-8",
        NativeEncoding.Yaml => "application/yaml; charset=utf-8",
        _ => "text/markdown; charset=utf-8",
    };

    private static string NativeExtension(string encoding) => encoding switch
    {
        NativeEncoding.Json => "json",
        NativeEncoding.Yaml => "yaml",
        _ => "md",
    };

    private static NotFound NotFound(Publish? publish)
    {
        publish?.Record(PublishOutcome.NotFound);
        return TypedResults.NotFound();
    }

    private static JsonHttpResult<RevisionError> Error(Publish? publish, string outcome, string error, int status, string family = ManifestValidator.UnknownFamily)
    {
        publish?.Record(outcome, family);
        return TypedResults.Json(new RevisionError(error), statusCode: status);
    }

    /// <summary>The publication span, which may be null when nothing listens; its counter is recorded either way.</summary>
    private sealed record Publish(Activity? Activity)
    {
        public void Record(string outcome, string family = ManifestValidator.UnknownFamily) => RevisionTelemetry.RecordPublish(Activity, outcome, family);
    }

    internal sealed record Target(IResult? Failure, string? Origin = null, Membership? Membership = null, Publisher? Publisher = null, Capability? Capability = null);
}
