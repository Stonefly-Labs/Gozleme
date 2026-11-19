using Gozleme.Api.Manifests.Projection;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions;
using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Children;

/// <summary>
/// Child identity and projection generation storage (GOZ-0008 data-model.md) in the caller's transaction: the request's
/// tenant transaction under forced RLS, or the operator's install transaction. Writes go in one statement per table, so
/// the statement-level entry check parses the projection once (research R4, R12). Reads are bounded pages; a malformed,
/// unknown or misplaced ID is null.
/// </summary>
internal sealed class ChildStore(GozlemeDbContext db)
{
    public const int MaxPageSize = 100;

    /// <summary>One query: the capability's children, as the matcher needs them (matchable keys, duplicate count, next ordinal).</summary>
    public async Task<ExistingChildren> ExistingAsync(Guid capabilityId, Guid organizationId, CancellationToken ct)
    {
        var children = await db.CapabilityChildren.AsNoTracking()
            .Where(c => c.CapabilityId == capabilityId && c.OrganizationId == organizationId)
            .Select(c => new { c.Id, c.Kind, c.NativeName, c.IsDuplicate, c.Ordinal })
            .ToListAsync(ct);
        return new ExistingChildren(
            children.Where(c => !c.IsDuplicate).ToDictionary(c => new ChildKey(c.Kind, c.NativeName), c => c.Id),
            children.Count(c => c.IsDuplicate),
            1 + (children.Count == 0 ? 0 : children.Max(c => c.Ordinal)));
    }

    /// <summary>
    /// Assigns the revision's entries against the capability's children and inserts the new children and the revision's
    /// child records, all inside the assignment span; then generation 1 inside the generation span (T076). Counters are
    /// the caller's, after commit.
    /// </summary>
    public async Task<Assignment> RecordAsync(ManifestRevision revision, IReadOnlyList<ProjectionEntry> entries, AssignMode mode, ExistingChildren? existing, CancellationToken ct)
    {
        Assignment assignment;
        using (var span = ChildTelemetry.StartAssign())
        {
            assignment = ChildAssigner.Assign(existing ?? await ExistingAsync(revision.CapabilityId, revision.OrganizationId, ct), entries, mode);
            await InsertChildrenAsync(revision, assignment, ct);
            ChildTelemetry.Assigned(span, revision.Family, assignment, mode);
        }

        using (ChildTelemetry.StartGeneration(1))
        {
            await InsertGenerationAsync(revision, ct);
        }

        return assignment;
    }

    /// <summary>
    /// Runs the deferred completeness checks now (F4): a refusal surfaces inside publication, where it answers
    /// <c>unavailable</c>, instead of at the request's commit.
    /// </summary>
    public Task CheckCompleteAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS gozleme.capability_children_recorded, gozleme.manifest_revisions_children IMMEDIATE", ct);

