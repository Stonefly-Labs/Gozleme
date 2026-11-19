using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 T051 (FR-002, FR-003, FR-006, FR-011a, data-model.md): the database refuses, for the app login with a
/// publisher's verified context, every child row that would disagree with the stored projection or the assignment
/// rules: a child whose first revision is another capability's, a skipped ordinal, a second matchable child for one
/// key, a member-inserted duplicate, a record whose entry index, kind, name or title differs from the projection, a
/// child or a revision without its records, and a generation 1 that does not match the revision or a generation N > 1
/// from a member. Deferred checks run immediately (<c>SET CONSTRAINTS ALL IMMEDIATE</c>) inside rolled-back transactions.
/// </summary>
public sealed class ChildIntegrityTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;
    private string _capabilityId = null!;
    private string _revisionId = null!;
    private string _childId = null!;
    private string _otherRevisionId = null!;

    public async ValueTask InitializeAsync()
    {
        _s = await RevisionScenario.CreateAsync(fixture.Database);
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        _capabilityId = capabilityId;
        var revision = await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId), ChildClient.Mcp(canonical, "1.0.0", ("tool", "search", "Search"), ("prompt", "summarize", null)));
        _revisionId = revision.GetProperty("id").GetString()!;
        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, _revisionId));
        _childId = ChildClient.Ids(children)[0];
        (_, _, var other) = await _s.PublishedAsync();
        _otherRevisionId = other.GetProperty("id").GetString()!;
    }

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    /// <summary>The SQLSTATE of <paramref name="sql"/>; <paramref name="immediate"/> runs the deferred checks at once, which
    /// a multi-statement case that inserts a revision first cannot use (its own completeness check would fire).</summary>
    private Task<string> FailsAsync(string sql, bool immediate = true) =>
        RevisionScenario.FailsAsync(_s.Db.AppConnectionString, RevisionScenario.Context(_s.CarolId), (immediate ? "SET CONSTRAINTS ALL IMMEDIATE; " : "") + sql);

    private string Child(string kind, string name, int ordinal, string firstRevision, bool duplicate = false) => $"""
        INSERT INTO gozleme.capability_children (id, organization_id, capability_id, kind, native_name, is_duplicate, ordinal, first_revision_id, created_at)
        VALUES (gen_random_uuid(), '{_s.OrgA}', '{_capabilityId}', '{kind}', '{name}', {duplicate}, {ordinal}, '{firstRevision}', now())
        """;

    private string Record(int entryIndex, string childId, string kind, string name, string? title) => $"""
        INSERT INTO gozleme.revision_children (revision_id, organization_id, entry_index, child_id, kind, native_name, title)
        VALUES ('{_revisionId}', '{_s.OrgA}', {entryIndex}, '{childId}', '{kind}', '{name}', {(title is null ? "NULL" : $"'{title}'")})
        """;

    [Fact]
    public async Task A_child_whose_first_revision_is_another_capabilitys_is_refused() =>
        Assert.Equal("42501", await FailsAsync(Child("tool", "elsewhere", 3, _otherRevisionId)));

    [Fact]
    public async Task A_skipped_ordinal_is_refused() =>
        Assert.Equal("42501", await FailsAsync(Child("tool", "later", 7, _revisionId)));

    [Fact]
    public async Task A_second_matchable_child_for_one_key_is_refused() =>
        Assert.Equal("23505", await FailsAsync(Child("tool", "search", 3, _revisionId)));

    [Fact]
    public async Task A_member_cannot_insert_a_duplicate_child() =>
        Assert.Equal("42501", await FailsAsync(Child("tool", "search", 3, _revisionId, duplicate: true)));

    [Fact]
    public async Task A_child_without_a_revision_record_is_refused_at_commit() =>
        Assert.Equal("23000", await FailsAsync(Child("tool", "orphan", 3, _revisionId)));

    [Fact]
    public async Task A_record_that_disagrees_with_the_projection_is_refused()
    {
        Assert.Equal("23505", await FailsAsync(Record(0, _childId, "tool", "search", "Search"))); // entry already recorded

        // A fresh SQL revision whose projection declares one entry: the record's kind, name and title must match it.
        var (capabilityId, _) = await _s.NewCapabilityAsync();
        var revisionId = Guid.CreateVersion7();
        var childId = Guid.CreateVersion7();
        const string projection = """{"entries":[{"kind":"tool","nativeName":"x","title":"X"}],"projectionVersion":"1"}""";
        var revision = RevisionScenario.InsertRevisionSql(_s.OrgA, _s.PublisherId, capabilityId, _s.CarolId)
            .Replace("VALUES (gen_random_uuid(),", $"VALUES ('{revisionId}',", StringComparison.Ordinal)
            .Replace("convert_to('{}', 'UTF8'), '1', 'sha256:' || encode(sha256(convert_to('{}', 'UTF8')), 'hex'), 'mcp'",
                $"convert_to('{projection}', 'UTF8'), '1', 'sha256:' || encode(sha256(convert_to('{projection}', 'UTF8')), 'hex'), 'mcp'", StringComparison.Ordinal);
        Assert.Contains(projection, revision, StringComparison.Ordinal);
        var child = $"""
            {revision};
            INSERT INTO gozleme.capability_children (id, organization_id, capability_id, kind, native_name, is_duplicate, ordinal, first_revision_id, created_at)
            VALUES ('{childId}', '{_s.OrgA}', '{capabilityId}', 'tool', 'x', false, 1, '{revisionId}', now());
            """;
        foreach (var (index, kind, name, title) in new[] { (1, "tool", "x", "'X'"), (0, "prompt", "x", "'X'"), (0, "tool", "y", "'X'"), (0, "tool", "x", "'Other'"), (0, "tool", "x", "NULL") })
        {
            Assert.Equal("42501", await FailsAsync(
                $"{child} INSERT INTO gozleme.revision_children (revision_id, organization_id, entry_index, child_id, kind, native_name, title) " +
                $"VALUES ('{revisionId}', '{_s.OrgA}', {index}, '{childId}', '{kind}', '{name}', {title})", immediate: false));
        }

        // Another capability's child is refused even when the key matches.
        Assert.Equal("42501", await FailsAsync(
            $"{revision}; INSERT INTO gozleme.revision_children (revision_id, organization_id, entry_index, child_id, kind, native_name, title) " +
            $"VALUES ('{revisionId}', '{_s.OrgA}', 0, '{_childId}', 'tool', 'x', 'X')", immediate: false));
    }

    [Fact]
    public async Task A_revision_without_children_and_generation_1_is_refused_at_commit()
    {
        var (capabilityId, _) = await _s.NewCapabilityAsync();
        Assert.Equal("23000", await FailsAsync(RevisionScenario.InsertRevisionSql(_s.OrgA, _s.PublisherId, capabilityId, _s.CarolId)));
    }

    [Fact]
    public async Task Generation_1_must_match_the_revision_and_members_cannot_add_later_generations()
    {
        string Generation(int generation, string adapterVersion, string projection) => $"""
            INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
            VALUES (gen_random_uuid(), '{_s.OrgA}', '{_revisionId}', {generation}, 'mcp', '{adapterVersion}', '1', {projection}, {(projection == "NULL" ? "NULL" : $"'sha256:' || encode(sha256({projection}), 'hex')")}, now())
            """;
        Assert.Equal("42501", await FailsAsync(Generation(1, "1", "NULL"))); // generation 1 already exists: not max + 1
        Assert.Equal("42501", await FailsAsync(Generation(2, "1", "convert_to('{}', 'UTF8')")));
        Assert.Equal("42501", await FailsAsync(Generation(2, "1", "NULL"))); // a member's generation > 1, whatever it holds

        var (capabilityId, _) = await _s.NewCapabilityAsync();
        var revision = RevisionScenario.InsertRevisionSql(_s.OrgA, _s.PublisherId, capabilityId, _s.CarolId);
        Assert.Equal("42501", await FailsAsync($"""
            {revision};
            INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
            SELECT gen_random_uuid(), organization_id, id, 1, adapter_id, '2', projection_version, NULL, NULL, now() FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'
            """, immediate: false));
    }
}
