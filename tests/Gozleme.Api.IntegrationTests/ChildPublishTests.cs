using System.Net;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 through the API, each test on fresh capabilities so counts are exact. US1 (T025): a revision's entries are
/// children with distinct child IDs and kinds, listed by capability, child and revision, paged, with one child record
/// per entry (SC-002). US2 (T035, T036): continuity by exact (kind, native name) in publication sequence, renames and
/// drops shown absent, children of different capabilities distinct, the publish-time duplicate refusal while validate
/// accepts (FR-005a) with nothing stored, likewise U+0000 strings (T069, ruling F3), the 201 body unchanged (FR-021)
/// and a deterministic assignment (SC-003). US3 (T042): generation 1 is the stored, attested projection, and child,
/// generation and revision IDs are never interchangeable (FR-010).
/// </summary>
public sealed class ChildPublishTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    private static readonly (string, string, string?)[] FourEntries =
        [("tool", "search", "Search"), ("tool", "fetch", null), ("resource", "docs", "Docs"), ("prompt", "summarize", "Summarize")];

    private async Task<JsonElement> PublishAsync(string capabilityId, string canonical, string version, params (string, string, string?)[] entries) =>
        await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId), ChildClient.Mcp(canonical, version, entries));

    private async Task<JsonElement> GetOkAsync(string path)
    {
        var (status, body) = await RevisionClient.GetAsync(_s.Erin, path);
        Assert.True(status == HttpStatusCode.OK, $"{status} {path}");
        return body;
    }

    private Task<JsonElement> RevisionChildrenAsync(string capabilityId, JsonElement revision, string query = "") =>
        GetOkAsync(ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, revision.GetProperty("id").GetString()!) + query);

    private Task<JsonElement> ChildrenAsync(string capabilityId, string query = "") => GetOkAsync(ChildClient.Children(_s.OrgA, _s.PublisherId, capabilityId) + query);

    private Task<JsonElement> ChildAsync(string capabilityId, string childId, string query = "") => GetOkAsync(ChildClient.Child(_s.OrgA, _s.PublisherId, capabilityId, childId) + query);

    private Task<long> CountAsync(string sql) => _s.Db.ScalarAsync<long>(sql);

    [Fact]
    public async Task One_MCP_revision_declares_four_children_with_distinct_ids_and_kinds()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var revision = await PublishAsync(capabilityId, canonical, "1.0.0", FourEntries);

        var items = ChildClient.Items(await RevisionChildrenAsync(capabilityId, revision));
        Assert.Equal(["tool", "tool", "resource", "prompt"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal([0, 1, 2, 3], items.Select(i => i.GetProperty("entryIndex").GetInt32()));
        Assert.Equal(["search", "fetch", "docs", "summarize"], items.Select(i => i.GetProperty("nativeName").GetString()));
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("title").ValueKind);
        Assert.Equal(4, items.Select(i => i.GetProperty("childId").GetString()).Distinct().Count());
        foreach (var item in items)
        {
            Assert.Equal($"{canonical}/{item.GetProperty("kind").GetString()}/{item.GetProperty("childId").GetString()}", item.GetProperty("canonicalId").GetString());
            Assert.False(item.GetProperty("duplicate").GetBoolean());
        }

        var children = await ChildrenAsync(capabilityId);
        Assert.True(children.GetProperty("declaredNotVerified").GetBoolean());
        Assert.Equal(items.Select(i => i.GetProperty("childId").GetString()), ChildClient.Ids(children, "id"));
        Assert.Equal([1, 2, 3, 4], ChildClient.Items(children).Select(c => c.GetProperty("ordinal").GetInt32()));

        var child = await ChildAsync(capabilityId, items[0].GetProperty("childId").GetString()!);
        Assert.Equal(capabilityId, child.GetProperty("capability").GetProperty("id").GetString());
        Assert.Equal(canonical, child.GetProperty("capability").GetProperty("canonicalId").GetString());
        Assert.Equal(revision.GetProperty("id").GetString(), child.GetProperty("child").GetProperty("firstRevisionId").GetString());
        var history = child.GetProperty("history").EnumerateArray().Single();
        Assert.True(history.GetProperty("present").GetBoolean());
        Assert.Equal(("search", "Search", 1, "1.0.0"), (history.GetProperty("nativeName").GetString(), history.GetProperty("title").GetString(),
            history.GetProperty("sequence").GetInt32(), history.GetProperty("version").GetString()));

        // SC-002: exactly one child record per entry.
        Assert.Equal(4, await CountAsync($"SELECT count(*) FROM gozleme.revision_children WHERE revision_id = '{revision.GetProperty("id").GetString()}'"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gozleme.projection_generations WHERE revision_id = '{revision.GetProperty("id").GetString()}' AND generation = 1"));
    }

    [Theory]
    [InlineData("valid-skill")]
    [InlineData("valid-model-card")]
    public async Task Skill_and_model_capabilities_have_no_children(string sample)
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync(sample: sample);
        Assert.Empty(ChildClient.Items(await ChildrenAsync(capabilityId)));
        Assert.Empty(ChildClient.Items(await RevisionChildrenAsync(capabilityId, revision)));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gozleme.projection_generations WHERE revision_id = '{revision.GetProperty("id").GetString()}'"));
    }

    [Fact]
    public async Task Children_history_and_revision_children_are_paged()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var entries = Enumerable.Range(0, 5).Select(i => ("tool", $"t{i}", (string?)null)).ToArray();
        var revision = await PublishAsync(capabilityId, canonical, "1.0.0", entries);
        await PublishAsync(capabilityId, canonical, "2.0.0", entries);
        await PublishAsync(capabilityId, canonical, "3.0.0", entries);

        var page = await ChildrenAsync(capabilityId, "?limit=2");
        Assert.Equal([1, 2], ChildClient.Items(page).Select(c => c.GetProperty("ordinal").GetInt32()));
        Assert.Equal(2, page.GetProperty("next").GetInt32());
        page = await ChildrenAsync(capabilityId, "?limit=2&after=4");
        Assert.Equal([5], ChildClient.Items(page).Select(c => c.GetProperty("ordinal").GetInt32()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next").ValueKind);
        Assert.Equal(5, ChildClient.Items(await ChildrenAsync(capabilityId, "?limit=1000")).Length);
        Assert.Equal(3, ChildClient.Items(await ChildrenAsync(capabilityId)).First().GetProperty("presentIn").GetInt32());

        var rows = await RevisionChildrenAsync(capabilityId, revision, "?limit=2&after=1");
        Assert.Equal([2, 3], ChildClient.Items(rows).Select(r => r.GetProperty("entryIndex").GetInt32()));
        Assert.Equal(3, rows.GetProperty("next").GetInt32());

        var childId = ChildClient.Ids(page, "id")[0];
        var history = await ChildAsync(capabilityId, childId, "?limit=2");
        Assert.Equal([1, 2], history.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("sequence").GetInt32()));
        Assert.Equal(2, history.GetProperty("next").GetInt32());
        history = await ChildAsync(capabilityId, childId, "?after=2");
        Assert.Equal([3], history.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("sequence").GetInt32()));
        Assert.Equal(JsonValueKind.Null, history.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task Kept_and_retitled_entries_keep_their_ids_new_ones_get_new_ids_and_dropped_ones_are_absent()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var first = await PublishAsync(capabilityId, canonical, "1.0.0", FourEntries);
        var before = ChildClient.Items(await RevisionChildrenAsync(capabilityId, first)).ToDictionary(i => i.GetProperty("nativeName").GetString()!, i => i.GetProperty("childId").GetString()!);

        // Reordered: fetch first; search retitled; the prompt dropped; a new tool added.
        var second = await PublishAsync(capabilityId, canonical, "1.1.0", ("tool", "fetch", null), ("tool", "search", "Search (v2)"), ("resource", "docs", "Docs"), ("tool", "lookup", null));
        // MCP projections list tools, then resources, then prompts.
        var after = ChildClient.Items(await RevisionChildrenAsync(capabilityId, second)).ToDictionary(i => i.GetProperty("nativeName").GetString()!);
        Assert.Equal(["fetch", "search", "lookup", "docs"], ChildClient.Items(await RevisionChildrenAsync(capabilityId, second)).Select(i => i.GetProperty("nativeName").GetString()));
        Assert.Equal(before["fetch"], after["fetch"].GetProperty("childId").GetString());
        Assert.Equal(before["search"], after["search"].GetProperty("childId").GetString());
        Assert.Equal("Search (v2)", after["search"].GetProperty("title").GetString());
        Assert.Equal(before["docs"], after["docs"].GetProperty("childId").GetString());
        Assert.DoesNotContain(after["lookup"].GetProperty("childId").GetString(), before.Values);

        var prompt = await ChildAsync(capabilityId, before["summarize"]);
        Assert.Equal([true, false], prompt.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("present").GetBoolean()));
        Assert.Equal(1, prompt.GetProperty("child").GetProperty("latest").GetProperty("sequence").GetInt32());

        // Revision 1 still lists exactly its four children (FR-008).
        Assert.Equal(before.Values.Order(), ChildClient.Ids(await RevisionChildrenAsync(capabilityId, first)).Order());
        Assert.Equal(5, ChildClient.Items(await ChildrenAsync(capabilityId)).Length);
    }

    [Fact]
    public async Task A_rename_is_a_new_child_and_the_old_child_is_absent_from_the_new_revision()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var first = await PublishAsync(capabilityId, canonical, "1.0.0", ("tool", "search", null));
        var second = await PublishAsync(capabilityId, canonical, "2.0.0", ("tool", "search_v2", null));
        var (old, renamed) = (ChildClient.Ids(await RevisionChildrenAsync(capabilityId, first))[0], ChildClient.Ids(await RevisionChildrenAsync(capabilityId, second))[0]);
        Assert.NotEqual(old, renamed);
        Assert.Equal([true, false], (await ChildAsync(capabilityId, old)).GetProperty("history").EnumerateArray().Select(h => h.GetProperty("present").GetBoolean()));
        Assert.Equal([false, true], (await ChildAsync(capabilityId, renamed)).GetProperty("history").EnumerateArray().Select(h => h.GetProperty("present").GetBoolean()));
    }

    [Fact]
    public async Task A_lower_version_published_later_matches_by_sequence_and_earlier_children_never_change()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var first = await PublishAsync(capabilityId, canonical, "2.0.0", ("tool", "a", null), ("tool", "b", null));
        var firstIds = ChildClient.Ids(await RevisionChildrenAsync(capabilityId, first));
        var second = await PublishAsync(capabilityId, canonical, "1.5.0", ("tool", "b", null), ("tool", "c", null));
        Assert.Equal(2, second.GetProperty("sequence").GetInt32());
        var secondIds = ChildClient.Ids(await RevisionChildrenAsync(capabilityId, second));
        Assert.Equal(firstIds[1], secondIds[0]);
        Assert.Equal(firstIds, ChildClient.Ids(await RevisionChildrenAsync(capabilityId, first)));
    }

    [Fact]
    public async Task The_same_key_in_two_capabilities_is_two_different_children()
    {
        var (one, oneCanonical) = await _s.NewCapabilityAsync();
        var (two, twoCanonical) = await _s.NewCapabilityAsync();
        var a = ChildClient.Ids(await RevisionChildrenAsync(one, await PublishAsync(one, oneCanonical, "1.0.0", ("tool", "search", null))));
        var b = ChildClient.Ids(await RevisionChildrenAsync(two, await PublishAsync(two, twoCanonical, "1.0.0", ("tool", "search", null))));
        Assert.NotEqual(a[0], b[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await RevisionClient.GetAsync(_s.Erin, ChildClient.Child(_s.OrgA, _s.PublisherId, two, a[0]))).Status);
    }

    [Fact]
    public async Task Duplicate_entries_are_accepted_by_validate_and_refused_by_publish_with_nothing_stored()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var body = ChildClient.A2aWithDuplicateSkill(canonical, "1.0.0");
        var (validateStatus, validated) = await ManifestClient.SendAsync(_s.Carol, ManifestClient.Validate(_s.OrgA, _s.PublisherId, capabilityId), body);
        Assert.Equal(HttpStatusCode.OK, validateStatus);
        Assert.Equal("accepted", ManifestClient.Json(validated).GetProperty("outcome").GetString());
        var entries = ManifestClient.Json(validated).GetProperty("projection").GetProperty("entries").GetArrayLength();

        long Attempts() => _s.Db.ScalarAsync<long>($"SELECT coalesce(sum(attempts), 0) FROM gozleme.manifest_publication_attempts WHERE account_id = '{_s.CarolId}'").GetAwaiter().GetResult();
        var attempts = Attempts();
        var (status, text) = await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var problems = ManifestClient.Json(text).GetProperty("problems").EnumerateArray().ToArray();
        Assert.Equal(["/entries/0", $"/entries/{entries - 1}"], problems.Select(p => p.GetProperty("location").GetProperty("pointer").GetString()));
        Assert.All(problems, p => Assert.Equal(("invalid_value", "publish.duplicate_entry", "projection"),
            (p.GetProperty("category").GetString(), p.GetProperty("rule").GetString(), p.GetProperty("location").GetProperty("part").GetString())));
        Assert.Equal("rejected", ManifestClient.Json(text).GetProperty("outcome").GetString());
        Assert.Equal(attempts + 1, Attempts());

        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.capability_children WHERE capability_id = '{capabilityId}'"));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.projection_generations g JOIN gozleme.manifest_revisions r ON r.id = g.revision_id WHERE r.capability_id = '{capabilityId}'"));
    }

    [Fact]
    public async Task U0000_strings_are_accepted_by_validate_and_refused_by_publish_at_their_pointers_with_nothing_stored()
    {
        // T069 (maintainer ruling F3): PostgreSQL text and jsonb cannot hold U+0000; without the rule this answered 500.
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(ChildClient.Mcp(canonical, "1.0.0", ("tool", "ok", null), ("tool", "a\0b", "T\0")))!.AsObject();
        var document = System.Text.Json.Nodes.JsonNode.Parse(envelope["native"]!["document"]!.GetValue<string>())!.AsObject();
        document["serverInfo"]!["description"] = "D\0";
        envelope["native"]!["document"] = document.ToJsonString();
        var body = System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString());
        Assert.Contains("\\u0000", envelope.ToJsonString(), StringComparison.Ordinal);

        var (validateStatus, validated) = await ManifestClient.SendAsync(_s.Carol, ManifestClient.Validate(_s.OrgA, _s.PublisherId, capabilityId), body);
        Assert.Equal((HttpStatusCode.OK, "accepted"), (validateStatus, ManifestClient.Json(validated).GetProperty("outcome").GetString()));

        var (status, text) = await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var problems = ManifestClient.Json(text).GetProperty("problems").EnumerateArray().ToArray();
        Assert.Equal(["/description", "/entries/1/nativeName", "/entries/1/title"], problems.Select(p => p.GetProperty("location").GetProperty("pointer").GetString()));
        Assert.All(problems, p => Assert.Equal(("invalid_value", "publish.nul_character", "projection"),
            (p.GetProperty("category").GetString(), p.GetProperty("rule").GetString(), p.GetProperty("location").GetProperty("part").GetString())));
        Assert.DoesNotContain("\\u0000", text, StringComparison.Ordinal);

        // Together with a duplicate (an A2A card whose repeated skill's name holds U+0000): duplicates first, then U+0000 strings.
        var card = System.Text.Json.Nodes.JsonNode.Parse(ChildClient.A2aWithDuplicateSkill(canonical, "1.0.0"))!.AsObject();
        var native = System.Text.Json.Nodes.JsonNode.Parse(card["native"]!["document"]!.GetValue<string>())!.AsObject();
        native["skills"]![native["skills"]!.AsArray().Count - 1]!["name"] = "Repeated\0";
        card["native"]!["document"] = native.ToJsonString();
        var (bothStatus, both) = await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId), System.Text.Encoding.UTF8.GetBytes(card.ToJsonString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bothStatus);
        Assert.Equal(["publish.duplicate_entry", "publish.duplicate_entry", "publish.nul_character"],
            ManifestClient.Json(both).GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("rule").GetString()));

        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.capability_children WHERE capability_id = '{capabilityId}'"));
    }

    [Fact]
    public async Task Envelope_U0000_strings_are_accepted_by_validate_and_refused_by_publish_after_projection_problems_with_nothing_stored()
    {
        // T082 (maintainer ruling envelope-nul): envelope title and summary are stored in envelope_summary jsonb; this answered 503.
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(ChildClient.Mcp(canonical, "1.0.0", ("tool", "ok", null), ("tool", "a\0b", null)))!.AsObject();
        envelope["title"] = "Env\0title";
        envelope["summary"] = "Env\0summary";
        var body = System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString());

        var (validateStatus, validated) = await ManifestClient.SendAsync(_s.Carol, ManifestClient.Validate(_s.OrgA, _s.PublisherId, capabilityId), body);
        Assert.Equal((HttpStatusCode.OK, "accepted"), (validateStatus, ManifestClient.Json(validated).GetProperty("outcome").GetString()));

        var (status, text) = await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var problems = ManifestClient.Json(text).GetProperty("problems").EnumerateArray().ToArray();
        Assert.Equal(["projection /entries/1/nativeName", "envelope /title", "envelope /summary"],
            problems.Select(p => $"{p.GetProperty("location").GetProperty("part").GetString()} {p.GetProperty("location").GetProperty("pointer").GetString()}"));
        Assert.All(problems, p => Assert.Equal("publish.nul_character", p.GetProperty("rule").GetString()));
        Assert.DoesNotContain("\\u0000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Env", text, StringComparison.Ordinal);

        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.capability_children WHERE capability_id = '{capabilityId}'"));
    }

    [Fact]
    public async Task The_publication_response_body_is_unchanged()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var revision = await PublishAsync(capabilityId, canonical, "1.0.0", FourEntries);
        Assert.Equal(
            ["adapter", "attestation", "capabilityId", "envelope", "id", "native", "nativeDigest", "projection", "projectionDigest", "publishedAt", "publishedBy",
             "publisherId", "publisherSuspended", "ruleCatalogueVersion", "schemaBundleVersion", "sequence", "verification", "version"],
            revision.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_same_sequence_in_two_fresh_capabilities_gives_the_same_ordinal_mapping()
    {
        (string, string, string?)[][] sequence =
        [
            [("tool", "a", null), ("tool", "b", null), ("prompt", "p", null)],
            [("tool", "b", "B"), ("tool", "c", null)],
            [("prompt", "p", null), ("tool", "a", null), ("resource", "r", null)],
        ];

        async Task<string> MappingAsync()
        {
            var (capabilityId, canonical) = await _s.NewCapabilityAsync();
            for (var i = 0; i < sequence.Length; i++)
            {
                await PublishAsync(capabilityId, canonical, $"{i + 1}.0.0", sequence[i]);
            }

            return await _s.Db.ScalarAsync<string>($"""
                SELECT string_agg(r.sequence || ':' || rc.entry_index || '>' || c.ordinal, ',' ORDER BY r.sequence, rc.entry_index)
                FROM gozleme.revision_children rc JOIN gozleme.manifest_revisions r ON r.id = rc.revision_id JOIN gozleme.capability_children c ON c.id = rc.child_id
                WHERE r.capability_id = '{capabilityId}'
                """);
        }

        var once = await MappingAsync();
        // Revision 3's projection lists the tool, then the resource, then the prompt.
        Assert.Equal("1:0>1,1:1>2,1:2>3,2:0>2,2:1>4,3:0>1,3:1>5,3:2>3", once);
        Assert.Equal(once, await MappingAsync());
    }

    [Fact]
    public async Task Generation_1_is_the_stored_attested_projection()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var revision = await PublishAsync(capabilityId, canonical, "1.0.0", FourEntries);
        var revisionId = revision.GetProperty("id").GetString()!;
        var list = await GetOkAsync(ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId));
        var generation = ChildClient.Items(list).Single();
        Assert.Equal(1, generation.GetProperty("generation").GetInt32());
        Assert.True(generation.GetProperty("attested").GetBoolean());
        Assert.False(generation.TryGetProperty("comparedToPrevious", out _));
        Assert.Equal(revision.GetProperty("projectionDigest").GetString(), generation.GetProperty("digest").GetString());
        Assert.Equal(("mcp", "1", "1"), (generation.GetProperty("adapter").GetProperty("id").GetString(), generation.GetProperty("adapter").GetProperty("version").GetString(),
            generation.GetProperty("projectionVersion").GetString()));

        var generationId = generation.GetProperty("id").GetString()!;
        using var download = await _s.Erin.GetAsync(new Uri($"{ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId)}/{generationId}", UriKind.Relative), TestContext.Current.CancellationToken);
        using var stored = await _s.Erin.GetAsync(new Uri(_s.Revision(capabilityId, revisionId) + "/projection", UriKind.Relative), TestContext.Current.CancellationToken);
        var bytes = await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(await stored.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken), bytes);
        Assert.Equal(generation.GetProperty("byteLength").GetInt32(), bytes.Length);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Child_generation_and_revision_ids_are_never_interchangeable()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        var revision = await PublishAsync(capabilityId, canonical, "1.0.0", FourEntries);
        var revisionId = revision.GetProperty("id").GetString()!;
        var childId = ChildClient.Ids(await RevisionChildrenAsync(capabilityId, revision))[0];
        var generationId = ChildClient.Ids(await GetOkAsync(ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId)), "id")[0];

        foreach (var path in new[]
        {
            ChildClient.Child(_s.OrgA, _s.PublisherId, capabilityId, generationId),
            ChildClient.Child(_s.OrgA, _s.PublisherId, capabilityId, revisionId),
            $"{ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId)}/{childId}",
            $"{ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId)}/{revisionId}",
            ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, childId),
            ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, generationId),
        })
        {
            using var response = await _s.Erin.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
    }
}
