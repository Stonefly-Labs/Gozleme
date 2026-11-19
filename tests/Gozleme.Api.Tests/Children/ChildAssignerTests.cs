using Gozleme.Api.Children;
using Gozleme.Api.Manifests.Projection;

namespace Gozleme.Api.Tests.Children;

/// <summary>
/// GOZ-0008 T013 (maintainer Q2, clarify Q1, research R2, SC-003): matching by exact (<c>kind</c>, <c>nativeName</c>)
/// in position order; title, description and position never match; a rename or a kind change is a new child; a
/// duplicate child is never matched; install mode records further duplicates; counts and ordinals are deterministic.
/// </summary>
public sealed class ChildAssignerTests
{
    private static ProjectionEntry E(string kind, string name, string? title = null) => new(kind, name, title);

    /// <summary>Assigns a sequence of projections to one capability, as publication or the install step does.</summary>
    private static List<Assignment> Sequence(AssignMode mode, params ProjectionEntry[][] revisions)
    {
        var existing = ExistingChildren.None;
        var result = new List<Assignment>();
        foreach (var entries in revisions)
        {
            var assignment = ChildAssigner.Assign(existing, entries, mode);
            result.Add(assignment);
            existing = existing.Then(assignment);
        }

        return result;
    }

    [Fact]
    public void Every_entry_of_a_first_revision_creates_a_child_with_the_next_ordinals()
    {
        var a = ChildAssigner.Assign(ExistingChildren.None, [E("tool", "search"), E("tool", "fetch"), E("resource", "docs"), E("prompt", "summarize")], AssignMode.Publish);
        Assert.Equal([1, 2, 3, 4], a.Created.Select(c => c.Ordinal));
        Assert.Equal(4, a.Created.Select(c => c.Id).Distinct().Count());
        Assert.Equal(a.Created.Select(c => c.Id), a.Records.Select(r => r.ChildId));
        Assert.Equal([0, 1, 2, 3], a.Records.Select(r => r.EntryIndex));
        Assert.Equal(new ChildCounts(4, 4, 0, 0, 0), a.Counts);
        Assert.All(a.Created, c => Assert.False(c.IsDuplicate));
    }

    [Fact]
    public void Kept_retitled_and_reordered_entries_keep_their_child_and_new_entries_get_new_ones()
    {
        var (first, second) = (Sequence(AssignMode.Publish, [E("tool", "search", "Search"), E("tool", "fetch"), E("prompt", "summarize")],
            [E("tool", "lookup"), E("tool", "fetch", "Fetch (new title)"), E("tool", "search", "Search")]) is [var x, var y] ? (x, y) : default);
        var ids = first.Records.ToDictionary(r => r.NativeName, r => r.ChildId);
        Assert.Equal(ids["fetch"], second.Records[1].ChildId);
        Assert.Equal(ids["search"], second.Records[2].ChildId);
        Assert.DoesNotContain(second.Records[0].ChildId, ids.Values);
        Assert.Equal("Fetch (new title)", second.Records[1].Title);
        Assert.Equal([4], second.Created.Select(c => c.Ordinal));
        Assert.Equal(new ChildCounts(3, 1, 2, 1, 0), second.Counts); // the prompt is absent
    }

    [Fact]
    public void A_rename_is_a_new_child_and_the_old_child_is_absent()
    {
        var revisions = Sequence(AssignMode.Publish, [E("tool", "search")], [E("tool", "search_v2")]);
        Assert.NotEqual(revisions[0].Records[0].ChildId, revisions[1].Records[0].ChildId);
        Assert.Equal(new ChildCounts(1, 1, 0, 1, 0), revisions[1].Counts);
    }

