using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gozleme.Api.Children;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 T041 (FR-011, FR-011a, maintainer Q3 option B, clarify Q2, contracts/projection-generations.md): the operator
/// command <c>projection-regenerate</c> records unattested generation N + 1 that keeps every child ID, even when
/// byte-identical; generation 1 and the attestation never change; exit codes 0/1/2/3/64 with fixed words, among them
/// <c>projection_failed</c>, <c>not_installed</c> (T077, maintainer ruling F14) and exactly <c>failed</c> for exit 1; the
/// app login cannot use it; a suspended publisher's revision can be re-projected; <c>comparedToPrevious</c> reports
/// <c>identical</c>, <c>different</c> and <c>not_comparable</c> (T074); output and audit carry opaque IDs only.
/// </summary>
public sealed partial class ProjectionRegenerateTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _logs = new();
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    [GeneratedRegex("^(?<r>[0-9a-f-]{36}) generation (?<n>[0-9]+) (?<g>[0-9a-f-]{36}) digest equals generation (?<p>[0-9]+): (?<eq>yes|no|not_comparable)$")]
    private static partial Regex OutputLine();

    private async Task<(string CapabilityId, string RevisionId, string[] ChildIds, string PublisherId)> PublishedAsync(string? publisherId = null)
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync(publisherId);
        var revision = await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId, publisherId),
            ChildClient.Mcp(canonical, "1.0.0", ("tool", "search", "Search"), ("tool", "fetch", null), ("prompt", "summarize", null)));
        var revisionId = revision.GetProperty("id").GetString()!;
        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, publisherId ?? _s.PublisherId, capabilityId, revisionId));
        return (capabilityId, revisionId, ChildClient.Ids(children), publisherId ?? _s.PublisherId);
    }

    private async Task<JsonElement[]> GenerationsAsync(string capabilityId, string revisionId, string? publisherId = null) =>
        ChildClient.Items((await RevisionClient.GetAsync(_s.Erin, ChildClient.Projections(_s.OrgA, publisherId ?? _s.PublisherId, capabilityId, revisionId))).Body);

    private Task<long> GenerationCountAsync(string revisionId) =>
        _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.projection_generations WHERE revision_id = '{revisionId}'");

    private Task TamperAsync(string sql) => GozlemeDatabase.ExecuteAsync("SET session_replication_role = replica; " + sql, _s.Db.SuperuserConnectionString);

    [Fact]
    public async Task Generation_2_keeps_every_child_and_a_third_run_records_generation_3()
    {
        var (capabilityId, revisionId, childIds, _) = await PublishedAsync();
        var (_, before) = await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, revisionId));

        var (code, stdout, stderr) = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.True(code == ProjectionOperatorCommands.Ok, stderr);
        var line = OutputLine().Match(stdout.TrimEnd());
        Assert.True(line.Success, stdout);
        Assert.Equal((revisionId, "2", "1", "yes"), (line.Groups["r"].Value, line.Groups["n"].Value, line.Groups["p"].Value, line.Groups["eq"].Value));

        var generations = await GenerationsAsync(capabilityId, revisionId);
        Assert.Equal([1, 2], generations.Select(g => g.GetProperty("generation").GetInt32()));
        Assert.Equal([true, false], generations.Select(g => g.GetProperty("attested").GetBoolean()));
        Assert.Equal(line.Groups["g"].Value, generations[1].GetProperty("id").GetString());
        Assert.Equal("identical", generations[1].GetProperty("comparedToPrevious").GetString());
        Assert.Equal(generations[0].GetProperty("digest").GetString(), generations[1].GetProperty("digest").GetString());

        // Every child keeps its ID; the revision, its generation 1 and its attestation are unchanged and still verify.
        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, revisionId));
        Assert.Equal(childIds, ChildClient.Ids(children));
        var (_, after) = await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, revisionId));
        Assert.Equal("verified", after.GetProperty("verification").GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("projectionDigest").GetString(), after.GetProperty("projectionDigest").GetString());
        Assert.Equal(before.GetProperty("attestation").GetRawText(), after.GetProperty("attestation").GetRawText());
        Assert.Equal(1, await _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.projection_generations WHERE revision_id = '{revisionId}' AND generation = 1 AND projection IS NULL"));

        // Byte-identical re-projection is still recorded (clarify Q2).
        var third = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal(ProjectionOperatorCommands.Ok, third.Code);
        Assert.Matches(" generation 3 .* digest equals generation 2: yes$", third.Stdout.TrimEnd());
        Assert.Equal(3, await GenerationCountAsync(revisionId));

        // Audit 1601 and the output carry opaque IDs only.
        Assert.Contains(_logs, l => l.Contains("Operator recorded projection generation 2", StringComparison.Ordinal) && l.Contains(revisionId, StringComparison.Ordinal));
        foreach (var text in _logs.Append(stdout).Append(third.Stdout))
        {
            foreach (var secret in new[] { "search", "fetch", "summarize", "Search", generations[0].GetProperty("digest").GetString()![7..] })
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task An_unknown_revision_exits_2_and_usage_exits_64()
    {
        var unknown = await _s.RegenerateAsync(_logs, null, "--revision", Guid.CreateVersion7().ToString());
        Assert.Equal((ProjectionOperatorCommands.UnknownTarget, "unknown_target"), (unknown.Code, unknown.Stderr.Trim()));
        Assert.Contains(_logs, l => l.Contains("Operator projection generation refused (unknown_target)", StringComparison.Ordinal));

        foreach (var args in new[] { Array.Empty<string>(), ["--revision"], ["--revision", "not-a-uuid"], ["--revision", Guid.CreateVersion7().ToString(), "extra"], ["--publisher", Guid.CreateVersion7().ToString()] })
        {
            Assert.Equal(ProjectionOperatorCommands.Usage, (await _s.RegenerateAsync(null, null, args)).Code);
        }
    }

    [Fact]
    public async Task Tampered_native_bytes_are_refused_with_nothing_recorded()
    {
        var (_, revisionId, _, _) = await PublishedAsync();
        await TamperAsync($"UPDATE gozleme.manifest_revisions SET native_document = native_document || convert_to(' ', 'UTF8'), native_byte_length = native_byte_length + 1 WHERE id = '{revisionId}';");
        var result = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal((ProjectionOperatorCommands.Refused, RegenerateRefusal.NativeDigestMismatch), (result.Code, result.Stderr.Trim()));
        Assert.Equal(1, await GenerationCountAsync(revisionId));
        Assert.Contains(_logs, l => l.Contains("Operator projection generation refused (native_digest_mismatch)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_changed_entry_multiset_is_refused_with_nothing_recorded()
    {
        var (_, revisionId, _, _) = await PublishedAsync();
        // The stored projection now names another entry, so the re-projection no longer declares the same children.
        await TamperAsync($"UPDATE gozleme.manifest_revisions SET projection = convert_to(replace(convert_from(projection, 'UTF8'), '\"fetch\"', '\"fetch_v2\"'), 'UTF8') WHERE id = '{revisionId}';");
        var result = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal((ProjectionOperatorCommands.Refused, RegenerateRefusal.EntriesChanged), (result.Code, result.Stderr.Trim()));
        Assert.Equal(1, await GenerationCountAsync(revisionId));
    }

    [Fact]
    public async Task An_unregistered_adapter_is_refused()
    {
        var (_, revisionId, _, _) = await PublishedAsync();
        await TamperAsync($"UPDATE gozleme.manifest_revisions SET adapter_version = '9' WHERE id = '{revisionId}';");
        var result = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal((ProjectionOperatorCommands.Refused, RegenerateRefusal.AdapterUnavailable), (result.Code, result.Stderr.Trim()));
        Assert.Equal(1, await GenerationCountAsync(revisionId));
    }

    [Fact]
    public async Task Unparseable_stored_native_bytes_with_a_matching_digest_are_refused_as_projection_failed()
    {
        var (_, revisionId, _, _) = await PublishedAsync();
        await TamperAsync($"UPDATE gozleme.manifest_revisions SET native_document = convert_to('{{not json', 'UTF8'), native_digest = 'sha256:' || encode(sha256(convert_to('{{not json', 'UTF8')), 'hex'), native_byte_length = 9 WHERE id = '{revisionId}';");
        var result = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal((ProjectionOperatorCommands.Refused, RegenerateRefusal.ProjectionFailed), (result.Code, result.Stderr.Trim()));
        Assert.Equal(1, await GenerationCountAsync(revisionId));
        Assert.Contains(_logs, l => l.Contains("Operator projection generation refused (projection_failed)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_known_revision_without_generation_1_exits_3_not_installed()
    {
        // T077 (maintainer ruling F14): the install step has not given the revision generation 1. Its own database: a
        // migrate run on the shared one (MigrationAndRoleTests) would install the revision mid-test.
        await using var db = await GozlemeDatabase.StartAsync();
        await using var s = await RevisionScenario.CreateAsync(db);
        var (_, _, revision) = await s.PublishedAsync();
        var revisionId = revision.GetProperty("id").GetString()!;
        await GozlemeDatabase.ExecuteAsync(
            "SET session_replication_role = replica; DELETE FROM gozleme.revision_children; DELETE FROM gozleme.capability_children; DELETE FROM gozleme.projection_generations;",
            db.SuperuserConnectionString);
        var result = await s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal((ProjectionOperatorCommands.Refused, RegenerateRefusal.NotInstalled, ""), (result.Code, result.Stderr.Trim(), result.Stdout));
        Assert.Equal(0, await db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.projection_generations WHERE revision_id = '{revisionId}'"));
        Assert.Contains(_logs, l => l.Contains("Operator projection generation refused (not_installed)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_usable_owner_login_the_command_exits_1_with_the_fixed_word_failed()
    {
        // T077: exactly "failed" on stderr, never an exception type or message.
        var (_, revisionId, _, _) = await PublishedAsync();
        var unreachable = new Npgsql.NpgsqlConnectionStringBuilder(_s.Db.MigratorConnectionString) { Password = "wrong-password", Timeout = 3 }.ConnectionString;
        foreach (var connection in new[] { "", unreachable })
        {
            var result = await _s.RegenerateAsync(_logs, connection, "--revision", revisionId);
            Assert.Equal((ProjectionOperatorCommands.Failed, "failed", ""), (result.Code, result.Stderr.Trim(), result.Stdout));
        }

        Assert.Equal(1, await GenerationCountAsync(revisionId));
        Assert.Equal(2, _logs.Count(l => l.Contains("Operator projection generation refused (failed)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_different_digest_and_another_adapter_version_are_reported_as_different_and_not_comparable()
    {
        // T074: generation 1 is the stored projection, here re-titled with its digest, so the re-projection differs.
        var (capabilityId, revisionId, _, _) = await PublishedAsync();
        await TamperAsync($"""
            UPDATE gozleme.manifest_revisions SET projection = convert_to(replace(convert_from(projection, 'UTF8'), '"Search"', '"Search before"'), 'UTF8') WHERE id = '{revisionId}';
            UPDATE gozleme.manifest_revisions SET projection_digest = 'sha256:' || encode(sha256(projection), 'hex') WHERE id = '{revisionId}';
            """);
        var different = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal(ProjectionOperatorCommands.Ok, different.Code);
        Assert.EndsWith("digest equals generation 1: no", different.Stdout.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal("different", (await GenerationsAsync(capabilityId, revisionId))[1].GetProperty("comparedToPrevious").GetString());

        // Generation 2 now carries another adapter version than generation 3 will: digests are not compared across them.
        await TamperAsync($"UPDATE gozleme.projection_generations SET adapter_version = '0' WHERE revision_id = '{revisionId}' AND generation = 2;");
        var notComparable = await _s.RegenerateAsync(_logs, null, "--revision", revisionId);
        Assert.Equal(ProjectionOperatorCommands.Ok, notComparable.Code);
        Assert.EndsWith("digest equals generation 2: not_comparable", notComparable.Stdout.TrimEnd(), StringComparison.Ordinal);

        var generations = await GenerationsAsync(capabilityId, revisionId);
        Assert.Equal([JsonValueKind.Undefined, JsonValueKind.String, JsonValueKind.String],
            generations.Select(g => g.TryGetProperty("comparedToPrevious", out var c) ? c.ValueKind : JsonValueKind.Undefined));
        Assert.Equal("not_comparable", generations[2].GetProperty("comparedToPrevious").GetString());
    }

    [Fact]
    public async Task The_app_login_cannot_re_project_or_insert_a_later_generation()
    {
        var (_, revisionId, _, _) = await PublishedAsync();
        var asApp = await _s.RegenerateAsync(null, _s.Db.AppConnectionString, "--revision", revisionId);
        Assert.NotEqual(ProjectionOperatorCommands.Ok, asApp.Code);
        Assert.Equal(1, await GenerationCountAsync(revisionId));

        var insert = $"""
            INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
            SELECT gen_random_uuid(), organization_id, id, 2, adapter_id, adapter_version, projection_version, projection, projection_digest, now()
            FROM gozleme.manifest_revisions WHERE id = '{revisionId}'
            """;
        Assert.Equal("42501", await RevisionScenario.FailsAsync(_s.Db.AppConnectionString, RevisionScenario.Context(_s.CarolId), insert));
    }

    [Fact]
    public async Task A_suspended_publishers_revision_can_be_re_projected()
    {
        var (publisherId, handle) = await _s.NewPublisherAsync();
        var (capabilityId, revisionId, _, _) = await PublishedAsync(publisherId);
        await _s.ReclaimAsync(handle);
        var result = await _s.RegenerateAsync(null, null, "--revision", revisionId);
        Assert.True(result.Code == ProjectionOperatorCommands.Ok, result.Stderr);
        Assert.Equal(2, (await GenerationsAsync(capabilityId, revisionId, publisherId)).Length);
        Assert.Equal(HttpStatusCode.OK, (await RevisionClient.GetAsync(_s.Erin, ChildClient.Children(_s.OrgA, publisherId, capabilityId))).Status);
    }
}
