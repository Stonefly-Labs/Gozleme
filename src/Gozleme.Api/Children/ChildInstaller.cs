using Gozleme.Api.Identity;
using Gozleme.Api.Persistence;
using Gozleme.Api.Revisions;
using Microsoft.EntityFrameworkCore;

namespace Gozleme.Api.Children;

/// <summary>
/// The install step (GOZ-0008 FR-016, FR-016a, research R6; maintainer-approved A2): the last step of every
/// <c>migrate</c> run, as the operator (the owner login with no account context). Revisions without generation 1 get
/// their children and generation 1 by the same rule as publication, capability by capability in sequence order under
/// the publication lock <c>gozleme.revisions:&lt;capability&gt;</c>, one transaction per capability. Duplicate keys are
/// recorded as duplicate children instead of refused. A revision whose projection holds U+0000 is skipped and audited
/// by ID (maintainer ruling F3). Revisions and attestations are only read, so every revision verifies as before. A
/// repeat run finds nothing to do but the skipped revisions, audited again.
/// </summary>
internal static class ChildInstaller
{
    public sealed record Result(int Revisions, int Capabilities);

    public static async Task<Result> RunAsync(GozlemeDbContext db, SecurityAudit audit, CancellationToken ct = default)
    {
        using var activity = ChildTelemetry.StartInstall();
        var capabilities = await Pending(db).Select(r => r.CapabilityId).Distinct().OrderBy(id => id).ToListAsync(ct);
        var (revisions, installed) = (0, 0);
        var children = new ChildStore(db);
        foreach (var capability in capabilities)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('gozleme.revisions:' || {capability.ToString()}, 0))", ct);
            var pending = await Pending(db).Where(r => r.CapabilityId == capability).OrderBy(r => r.Sequence).ToListAsync(ct);
            if (pending.Count == 0)
            {
                await tx.RollbackAsync(ct);
                continue;
            }

            // F3: PostgreSQL text and jsonb cannot hold U+0000, so such a revision is skipped, audited by ID, and left
            // without generation 1 (its capability keeps answering the rollout-gap 503); the others are installed.
            var skipped = pending.Where(r => ChildEntries.HasNulCharacter(r.Projection)).Select(r => r.Id).ToList();
            var existing = await children.ExistingAsync(capability, pending[0].OrganizationId, ct);
            var recorded = new List<(string Family, Assignment Assignment)>();
            foreach (var revision in pending.Where(r => !skipped.Contains(r.Id)))
            {
                var assignment = await children.RecordAsync(revision, ChildEntries.FromProjection(revision.Projection), AssignMode.Install, existing, ct);
                recorded.Add((revision.Family, assignment));
                existing = existing.Then(assignment);
            }

            await tx.CommitAsync(ct);
            foreach (var id in skipped)
            {
                audit.ChildrenInstallSkipped(id);
            }

            foreach (var (family, assignment) in recorded)
            {
                ChildTelemetry.RecordCommitted(family, assignment);
            }

            if (recorded.Count > 0)
            {
                audit.ChildrenInstalled(capability, recorded.Count);
                revisions += recorded.Count;
                installed++;
            }
        }

        ChildTelemetry.Installed(activity, revisions, installed);
        return new Result(revisions, installed);
    }

    private static IQueryable<ManifestRevision> Pending(GozlemeDbContext db) =>
        db.ManifestRevisions.AsNoTracking().Where(r => !db.ProjectionGenerations.Any(g => g.RevisionId == r.Id && g.Generation == 1));
}
