using System.Collections.Concurrent;
using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Manifests;
using Gozleme.Api.Persistence;
using Gozleme.Api.Publishers;
using Gozleme.Api.Revisions;
using Gozleme.Api.Revisions.Signing;
using Gozleme.Api.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 US5. T057 (FR-016, FR-016a, SC-002): revisions published before this feature (simulated by publishing,
/// then removing every child row and generation 1 with the guard triggers bypassed, plus a GOZ-0007-shaped <c>a2a</c>
/// revision with a duplicate skill <c>id</c>, which publication now refuses) get their children and generation 1 from
/// the install step at the end of <c>migrate</c>: the same ordinal mapping as publication, the first duplicate by
/// position keeps the key and the other is a duplicate child absent from every later revision, one record per entry, a
/// repeat run changes nothing, every revision verifies as before, and audit 1603 once per capability. T058
/// (maintainer-approved A7): while a capability has a revision without generation 1, a publication answers
/// <c>503 {"error":"unavailable"}</c> and stores nothing. The class owns its database: the install step spans every
/// capability.
/// </summary>
public sealed class ChildInstallTests : IAsyncLifetime
{
    private GozlemeDatabase _db = null!;
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await GozlemeDatabase.StartAsync();
        _s = await RevisionScenario.CreateAsync(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _s.DisposeAsync();
        await _db.DisposeAsync();
    }

    private Task<long> CountAsync(string sql) => _db.ScalarAsync<long>(sql);

    /// <summary>(sequence, entry index) → (ordinal, kind, name, duplicate) for every record of the capability.</summary>
    private Task<string> MappingAsync(string capabilityId) => _db.ScalarAsync<string>($"""
        SELECT coalesce(string_agg(r.sequence || ':' || rc.entry_index || '>' || c.ordinal || c.kind || '/' || c.native_name || (CASE WHEN c.is_duplicate THEN '!' ELSE '' END),
          ',' ORDER BY r.sequence, rc.entry_index), '')
        FROM gozleme.revision_children rc JOIN gozleme.manifest_revisions r ON r.id = rc.revision_id JOIN gozleme.capability_children c ON c.id = rc.child_id
        WHERE r.capability_id = '{capabilityId}'
        """)!;

    private Task<string> StateAsync() => _db.ScalarAsync<string>("""
        SELECT (SELECT count(*) FROM gozleme.capability_children) || '/' || (SELECT count(*) FROM gozleme.revision_children) || '/' || (SELECT count(*) FROM gozleme.projection_generations)
          || '/' || coalesce((SELECT string_agg(id::text, ',' ORDER BY id) FROM gozleme.capability_children), '')
          || '/' || coalesce((SELECT string_agg(id::text, ',' ORDER BY id) FROM gozleme.projection_generations), '')
        """)!;

    /// <summary>Back to a database that has the tables but no install-step output (migrated, install not yet run).</summary>
    private Task UninstallAsync() => GozlemeDatabase.ExecuteAsync(
        "SET session_replication_role = replica; DELETE FROM gozleme.revision_children; DELETE FROM gozleme.capability_children; DELETE FROM gozleme.projection_generations;",
        _db.SuperuserConnectionString);

    /// <summary>
    /// A revision stored exactly as GOZ-0007 stored it, without the GOZ-0008 duplicate rule: validated, canonicalized
    /// and signed with the publisher's active key by the API's own services, then inserted with the triggers bypassed.
    /// </summary>
    private async Task<string> LegacyRevisionAsync(string capabilityId, byte[] body, int sequence)
    {
        var services = _s.Factory.Services;
        var capability = await OrgClient.GetJsonAsync(_s.Erin, OrgClient.Capability(_s.OrgA, _s.PublisherId, capabilityId));
        var run = services.GetRequiredService<ManifestValidator>().Validate(body,
            new ManifestTarget(CanonicalId.Capability(GozlemeDatabase.Origin, Guid.Parse(capabilityId)), $"{_s.Handle}/{capability.GetProperty("name").GetString()}"));
        var accepted = Assert.IsType<AcceptedResult>(run.Result);

        await using var connection = new NpgsqlConnection(_db.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        PublisherSigningKey key;
        await using (var select = new NpgsqlCommand($"""
            SELECT id, organization_id, publisher_id, generation, key_id, algorithm, public_key, encrypted_private_key FROM gozleme.publisher_signing_keys
            WHERE publisher_id = '{_s.PublisherId}' AND id NOT IN (SELECT key_id FROM gozleme.signing_key_retirements) ORDER BY generation DESC LIMIT 1
            """, connection))
        await using (var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            key = new PublisherSigningKey
            {
                Id = reader.GetGuid(0), OrganizationId = reader.GetGuid(1), PublisherId = reader.GetGuid(2), Generation = reader.GetInt32(3), KeyId = reader.GetString(4),
                Algorithm = reader.GetString(5), PublicKey = reader.GetFieldValue<byte[]>(6), EncryptedPrivateKey = reader.GetFieldValue<byte[]>(7),
            };
        }

        var native = EnvelopeReader.NativeBytes(accepted.Native.Document);
        var projection = CanonicalJson.Canonicalize(ManifestJson.ProjectionNode(accepted.Projection));
        var (adapterId, adapterVersion) = AttestationStatement.SplitAdapter(accepted.Projection.Adapter);
        var now = DateTimeOffset.UtcNow;
        var publishedAt = now.AddTicks(-(now.Ticks % 10)); // PostgreSQL keeps microseconds
        var revision = new ManifestRevision
        {
            Id = Guid.CreateVersion7(publishedAt), OrganizationId = Guid.Parse(_s.OrgA), PublisherId = Guid.Parse(_s.PublisherId), CapabilityId = Guid.Parse(capabilityId),
            Sequence = sequence, Version = accepted.Envelope.Version, VersionKey = VersionKey.From(accepted.Envelope.Version), Family = accepted.Envelope.Family,
            EnvelopeSummary = ManifestJson.SummaryJson(accepted.Envelope), NativeFormat = accepted.Native.Format, NativeFormatVersion = accepted.Native.FormatVersion,
            NativeEncoding = accepted.Native.Encoding, NativeDocument = native, NativeDigest = RevisionVerifier.Digest(native), NativeByteLength = native.Length,
            Projection = projection, ProjectionVersion = accepted.Projection.ProjectionVersion, ProjectionDigest = RevisionVerifier.Digest(projection),
            AdapterId = adapterId, AdapterVersion = adapterVersion, RuleCatalogueVersion = ManifestRules.CatalogueVersion, SchemaBundleVersion = SchemaRegistry.BundleVersion,
            PublishedBy = _s.CarolId, PublishedAt = publishedAt,
        };
        var statement = AttestationStatement.For(revision, GozlemeDatabase.Origin, key.KeyId).ToCanonicalBytes();
        Assert.True(services.GetRequiredService<SigningKeyVault>().TrySign(key, statement, out var signature));

        await using var insert = new NpgsqlCommand("""
            SET session_replication_role = replica;
            INSERT INTO gozleme.manifest_revisions (id, organization_id, publisher_id, capability_id, sequence, version, version_key, family, envelope_summary,
              native_format, native_format_version, native_encoding, native_document, native_digest, native_byte_length, projection, projection_version,
              projection_digest, adapter_id, adapter_version, rule_catalogue_version, schema_bundle_version, published_by, published_at)
            VALUES (@id, @org, @publisher, @capability, @sequence, @version, @versionKey, @family, @summary::jsonb, @format, @formatVersion, @encoding, @native,
              @nativeDigest, @nativeLength, @projection, @projectionVersion, @projectionDigest, @adapterId, @adapterVersion, @rules, @schemas, @by, @at);
            INSERT INTO gozleme.revision_attestations (revision_id, organization_id, signing_key_id, statement_version, statement, signature, created_at)
            VALUES (@id, @org, @key, @statementVersion, @statement, @signature, @at);
            """, connection);
        foreach (var (name, value) in new (string, object)[]
        {
            ("id", revision.Id), ("org", revision.OrganizationId), ("publisher", revision.PublisherId), ("capability", revision.CapabilityId), ("sequence", sequence),
            ("version", revision.Version), ("versionKey", revision.VersionKey), ("family", revision.Family), ("summary", revision.EnvelopeSummary),
            ("format", revision.NativeFormat), ("formatVersion", revision.NativeFormatVersion), ("encoding", revision.NativeEncoding), ("native", native),
            ("nativeDigest", revision.NativeDigest), ("nativeLength", native.Length), ("projection", projection), ("projectionVersion", revision.ProjectionVersion),
            ("projectionDigest", revision.ProjectionDigest), ("adapterId", adapterId), ("adapterVersion", adapterVersion), ("rules", revision.RuleCatalogueVersion),
            ("schemas", revision.SchemaBundleVersion), ("by", revision.PublishedBy), ("at", publishedAt), ("key", key.Id),
            ("statementVersion", AttestationStatement.CurrentVersion), ("statement", statement), ("signature", signature!),
        })
        {
            insert.Parameters.AddWithValue(name, value);
        }

        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return revision.Id.ToString();
    }

    private async Task<string> VerificationAsync(string capabilityId, string revisionId) =>
        (await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, revisionId))).Body.GetProperty("verification").GetRawText();

    [Fact]
    public async Task The_install_step_assigns_pre_existing_revisions_like_publication_and_a_repeat_run_changes_nothing()
    {
        // Two MCP capabilities, several revisions each in mixed version order, published with the feature.
        (string, string, string?)[][] sequence =
        [
            [("tool", "a", null), ("tool", "b", null), ("prompt", "p", null)],
            [("tool", "b", "B"), ("tool", "c", null)],
            [("prompt", "p", null), ("tool", "a", null), ("resource", "r", null)],
        ];
        var capabilities = new List<(string Id, string[] Revisions)>();
        foreach (var versions in new[] { new[] { "2.0.0", "1.0.0", "3.0.0" }, ["1.0.0", "1.1.0", "0.9.0"] })
        {
            var (capabilityId, canonical) = await _s.NewCapabilityAsync();
            var revisions = new List<string>();
            for (var i = 0; i < sequence.Length; i++)
            {
                revisions.Add((await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId), ChildClient.Mcp(canonical, versions[i], sequence[i]))).GetProperty("id").GetString()!);
            }

            capabilities.Add((capabilityId, [.. revisions]));
        }

        // An a2a capability with one revision as published, then a GOZ-0007-shaped revision with a duplicate skill id.
        var (agentId, agentCanonical) = await _s.NewCapabilityAsync();
        var agentFirst = (await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(agentId), ManifestClient.SampleById("valid-agent-card").Body(agentCanonical))).GetProperty("id").GetString()!;
        var expected = new Dictionary<string, string>();
        var verifications = new Dictionary<string, string>();
        foreach (var (id, revisions) in capabilities)
        {
            expected[id] = await MappingAsync(id);
            foreach (var revision in revisions)
            {
                verifications[revision] = await VerificationAsync(id, revision);
            }
        }

        await UninstallAsync();
        var agentDuplicate = await LegacyRevisionAsync(agentId, ChildClient.A2aWithDuplicateSkill(agentCanonical, "2.0.0"), 2);
        verifications[agentFirst] = await VerificationAsync(agentId, agentFirst);
        verifications[agentDuplicate] = await VerificationAsync(agentId, agentDuplicate);
        Assert.Contains("\"verified\"", verifications[agentDuplicate], StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM gozleme.projection_generations"));

        // migrate: the install step is its last step.
        var logs = new ConcurrentQueue<string>();
        using (var provider = new GozlemeApiFactory.CapturingLoggerProvider(logs))
        {
            Assert.Equal(0, await MigrateWithLogsAsync(provider));
        }

        foreach (var (id, _) in capabilities)
        {
            Assert.Equal(expected[id], await MappingAsync(id));
        }

        // The first of the two entries with the repeated skill id keeps the key; the other is a duplicate child.
        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, agentId, agentDuplicate));
        var items = ChildClient.Items(children);
        var duplicates = items.Where(i => i.GetProperty("duplicate").GetBoolean()).ToArray();
        var duplicate = Assert.Single(duplicates);
        var keeper = items.First(i => i.GetProperty("nativeName").GetString() == duplicate.GetProperty("nativeName").GetString());
        Assert.True(keeper.GetProperty("entryIndex").GetInt32() < duplicate.GetProperty("entryIndex").GetInt32());
        var (_, first) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, agentId, agentFirst));
        Assert.Contains(keeper.GetProperty("childId").GetString(), ChildClient.Ids(first));

        // SC-002: one record per entry and one generation 1 per revision, everywhere.
        Assert.Equal(0, await CountAsync("""
            SELECT count(*) FROM gozleme.manifest_revisions r
            WHERE (SELECT count(*) FROM gozleme.revision_children rc WHERE rc.revision_id = r.id) <> jsonb_array_length(convert_from(r.projection, 'UTF8')::jsonb -> 'entries')
               OR (SELECT count(*) FROM gozleme.projection_generations g WHERE g.revision_id = r.id AND g.generation = 1) <> 1
            """));

        // Every revision verifies exactly as before.
        foreach (var (id, revisions) in capabilities.Append((agentId, [agentFirst, agentDuplicate])))
        {
            foreach (var revision in revisions)
            {
                Assert.Equal(verifications[revision], await VerificationAsync(id, revision));
            }
        }

        // Audit 1603 once per installed capability, with opaque IDs only.
        Assert.Equal(3, logs.Count(l => l.Contains("Gozleme.Security.Audit", StringComparison.Ordinal) && l.Contains("children installed", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(logs, l => l.Contains(agentId, StringComparison.Ordinal) && l.Contains("children installed", StringComparison.OrdinalIgnoreCase));

        // A repeat run finds nothing to do.
        var state = await StateAsync();
        Assert.Equal(0, await _db.MigrateAsync());
        Assert.Equal(state, await StateAsync());

        // A later publication with the repeated skill once matches the keeper; the duplicate child stays absent.
        var later = await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(agentId), Version(ManifestClient.SampleById("valid-agent-card").Body(agentCanonical), "3.0.0"));
        var (_, laterChildren) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, agentId, later.GetProperty("id").GetString()!));
        Assert.Contains(keeper.GetProperty("childId").GetString(), ChildClient.Ids(laterChildren));
        var (_, history) = await RevisionClient.GetAsync(_s.Erin, ChildClient.Child(_s.OrgA, _s.PublisherId, agentId, duplicate.GetProperty("childId").GetString()!));
        Assert.True(history.GetProperty("child").GetProperty("duplicate").GetBoolean());
        Assert.Equal([false, true, false], history.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("present").GetBoolean()));
    }

    [Fact]
    public async Task A_publication_while_a_revision_has_no_generation_1_answers_503_and_stores_nothing()
    {
        var (capabilityId, canonical, _) = await _s.PublishedAsync();
        await UninstallAsync();
        var (status, text) = await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId), RevisionClient.Envelope(canonical, "2.0.0"));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}"""), (status, text));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM gozleme.capability_children"));

        // After the install step the same publication succeeds.
        Assert.Equal(0, await _db.MigrateAsync());
        Assert.Equal(HttpStatusCode.Created, (await ManifestClient.SendAsync(_s.Carol, _s.Revisions(capabilityId), RevisionClient.Envelope(canonical, "2.0.0"))).Status);
    }

    private Task<int> MigrateWithLogsAsync(ILoggerProvider provider) => MigrationRunner.RunAsync(
        [
            $"--ConnectionStrings:{MigrationRunner.MigratorConnectionStringName}={_db.MigratorConnectionString}", $"--{TenancyOptions.Section}:ContextKey={GozlemeDatabase.ContextKey}",
            $"--{RegistryOriginOptions.Section}:Origin={GozlemeDatabase.Origin}",
        ], provider);

    private static byte[] Version(byte[] body, string version)
    {
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        envelope["version"] = version;
        return System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }
}