    /// <summary>Inserts the new children and the revision's child records.</summary>
    private async Task InsertChildrenAsync(ManifestRevision revision, Assignment assignment, CancellationToken ct)
    {
        if (assignment.Created.Count > 0)
        {
            var created = assignment.Created;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO gozleme.capability_children (id, organization_id, capability_id, kind, native_name, is_duplicate, ordinal, first_revision_id, created_at)
                SELECT c.id, {revision.OrganizationId}, {revision.CapabilityId}, c.kind, c.native_name, c.is_duplicate, c.ordinal, {revision.Id}, now()
                FROM unnest({created.Select(c => c.Id).ToArray()}, {created.Select(c => c.Kind).ToArray()}, {created.Select(c => c.NativeName).ToArray()},
                            {created.Select(c => c.IsDuplicate).ToArray()}, {created.Select(c => c.Ordinal).ToArray()})
                  AS c(id, kind, native_name, is_duplicate, ordinal)
                ORDER BY c.ordinal
                """, ct);
        }

        if (assignment.Records.Count > 0)
        {
            var records = assignment.Records;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO gozleme.revision_children (revision_id, entry_index, organization_id, child_id, kind, native_name, title)
                SELECT {revision.Id}, r.entry_index, {revision.OrganizationId}, r.child_id, r.kind, r.native_name, r.title
                FROM unnest({records.Select(r => r.EntryIndex).ToArray()}, {records.Select(r => r.ChildId).ToArray()}, {records.Select(r => r.Kind).ToArray()},
                            {records.Select(r => r.NativeName).ToArray()}, {records.Select(r => r.Title).ToArray()})
                  AS r(entry_index, child_id, kind, native_name, title)
                """, ct);
        }

    }

    /// <summary>Inserts generation 1 (no bytes: it is the stored projection, FR-009).</summary>
    private async Task InsertGenerationAsync(ManifestRevision revision, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
            VALUES ({Guid.CreateVersion7()}, {revision.OrganizationId}, {revision.Id}, 1, {revision.AdapterId}, {revision.AdapterVersion}, {revision.ProjectionVersion}, NULL, NULL, now())
            """, ct);
    }

    /// <summary>A page of the capability's children by ordinal.</summary>
    public Task<List<CapabilityChild>> ListAsync(Capability capability, int afterOrdinal, int take, CancellationToken ct) =>
        db.CapabilityChildren.AsNoTracking()
            .Where(c => c.CapabilityId == capability.Id && c.OrganizationId == capability.OrganizationId && c.Ordinal > afterOrdinal)
            .OrderBy(c => c.Ordinal).Take(take).ToListAsync(ct);

    public async Task<CapabilityChild?> FindAsync(Capability capability, string? childId, CancellationToken ct) =>
        Guid.TryParse(childId, out var id)
            ? await db.CapabilityChildren.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.CapabilityId == capability.Id && c.OrganizationId == capability.OrganizationId, ct)
            : null;

    /// <summary>For each child: the latest revision that declares it and how many do.</summary>
    public async Task<Dictionary<Guid, (ChildLatest Latest, int PresentIn)>> PresenceAsync(IReadOnlyCollection<Guid> childIds, CancellationToken ct)
    {
        var ids = childIds.ToList();
        var rows = await (from rc in db.RevisionChildren.AsNoTracking()
                          join r in db.ManifestRevisions.AsNoTracking() on rc.RevisionId equals r.Id
                          where ids.Contains(rc.ChildId)
                          select new { rc.ChildId, RevisionId = r.Id, r.Sequence, r.Version, rc.NativeName, rc.Title }).ToListAsync(ct);
        return rows.GroupBy(r => r.ChildId).ToDictionary(g => g.Key, g =>
        {
            var latest = g.MaxBy(r => r.Sequence)!;
            return (new ChildLatest(latest.RevisionId, latest.Sequence, latest.Version, latest.NativeName, latest.Title), g.Count());
        });
    }

    /// <summary>A page of the capability's revisions by sequence, each with the child's record there (absent: null).</summary>
    public async Task<List<ChildHistoryEntry>> HistoryAsync(Capability capability, CapabilityChild child, int afterSequence, int take, CancellationToken ct)
    {
        var revisions = await db.ManifestRevisions.AsNoTracking()
            .Where(r => r.CapabilityId == capability.Id && r.OrganizationId == capability.OrganizationId && r.Sequence > afterSequence)
            .OrderBy(r => r.Sequence).Take(take)
            .Select(r => new { r.Id, r.Sequence, r.Version }).ToListAsync(ct);
        var ids = revisions.Select(r => r.Id).ToList();
        var records = await db.RevisionChildren.AsNoTracking()
            .Where(rc => rc.ChildId == child.Id && ids.Contains(rc.RevisionId)).ToDictionaryAsync(rc => rc.RevisionId, ct);
        return [.. revisions.Select(r => records.TryGetValue(r.Id, out var rc)
            ? new ChildHistoryEntry(r.Id, r.Sequence, r.Version, true, rc.NativeName, rc.Title)
            : new ChildHistoryEntry(r.Id, r.Sequence, r.Version, false, null, null))];
    }

    /// <summary>The revision under the capability, without its documents' attestation (child and generation routes).</summary>
    public async Task<ManifestRevision?> FindRevisionAsync(Capability capability, string? revisionId, CancellationToken ct) =>
        Guid.TryParse(revisionId, out var id)
            ? await db.ManifestRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.CapabilityId == capability.Id && r.OrganizationId == capability.OrganizationId, ct)
            : null;

    /// <summary>A page of the revision's child records by entry index (exclusive cursor), with each child's duplicate flag.</summary>
    public async Task<List<(RevisionChild Record, bool Duplicate)>> RevisionChildrenAsync(ManifestRevision revision, int afterIndex, int take, CancellationToken ct)
    {
        var rows = await (from rc in db.RevisionChildren.AsNoTracking()
                          join c in db.CapabilityChildren.AsNoTracking() on rc.ChildId equals c.Id
                          where rc.RevisionId == revision.Id && rc.OrganizationId == revision.OrganizationId && rc.EntryIndex > afterIndex
                          orderby rc.EntryIndex
                          select new { Record = rc, c.IsDuplicate }).Take(take).ToListAsync(ct);
        return [.. rows.Select(r => (r.Record, r.IsDuplicate))];
    }

    public Task<List<ProjectionGeneration>> GenerationsAsync(ManifestRevision revision, CancellationToken ct) =>
        db.ProjectionGenerations.AsNoTracking()
            .Where(g => g.RevisionId == revision.Id && g.OrganizationId == revision.OrganizationId)
            .OrderBy(g => g.Generation).ToListAsync(ct);

    /// <summary>Whether the capability has a revision without generation 1: the rollout gap (maintainer-approved A7).</summary>
    public Task<bool> HasUninstalledRevisionAsync(Guid capabilityId, CancellationToken ct) =>
        db.ManifestRevisions.AsNoTracking()
            .AnyAsync(r => r.CapabilityId == capabilityId && !db.ProjectionGenerations.Any(g => g.RevisionId == r.Id && g.Generation == 1), ct);
}
