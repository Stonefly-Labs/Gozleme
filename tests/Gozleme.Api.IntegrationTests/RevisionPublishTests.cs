using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 US1 and US2 through the API: two publications in order with every FR-002 column stored and the statement's
/// <c>publishedAt</c> equal to the row (T035); a GOZ-0006 rejection answered byte-identically to validate and nothing
/// stored, and concurrent first publications creating exactly one key (T036); republishing an existing version, with
/// different, identical or <c>+build</c> bytes, refused <c>409 version_exists</c>, and concurrent publications of one
/// version giving exactly one row (T044).
/// </summary>
public sealed class RevisionPublishTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!; // admin, member
    private HttpClient _carol = null!; // member, publisher
    private Guid _carolId;
    private string _org = null!;
    private string _publisherId = null!;

    private GozlemeDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString, settings: new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "10000" });
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        _org = await OrgClient.CreateAsync(_alice, "Revisions A");
        (_carol, _carolId) = await TwoTenants.AddPublisherMemberAsync(_factory, _alice, Guid.Parse(_org));
        _publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _org, OrgClient.NewHandle("rv"))).GetProperty("id").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        _carol.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(string Id, string Canonical)> NewCapabilityAsync(string? publisherId = null)
    {
        var capability = await OrgClient.CreateCapabilityAsync(_carol, _org, publisherId ?? _publisherId, $"cap-{Guid.NewGuid():N}"[..20]);
        return (capability.GetProperty("id").GetString()!, capability.GetProperty("canonicalId").GetString()!);
    }

    [Fact]
    public async Task Two_revisions_publish_in_order_with_every_column_stored()
    {
        var (capabilityId, canonical) = await NewCapabilityAsync();
        var path = RevisionClient.Revisions(_org, _publisherId, capabilityId);
        var firstBody = RevisionClient.Envelope(canonical, "1.0.0");

        var (status, first, response) = await RevisionClient.PublishAsync(_carol, path, firstBody);
        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, status);
            Assert.Equal($"{path}/{first.GetProperty("id").GetString()}", response.Headers.Location?.OriginalString);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        var second = await RevisionClient.PublishedAsync(_carol, path, RevisionClient.Envelope(canonical, "1.1.0"));
        Assert.Equal(1, first.GetProperty("sequence").GetInt32());
        Assert.Equal(2, second.GetProperty("sequence").GetInt32());
        foreach (var revision in new[] { first, second })
        {
            Assert.Equal("verified", revision.GetProperty("verification").GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, revision.GetProperty("verification").GetProperty("reason").ValueKind);
            Assert.Equal(canonical, revision.GetProperty("capabilityId").GetString());
            Assert.Equal(_carolId.ToString(), revision.GetProperty("publishedBy").GetString());
        }

        // The list shows both in order with the API's records; the first is unchanged by the second.
        var (listStatus, list) = await RevisionClient.GetAsync(_carol, path);
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.False(list.GetProperty("publisherSuspended").GetBoolean());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("next").ValueKind);
        Assert.Equal(["1.0.0", "1.1.0"], list.GetProperty("revisions").EnumerateArray().Select(r => r.GetProperty("version").GetString()));
        var (_, reread) = await RevisionClient.GetAsync(_carol, RevisionClient.Revision(_org, _publisherId, capabilityId, first.GetProperty("id").GetString()!));
        Assert.Equal(first.GetProperty("publishedAt").GetString(), reread.GetProperty("publishedAt").GetString());
        Assert.Equal(first.GetProperty("attestation").GetRawText(), reread.GetProperty("attestation").GetRawText());

        // FR-002: every column as published, and the signed publishedAt equals the stored one.
        var document = JsonDocument.Parse(firstBody).RootElement.GetProperty("native").GetProperty("document").GetString()!;
        var native = Encoding.UTF8.GetBytes(document);
        var id = Guid.Parse(first.GetProperty("id").GetString()!);
        await GozlemeDatabase.WithConnectionAsync(Db.SuperuserConnectionString, async c =>
        {
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT r.native_document, r.native_digest, r.native_byte_length, r.adapter_id, r.adapter_version, r.projection_version,
                       r.rule_catalogue_version, r.schema_bundle_version, r.published_by, r.family, r.version_key, r.projection_digest,
                       encode(sha256(r.projection), 'hex'), convert_from(a.statement, 'UTF8')::jsonb ->> 'publishedAt',
                       to_char(r.published_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
                FROM gozleme.manifest_revisions r JOIN gozleme.revision_attestations a ON a.revision_id = r.id WHERE r.id = $1
                """, c);
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = id });
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(native, reader.GetFieldValue<byte[]>(0));
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(native)), reader.GetString(1));
            Assert.Equal(native.Length, reader.GetInt32(2));
            Assert.Equal(("mcp", "1", "1"), (reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            Assert.Equal(("1", "1"), (reader.GetString(6), reader.GetString(7)));
            Assert.Equal(_carolId, reader.GetGuid(8));
            Assert.Equal(("mcp", "1.0.0"), (reader.GetString(9), reader.GetString(10)));
            Assert.Equal(reader.GetString(11), "sha256:" + reader.GetString(12));
            Assert.Equal(reader.GetString(14), reader.GetString(13));
            return 0;
        });

        // The first publication created exactly one generation-1 key, readable through the key list.
        var (_, keys) = await RevisionClient.GetAsync(_carol, RevisionClient.Keys(_org, _publisherId));
        var key = Assert.Single(keys.GetProperty("keys").EnumerateArray());
        Assert.Equal(1, key.GetProperty("generation").GetInt32());
        Assert.Equal("active", key.GetProperty("status").GetString());
        Assert.Equal(first.GetProperty("attestation").GetProperty("keyId").GetString(), key.GetProperty("keyId").GetString());
    }

    [Fact]
    public async Task A_lower_version_after_a_higher_one_is_accepted_and_ordered_by_publication()
    {
        var (capabilityId, canonical) = await NewCapabilityAsync();
        var path = RevisionClient.Revisions(_org, _publisherId, capabilityId);
        await RevisionClient.PublishedAsync(_carol, path, RevisionClient.Envelope(canonical, "2.0.0"));
        var lower = await RevisionClient.PublishedAsync(_carol, path, RevisionClient.Envelope(canonical, "1.2.5"));
        Assert.Equal(2, lower.GetProperty("sequence").GetInt32());
        var (_, list) = await RevisionClient.GetAsync(_carol, path);
        Assert.Equal(["2.0.0", "1.2.5"], list.GetProperty("revisions").EnumerateArray().Select(r => r.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task A_rejection_answers_exactly_as_validate_and_stores_nothing()
    {
        var (capabilityId, canonical) = await NewCapabilityAsync();
        var publish = RevisionClient.Revisions(_org, _publisherId, capabilityId);
        var validate = ManifestClient.Validate(_org, _publisherId, capabilityId);
        var invalid = ManifestClient.Samples.Where(s => !s.Valid).GroupBy(s => s.Status).Select(g => g.First()).ToList();
        Assert.Contains(invalid, s => s.Status == HttpStatusCode.UnprocessableEntity);
        Assert.Contains(invalid, s => s.Status == HttpStatusCode.BadRequest);
        foreach (var body in invalid.Select(s => s.Body(canonical)).Append("{not json"u8.ToArray()))
        {
            Assert.Equal(await ManifestClient.SendAsync(_carol, validate, body), await ManifestClient.SendAsync(_carol, publish, body));
        }

        // Too large: one native byte over the bound is the same 413 result body.
        var padded = System.Text.Json.Nodes.JsonNode.Parse(RevisionClient.Envelope(canonical, "9.0.0"))!;
        padded["native"]!["document"] = padded["native"]!["document"]!.GetValue<string>() + new string(' ', 512 * 1024);
        var tooLarge = Encoding.UTF8.GetBytes(padded.ToJsonString());
        var (status, _) = await ManifestClient.SendAsync(_carol, publish, tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal(await ManifestClient.SendAsync(_carol, validate, tooLarge), await ManifestClient.SendAsync(_carol, publish, tooLarge));

        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'", Db.SuperuserConnectionString));
    }

    [Fact]
    public async Task A_lone_surrogate_in_the_native_document_is_refused_never_a_server_error()
    {
        var (capabilityId, canonical) = await NewCapabilityAsync();
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(RevisionClient.Envelope(canonical, "1.0.0"))!;
        var document = envelope["native"]!["document"]!.GetValue<string>().Replace("\"title\": \"Forecasts\"", "\"title\": \"Fore\\ud800casts\"", StringComparison.Ordinal);
        Assert.Contains("\\ud800", document, StringComparison.Ordinal);
        envelope["native"]!["document"] = document;
        var body = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var (status, _) = await ManifestClient.SendAsync(_carol, RevisionClient.Revisions(_org, _publisherId, capabilityId), body);
        Assert.True((int)status is >= 400 and < 500, $"{status}");
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'", Db.SuperuserConnectionString));
    }

    [Fact]
    public async Task Concurrent_first_publications_create_exactly_one_key()
    {
        var publisherId = (await OrgClient.RegisterPublisherAsync(_alice, _org, OrgClient.NewHandle("rk"))).GetProperty("id").GetString()!;
        var targets = new List<(string Path, byte[] Body)>();
        for (var i = 0; i < 3; i++)
        {
            var (capabilityId, canonical) = await NewCapabilityAsync(publisherId);
            targets.Add((RevisionClient.Revisions(_org, publisherId, capabilityId), RevisionClient.Envelope(canonical, "1.0.0")));
            targets.Add((RevisionClient.Revisions(_org, publisherId, capabilityId), RevisionClient.Envelope(canonical, "2.0.0")));
        }

        var results = await Task.WhenAll(targets.Select(t => RevisionClient.PublishAsync(_carol, t.Path, t.Body)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Single(results.Select(r => r.Body.GetProperty("attestation").GetProperty("keyId").GetString()).Distinct());
        Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.publisher_signing_keys WHERE publisher_id = '{publisherId}'", Db.SuperuserConnectionString));
    }

    [Fact]
    public async Task An_existing_version_is_refused_whatever_its_content()
    {
        var (capabilityId, canonical) = await NewCapabilityAsync();
        var path = RevisionClient.Revisions(_org, _publisherId, capabilityId);
        var original = RevisionClient.Envelope(canonical, "1.0.0");
        await RevisionClient.PublishedAsync(_carol, path, original);
        var different = RevisionClient.Envelope(canonical, "1.0.0", "valid-stateful-2025-11-25");
        var build = RevisionClient.Envelope(canonical, "1.0.0+build.7");
        foreach (var body in new[] { different, original, build })
        {
            var (status, text) = await ManifestClient.SendAsync(_carol, path, body);
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("""{"error":"version_exists"}""", text);
        }

        // A pre-release is a different precedence, so it is a new revision.
        await RevisionClient.PublishedAsync(_carol, path, RevisionClient.Envelope(canonical, "1.0.0-rc.1"));
        Assert.Equal(2, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'", Db.SuperuserConnectionString));
    }

    [Fact]
    public async Task Concurrent_publications_of_one_version_store_exactly_one_revision()
    {
        var (capabilityId, canonical) = await NewCapabilityAsync();
        var path = RevisionClient.Revisions(_org, _publisherId, capabilityId);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ManifestClient.SendAsync(_carol, path, RevisionClient.Envelope(canonical, i % 2 == 0 ? "3.0.0" : "3.0.0+b" + i))));
        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal((HttpStatusCode.Conflict, """{"error":"version_exists"}"""), r));
        Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'", Db.SuperuserConnectionString));
    }
}
