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
/// recorded as duplicate children instead of refused. Revisions and attestations are only read, so every revision
/// verifies as before. A repeat run finds nothing to do.
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

            var existing = await children.ExistingAsync(capability, pending[0].OrganizationId, ct);
            foreach (var revision in pending)
            {
                var assignment = ChildAssigner.Assign(existing, ChildEntries.FromProjection(revision.Projection), AssignMode.Install);
                await children.InsertAsync(revision, assignment, ct);
                ChildTelemetry.RecordAssign(revision.Family, assignment, AssignMode.Install);
                ChildTelemetry.RecordGeneration(revision.Family, 1, ChildOutcome.Recorded);
                existing = existing.Then(assignment);
            }

            await tx.CommitAsync(ct);
            audit.ChildrenInstalled(capability, pending.Count);
            revisions += pending.Count;
            installed++;
        }

        ChildTelemetry.Installed(activity, revisions, installed);
        return new Result(revisions, installed);
    }

    private static IQueryable<ManifestRevision> Pending(GozlemeDbContext db) =>
        db.ManifestRevisions.AsNoTracking().Where(r => !db.ProjectionGenerations.Any(g => g.RevisionId == r.Id && g.Generation == 1));
}
