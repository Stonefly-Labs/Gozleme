using Gozleme.Api.Manifests.Projection;

namespace Gozleme.Api.Children;

/// <summary>Publication refuses duplicate keys before assignment; the install step records them (FR-016a).</summary>
internal enum AssignMode
{
    Publish,
    Install,
}

/// <summary>The match key: exact (ordinal) <c>kind</c> and <c>nativeName</c>, no case folding or normalization (R2).</summary>
internal readonly record struct ChildKey(string Kind, string NativeName);

/// <summary>A capability's children before a revision: the matchable (non-duplicate) ones by key, the count of duplicates and the next ordinal.</summary>
internal sealed record ExistingChildren(IReadOnlyDictionary<ChildKey, Guid> Matchable, int DuplicateCount, int NextOrdinal)
{
    public static readonly ExistingChildren None = new(new Dictionary<ChildKey, Guid>(), 0, 1);

    /// <summary>The children after <paramref name="assignment"/>, for the next revision of the same capability.</summary>
    public ExistingChildren Then(Assignment assignment)
    {
        var matchable = new Dictionary<ChildKey, Guid>(Matchable);
        foreach (var child in assignment.Created.Where(c => !c.IsDuplicate))
        {
            matchable[new(child.Kind, child.NativeName)] = child.Id;
        }

        return new(matchable, DuplicateCount + assignment.Counts.Duplicates, NextOrdinal + assignment.Created.Count);
    }
}

internal sealed record NewChild(Guid Id, string Kind, string NativeName, bool IsDuplicate, int Ordinal);

internal sealed record ChildRecord(int EntryIndex, Guid ChildId, string Kind, string NativeName, string? Title);

/// <summary>
/// Bounded counts for telemetry (R11). <see cref="Absent"/> is the earlier non-duplicate children the revision does not
/// declare plus every earlier duplicate child; <see cref="Duplicates"/> is the duplicate children created (install mode).
/// </summary>
internal sealed record ChildCounts(int Declared, int Created, int Matched, int Absent, int Duplicates);

internal sealed record Assignment(IReadOnlyList<NewChild> Created, IReadOnlyList<ChildRecord> Records, ChildCounts Counts);

/// <summary>
/// The continuity rule (maintainer Q2, clarify Q1; research R2): walk the entries in position order; an entry whose
/// key is a matchable child of the capability is that child, any other entry creates a child with the next ordinal.
/// Title, description and position never affect matching, so a rename is a new child and the old one is absent. In
/// install mode the first entry of a key takes the key and each further entry gets a new duplicate child that is never
/// matched; publication refuses such a projection first (<see cref="ChildEntries.Duplicates"/>). The result depends
/// only on the ordered projections, which makes it deterministic (SC-003); only the child UUIDs differ.
/// </summary>
internal static class ChildAssigner
{
    public static Assignment Assign(ExistingChildren existing, IReadOnlyList<ProjectionEntry> entries, AssignMode mode, Func<Guid>? newId = null)
    {
        newId ??= Guid.CreateVersion7;
        var created = new List<NewChild>();
        var records = new List<ChildRecord>(entries.Count);
        var seen = new Dictionary<ChildKey, Guid>();
        var (matched, duplicates) = (0, 0);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var key = new ChildKey(entry.Kind, entry.NativeName);
            Guid id;
            if (seen.ContainsKey(key))
            {
                if (mode == AssignMode.Publish)
                {
                    throw new InvalidOperationException("Publication must refuse duplicate entries before assignment.");
                }

                id = newId();
                created.Add(new NewChild(id, entry.Kind, entry.NativeName, true, existing.NextOrdinal + created.Count));
                duplicates++;
            }
            else if (existing.Matchable.TryGetValue(key, out var match))
            {
                id = match;
                matched++;
                seen[key] = id;
            }
            else
            {
                id = newId();
                created.Add(new NewChild(id, entry.Kind, entry.NativeName, false, existing.NextOrdinal + created.Count));
                seen[key] = id;
            }

            records.Add(new ChildRecord(i, id, entry.Kind, entry.NativeName, entry.Title));
        }

        var absent = existing.Matchable.Count - matched + existing.DuplicateCount;
        return new Assignment(created, records, new ChildCounts(entries.Count, created.Count, matched, absent, duplicates));
    }
}