    [Theory]
    [InlineData("Search")] // case
    [InlineData("search ")] // whitespace
    [InlineData("café")] // precomposed vs decomposed below
    public void Keys_compare_by_exact_bytes_without_case_folding_or_normalization(string variant)
    {
        var original = variant == "café" ? "café" : "search";
        var revisions = Sequence(AssignMode.Publish, [E("tool", original)], [E("tool", variant)]);
        Assert.NotEqual(revisions[0].Records[0].ChildId, revisions[1].Records[0].ChildId);
    }

    [Fact]
    public void A_kind_change_is_a_different_child()
    {
        var revisions = Sequence(AssignMode.Publish, [E("tool", "docs")], [E("resource", "docs")]);
        Assert.NotEqual(revisions[0].Records[0].ChildId, revisions[1].Records[0].ChildId);
        Assert.Equal("resource", revisions[1].Created.Single().Kind);
    }

    [Fact]
    public void Publication_never_assigns_a_projection_with_duplicate_keys()
    {
        Assert.Throws<InvalidOperationException>(() => ChildAssigner.Assign(ExistingChildren.None, [E("skill", "s"), E("skill", "s")], AssignMode.Publish));
    }

    [Fact]
    public void Install_mode_gives_the_key_to_the_first_duplicate_and_new_duplicate_children_to_the_others()
    {
        var revisions = Sequence(AssignMode.Install, [E("skill", "s", "first"), E("skill", "t"), E("skill", "s", "second"), E("skill", "s", "third")],
            [E("skill", "s"), E("skill", "t")]);
        var first = revisions[0];
        Assert.Equal([false, false, true, true], first.Created.Select(c => c.IsDuplicate));
        Assert.Equal([1, 2, 3, 4], first.Created.Select(c => c.Ordinal));
        Assert.Equal(new ChildCounts(4, 4, 0, 0, 2), first.Counts);

        // The later revision matches the key's child (the first entry), never a duplicate; both duplicates are absent.
        var second = revisions[1];
        Assert.Equal(first.Records[0].ChildId, second.Records[0].ChildId);
        Assert.Equal(first.Records[1].ChildId, second.Records[1].ChildId);
        Assert.Empty(second.Created);
        Assert.Equal(new ChildCounts(2, 0, 2, 2, 0), second.Counts);
    }

    [Fact]
    public void Ordinals_continue_from_the_next_ordinal()
    {
        var existing = new ExistingChildren(new Dictionary<ChildKey, Guid> { [new("tool", "a")] = Guid.CreateVersion7() }, 1, 3);
        var a = ChildAssigner.Assign(existing, [E("tool", "a"), E("tool", "b"), E("tool", "c")], AssignMode.Publish);
        Assert.Equal([3, 4], a.Created.Select(c => c.Ordinal));
        Assert.Equal(new ChildCounts(3, 2, 1, 1, 0), a.Counts); // the one earlier duplicate is absent
    }

    [Fact]
    public void A_fixed_sequence_gives_the_same_sequence_and_entry_to_ordinal_mapping_every_time()
    {
        ProjectionEntry[][] revisions =
        [
            [E("tool", "a"), E("tool", "b"), E("prompt", "p")],
            [E("tool", "b"), E("tool", "c")],
            [E("prompt", "p"), E("tool", "a"), E("tool", "d")],
        ];

        static List<(int Sequence, int Entry, int Ordinal)> Mapping(List<Assignment> assignments)
        {
            var ordinals = new Dictionary<Guid, int>();
            foreach (var child in assignments.SelectMany(a => a.Created))
            {
                ordinals[child.Id] = child.Ordinal;
            }

            return [.. assignments.SelectMany((a, s) => a.Records.Select(r => (s + 1, r.EntryIndex, ordinals[r.ChildId])))];
        }

        var once = Mapping(Sequence(AssignMode.Publish, revisions));
        Assert.Equal(once, Mapping(Sequence(AssignMode.Publish, revisions)));
        Assert.Equal([(1, 0, 1), (1, 1, 2), (1, 2, 3), (2, 0, 2), (2, 1, 4), (3, 0, 3), (3, 1, 1), (3, 2, 5)], once);
    }
}
