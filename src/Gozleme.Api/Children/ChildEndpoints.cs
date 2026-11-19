using System.Diagnostics;
using System.Globalization;
using Gozleme.Api.Manifests;
using Gozleme.Api.Organizations;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions;
using Gozleme.Api.Tenancy;
using Microsoft.Extensions.Options;

namespace Gozleme.Api.Children;

/// <summary>
/// The child identity API (GOZ-0008 contracts/children-api.openapi.yaml, research R9): five read-only routes on the
/// existing <c>manifest.list</c> and <c>manifest.read</c> actions, mapped on the organizations group, so every call
/// runs in one <see cref="TenantTransaction"/> with <c>no-store</c>. The order is the GOZ-0007 one, reused: origin
/// verified (503) → membership (identical empty 404) → permission (403) → target (the same empty 404 for malformed,
/// unknown, foreign or misplaced publisher, capability, revision, child or generation IDs, FR-015). Child and generation
/// IDs are looked up only in their own table, so one is never found where the other is expected (FR-010). There is no
/// write route: the router answers 405 for any other method on these paths.
/// </summary>
internal static class ChildEndpoints
{
    private const string Capability = "/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}";
    private const string Revision = Capability + "/revisions/{revisionId}";

    public static RouteGroupBuilder MapChildEndpoints(this RouteGroupBuilder orgs)
    {
        orgs.MapGet(Capability + "/children", ListAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestList));
        orgs.MapGet(Capability + "/children/{childId}", ReadAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        orgs.MapGet(Revision + "/children", RevisionChildrenAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        orgs.MapGet(Revision + "/projections", GenerationsAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        orgs.MapGet(Revision + "/projections/{generationId}", DownloadAsync).WithMetadata(new OrgActionAttribute(OrgAction.ManifestRead));
        return orgs;
    }

    private static async Task<IResult> ListAsync(
        string organizationId, string publisherId, string capabilityId, string? after, string? limit, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, ChildStore store, RegistryOriginReader origins, IOptions<ManifestOptions> options, CancellationToken ct)
    {
        var target = await RevisionEndpoints.ResolveReadAsync(OrgAction.ManifestList, organizationId, publisherId, capabilityId, http, db, publishers, origins, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        var (origin, capability) = (target.Origin!, target.Capability!);
        var take = Take(limit, options);
        var rows = await store.ListAsync(capability, Cursor(after, 0), take + 1, ct);
        var page = rows.Take(take).ToList();
        var presence = await store.PresenceAsync([.. page.Select(c => c.Id)], ct);
        return TypedResults.Ok(new ChildList(
            [.. page.Where(c => presence.ContainsKey(c.Id)).Select(c => Summary(c, origin, presence[c.Id]))],
            rows.Count > take ? page[^1].Ordinal : null));
    }

    private static async Task<IResult> ReadAsync(
        string organizationId, string publisherId, string capabilityId, string childId, string? after, string? limit, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, ChildStore store, RegistryOriginReader origins, IOptions<ManifestOptions> options, CancellationToken ct)
    {
        var target = await RevisionEndpoints.ResolveReadAsync(OrgAction.ManifestRead, organizationId, publisherId, capabilityId, http, db, publishers, origins, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        var (origin, capability) = (target.Origin!, target.Capability!);
        if (await store.FindAsync(capability, childId, ct) is not { } child
            || (await store.PresenceAsync([child.Id], ct)).GetValueOrDefault(child.Id) is not (not null, _) presence)
        {
            return TypedResults.NotFound();
        }

        ChildTelemetry.Child(Activity.Current, child.Id);
        var take = Take(limit, options);
        var rows = await store.HistoryAsync(capability, child, Cursor(after, 0), take + 1, ct);
        var page = rows.Take(take).ToList();
        return TypedResults.Ok(new ChildDetail(
            Summary(child, origin, presence),
            new ChildCapability(capability.Id, CanonicalId.Capability(origin, capability.Id), capability.DisplayName),
            page,
            rows.Count > take ? page[^1].Sequence : null));
    }

    private static async Task<IResult> RevisionChildrenAsync(
        string organizationId, string publisherId, string capabilityId, string revisionId, string? after, string? limit, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, ChildStore store, RegistryOriginReader origins, IOptions<ManifestOptions> options, CancellationToken ct)
    {
        var target = await RevisionEndpoints.ResolveReadAsync(OrgAction.ManifestRead, organizationId, publisherId, capabilityId, http, db, publishers, origins, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        if (await store.FindRevisionAsync(target.Capability!, revisionId, ct) is not { } revision)
        {
            return TypedResults.NotFound();
        }

        var origin = target.Origin!;
        var take = Take(limit, options);
        var rows = await store.RevisionChildrenAsync(revision, Cursor(after, -1), take + 1, ct);
        var page = rows.Take(take).ToList();
        return TypedResults.Ok(new RevisionChildList(
            [.. page.Select(r => new RevisionChildView(r.Record.EntryIndex, r.Record.ChildId, CanonicalId.Child(origin, revision.CapabilityId, r.Record.Kind, r.Record.ChildId),
                r.Record.Kind, r.Record.NativeName, r.Record.Title, r.Duplicate))],
            rows.Count > take ? page[^1].Record.EntryIndex : null));
    }

    private static async Task<IResult> GenerationsAsync(
        string organizationId, string publisherId, string capabilityId, string revisionId, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, ChildStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        var target = await RevisionEndpoints.ResolveReadAsync(OrgAction.ManifestRead, organizationId, publisherId, capabilityId, http, db, publishers, origins, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        if (await store.FindRevisionAsync(target.Capability!, revisionId, ct) is not { } revision)
        {
            return TypedResults.NotFound();
        }

        var generations = await store.GenerationsAsync(revision, ct);
        var views = new List<ProjectionGenerationView>(generations.Count);
        ProjectionGeneration? previous = null;
        foreach (var generation in generations)
        {
            var (bytes, digest) = Bytes(revision, generation);
            views.Add(new ProjectionGenerationView(
                generation.Id, generation.Generation, new GenerationAdapter(generation.AdapterId, generation.AdapterVersion), generation.ProjectionVersion,
                digest, bytes.Length, generation.Generation == 1, generation.CreatedAt,
                previous is null ? null : Compare(previous, Bytes(revision, previous).Digest, generation, digest)));
            previous = generation;
        }

        return TypedResults.Ok(new GenerationList(views));
    }

    /// <summary>The exact generation bytes as an attachment; generation 1 serves the revision's stored, attested projection.</summary>
    private static async Task<IResult> DownloadAsync(
        string organizationId, string publisherId, string capabilityId, string revisionId, string generationId, HttpContext http, GozlemeDbContext db,
        PublisherStore publishers, ChildStore store, RegistryOriginReader origins, CancellationToken ct)
    {
        var target = await RevisionEndpoints.ResolveReadAsync(OrgAction.ManifestRead, organizationId, publisherId, capabilityId, http, db, publishers, origins, ct);
        if (target.Failure is { } failure)
        {
            return failure;
        }

        if (await store.FindRevisionAsync(target.Capability!, revisionId, ct) is not { } revision
            || !Guid.TryParse(generationId, out var id)
            || (await store.GenerationsAsync(revision, ct)).FirstOrDefault(g => g.Id == id) is not { } generation)
        {
            return TypedResults.NotFound();
        }

        ChildTelemetry.Generation(Activity.Current, generation.Id);
        return RevisionEndpoints.Attachment(http, Bytes(revision, generation).Bytes, "application/json", $"projection-generation-{generation.Generation}.json");
    }

    /// <summary>Generation 1 has no bytes of its own (FR-009): they are the revision's projection and digest.</summary>
    private static (byte[] Bytes, string Digest) Bytes(ManifestRevision revision, ProjectionGeneration generation) =>
        generation.Generation == 1 ? (revision.Projection, revision.ProjectionDigest) : (generation.Projection!, generation.ProjectionDigest!);

    /// <summary>Equal digests mean byte-identical projections, but only within one adapter and projection version (GOZ-0007-IF-09).</summary>
    internal static string Compare(ProjectionGeneration previous, string previousDigest, ProjectionGeneration current, string currentDigest) =>
        previous.AdapterId != current.AdapterId || previous.AdapterVersion != current.AdapterVersion || previous.ProjectionVersion != current.ProjectionVersion
            ? GenerationComparison.NotComparable
            : string.Equals(previousDigest, currentDigest, StringComparison.Ordinal) ? GenerationComparison.Identical : GenerationComparison.Different;

    private static ChildSummary Summary(CapabilityChild child, string origin, (ChildLatest Latest, int PresentIn) presence) => new(
        child.Id, CanonicalId.Child(origin, child.CapabilityId, child.Kind, child.Id), child.Kind, child.Ordinal, child.IsDuplicate, child.FirstRevisionId,
        presence.Latest, presence.PresentIn, child.CreatedAt);

    private static int Cursor(string? after, int none) =>
        int.TryParse(after, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= none ? value : none;

    private static int Take(string? limit, IOptions<ManifestOptions> options) =>
        int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, ChildStore.MaxPageSize) : options.Value.ChildPageSize;
}
