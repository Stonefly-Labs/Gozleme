using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 T051 (FR-002, FR-003, FR-006, FR-011a, data-model.md): the database refuses, for the app login with a
/// publisher's verified context, every child row that would disagree with the stored projection or the assignment
/// rules: a child whose first revision is another capability's, a skipped ordinal, a second matchable child for one
/// key, a member-inserted duplicate, a record whose entry index, kind, name or title differs from the projection, a
/// child or a revision without its records, a record whose non-duplicate child has another name, and a generation 1
/// that does not match the revision or a generation N > 1 from a member. Each case inserts against a revision Carol
/// publishes in the same transaction, so only the check under test refuses; maintainer ruling F2 adds that a member
/// inserts no child row for any other revision. Generation and child IDs are never found in each other's routes
/// (T078, FR-010). Deferred checks run immediately (<c>SET CONSTRAINTS</c>) inside rolled-back transactions.
/// </summary>
public sealed class ChildIntegrityTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;
    private string _capabilityId = null!;
    private string _revisionId = null!;
    private string _childId = null!;

    public async ValueTask InitializeAsync()
    {
        _s = await RevisionScenario.CreateAsync(fixture.Database);
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        _capabilityId = capabilityId;
        var revision = await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId), ChildClient.Mcp(canonical, "1.0.0", ("tool", "search", "Search"), ("prompt", "summarize", null)));
        _revisionId = revision.GetProperty("id").GetString()!;
        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, _revisionId));
        _childId = ChildClient.Ids(children)[0];
    }

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    /// <summary>The SQLSTATE of <paramref name="sql"/>; <paramref name="immediate"/> runs the deferred checks at once, which
    /// a case that inserts a revision first cannot use (its own completeness check would fire).</summary>
    private Task<string> FailsAsync(string sql, bool immediate = true) =>
        RevisionScenario.FailsAsync(_s.Db.AppConnectionString, RevisionScenario.Context(_s.CarolId), (immediate ? "SET CONSTRAINTS ALL IMMEDIATE; " : "") + sql);

    /// <summary>
    /// A revision Carol publishes in the case's own transaction (maintainer ruling F2: members insert child rows only
    /// for it), with id <paramref name="revisionId"/> and the projection <paramref name="projection"/>.
    /// </summary>
    private string Revision(Guid revisionId, string capabilityId, string projection = Projection, int sequence = 1)
    {
        var sql = RevisionScenario.InsertRevisionSql(_s.OrgA, _s.PublisherId, capabilityId, _s.CarolId, sequence, $"{sequence}.0.0")
            .Replace("VALUES (gen_random_uuid(),", $"VALUES ('{revisionId}',", StringComparison.Ordinal)
            .Replace("convert_to('{}', 'UTF8'), '1', 'sha256:' || encode(sha256(convert_to('{}', 'UTF8')), 'hex'), 'mcp'",
                $"convert_to('{projection}', 'UTF8'), '1', 'sha256:' || encode(sha256(convert_to('{projection}', 'UTF8')), 'hex'), 'mcp'", StringComparison.Ordinal);
        Assert.Contains(projection, sql, StringComparison.Ordinal);
        return sql + ";\n";
    }

    private const string Projection = """{"entries":[{"kind":"tool","nativeName":"x","title":"X"}],"projectionVersion":"1"}""";

    private string Child(string kind, string name, int ordinal, Guid firstRevision, bool duplicate = false, string? capabilityId = null, Guid? id = null) => $"""
        INSERT INTO gozleme.capability_children (id, organization_id, capability_id, kind, native_name, is_duplicate, ordinal, first_revision_id, created_at)
        VALUES ({(id is null ? "gen_random_uuid()" : $"'{id}'")}, '{_s.OrgA}', '{capabilityId ?? _capabilityId}', '{kind}', '{name}', {duplicate}, {ordinal}, '{firstRevision}', now());

        """;

    private string Record(Guid revisionId, int entryIndex, string childId, string kind, string name, string? title) => $"""
        INSERT INTO gozleme.revision_children (revision_id, organization_id, entry_index, child_id, kind, native_name, title)
        VALUES ('{revisionId}', '{_s.OrgA}', {entryIndex}, '{childId}', '{kind}', '{name}', {(title is null ? "NULL" : $"'{title}'")});

        """;

    private string Generation1(Guid revisionId) => $"""
        INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
        VALUES (gen_random_uuid(), '{_s.OrgA}', '{revisionId}', 1, 'mcp', '1', '1', NULL, NULL, now());

        """;

    /// <summary>A second revision of the capability, published in the case's transaction (the first is <c>_revisionId</c>).</summary>
    private string Second(Guid revisionId) => Revision(revisionId, _capabilityId, sequence: 2);

    [Fact]
    public async Task A_child_whose_first_revision_is_another_capabilitys_is_refused()
    {
        var (otherCapability, _) = await _s.NewCapabilityAsync();
        var revisionId = Guid.CreateVersion7();
        Assert.Equal("42501", await FailsAsync(Revision(revisionId, otherCapability) + Child("tool", "elsewhere", 3, revisionId), immediate: false));
    }

    [Fact]
    public async Task A_skipped_ordinal_is_refused()
    {
        var revisionId = Guid.CreateVersion7();
        Assert.Equal("42501", await FailsAsync(Second(revisionId) + Child("tool", "later", 7, revisionId), immediate: false));
    }

    [Fact]
    public async Task A_second_matchable_child_for_one_key_is_refused()
    {
        var revisionId = Guid.CreateVersion7();
        Assert.Equal("23505", await FailsAsync(Second(revisionId) + Child("tool", "search", 3, revisionId), immediate: false));
    }

    [Fact]
    public async Task A_member_cannot_insert_a_duplicate_child()
    {
        var revisionId = Guid.CreateVersion7();
        Assert.Equal("42501", await FailsAsync(Second(revisionId) + Child("tool", "search", 3, revisionId, duplicate: true), immediate: false));
    }

    [Fact]
    public async Task A_child_without_a_revision_record_is_refused_at_commit()
    {
        // Only the child's own completeness check runs at once; the revision's would fire too under ALL IMMEDIATE.
        var revisionId = Guid.CreateVersion7();
        Assert.Equal("23000", await FailsAsync(
            Second(revisionId) + Child("tool", "orphan", 3, revisionId) + "SET CONSTRAINTS gozleme.capability_children_recorded IMMEDIATE;", immediate: false));
    }

    [Fact]
    public async Task A_record_that_disagrees_with_the_projection_is_refused()
    {
        // A fresh revision whose projection declares one entry: the record's index, kind, name and title must match it.
        var (capabilityId, _) = await _s.NewCapabilityAsync();
        var revisionId = Guid.CreateVersion7();
        var childId = Guid.CreateVersion7();
        var child = Revision(revisionId, capabilityId) + Child("tool", "x", 1, revisionId, capabilityId: capabilityId, id: childId);
        foreach (var (index, kind, name, title) in new[] { (1, "tool", "x", "X"), (0, "prompt", "x", "X"), (0, "tool", "y", "X"), (0, "tool", "x", "Other"), (0, "tool", "x", null) })
        {
            Assert.Equal("42501", await FailsAsync(child + Record(revisionId, index, childId.ToString(), kind, name, title), immediate: false));
        }

        // The entry is recorded once.
        var record = Record(revisionId, 0, childId.ToString(), "tool", "x", "X");
        Assert.Equal("23505", await FailsAsync(child + record + record, immediate: false));

        // Another capability's child is refused even when the key matches.
        Assert.Equal("42501", await FailsAsync(Revision(revisionId, capabilityId) + Record(revisionId, 0, _childId, "tool", "x", "X"), immediate: false));
    }

    [Fact]
    public async Task A_record_that_matches_its_entry_but_reuses_a_child_of_another_name_is_refused()
    {
        // T078: the record equals the projection entry (tool "x"), but its non-duplicate child is the capability's "search".
        var revisionId = Guid.CreateVersion7();
        Assert.Equal("42501", await FailsAsync(Second(revisionId) + Record(revisionId, 0, _childId, "tool", "x", "X"), immediate: false));
    }

    [Fact]
    public async Task A_member_inserts_child_rows_only_for_the_revision_published_in_its_transaction()
    {
        // F2: a revision stored without children (as before the install step) and its child, committed out of band.
        var (capabilityId, _) = await _s.NewCapabilityAsync();
        var pending = Guid.CreateVersion7();
        var childId = Guid.CreateVersion7();
        await GozlemeDatabase.ExecuteAsync($"""
            SET session_replication_role = replica;
            {Revision(pending, capabilityId)}
            INSERT INTO gozleme.capability_children (id, organization_id, capability_id, kind, native_name, native_name_digest, is_duplicate, ordinal, first_revision_id, created_at)
            VALUES ('{childId}', '{_s.OrgA}', '{capabilityId}', 'tool', 'x', sha256(convert_to('x', 'UTF8')), false, 1, '{pending}', now());
            """, _s.Db.SuperuserConnectionString);

        // Carol holds the claim and the publisher role, and every row agrees with the guards and the projection: only
        // the policy refuses, because the revision was not published in this transaction.
        Assert.Equal("42501", await FailsAsync(Generation1(pending)));
        Assert.Equal("42501", await FailsAsync(Child("tool", "y", 2, pending, capabilityId: capabilityId)));
        Assert.Equal("42501", await FailsAsync(Record(pending, 0, childId.ToString(), "tool", "x", "X")));

        // The same for a revision that already has its children: no second record or generation 1 for it.
        Assert.Equal("42501", await FailsAsync(Record(Guid.Parse(_revisionId), 0, _childId, "tool", "search", "Search")));
    }

    [Fact]
    public async Task A_generation_id_is_never_a_child_id_and_a_child_id_never_a_generation_id()
    {
        // T078, FR-010: each ID is looked up only in its own table.
        var (_, generations) = await RevisionClient.GetAsync(_s.Erin, ChildClient.Projections(_s.OrgA, _s.PublisherId, _capabilityId, _revisionId));
        var generationId = ChildClient.Ids(generations, "id")[0];
        var (asChild, childBody) = await RevisionClient.GetAsync(_s.Erin, ChildClient.Child(_s.OrgA, _s.PublisherId, _capabilityId, generationId));
        var (asGeneration, generationBody) = await RevisionClient.GetAsync(_s.Erin, $"{ChildClient.Projections(_s.OrgA, _s.PublisherId, _capabilityId, _revisionId)}/{_childId}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, asChild);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, asGeneration);
        Assert.Equal(childBody.ToString(), generationBody.ToString());
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
        string Generation(Guid revisionId, int generation, string adapterVersion, string projection) => $"""
            INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
            VALUES (gen_random_uuid(), '{_s.OrgA}', '{revisionId}', {generation}, 'mcp', '{adapterVersion}', '1', {projection}, {(projection == "NULL" ? "NULL" : $"'sha256:' || encode(sha256({projection}), 'hex')")}, now())
            """;
        var revisionId = Guid.CreateVersion7();
        var first = Second(revisionId) + Generation1(revisionId);
        Assert.Equal("42501", await FailsAsync(first + Generation(revisionId, 1, "1", "NULL"), immediate: false)); // generation 1 already exists: not max + 1
        Assert.Equal("42501", await FailsAsync(first + Generation(revisionId, 2, "1", "convert_to('{}', 'UTF8')"), immediate: false));
        Assert.Equal("42501", await FailsAsync(first + Generation(revisionId, 2, "1", "NULL"), immediate: false)); // a member's generation > 1, whatever it holds
        Assert.Equal("42501", await FailsAsync(Second(revisionId) + Generation(revisionId, 1, "2", "NULL"), immediate: false)); // not the revision's adapter version
    }
}
