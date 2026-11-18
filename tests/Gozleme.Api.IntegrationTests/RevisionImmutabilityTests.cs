using System.Net;
using System.Text;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 US2. T045 (FR-004, SC-002): <c>UPDATE</c>, <c>DELETE</c> and <c>TRUNCATE</c> on the four tables change
/// nothing as <c>gozleme_app</c> with an account context or as <c>gozleme_migrator</c> (with or without one); the API
/// has no route that changes a revision, attestation or key (<c>405</c> on the revision); both revisions still verify.
/// T046 (SC-003): a superuser who bypasses RLS and triggers alters one byte of a dedicated revision's native document,
/// projection, statement or signature, and the read reports <c>failed</c> with the first failing reason.
/// </summary>
public sealed class RevisionImmutabilityTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Tables = ["manifest_revisions", "revision_attestations", "publisher_signing_keys", "signing_key_retirements"];

    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    private GozlemeDatabase Db => _s.Db;

    /// <summary>Refused means an error, or (where forced RLS hides every row from UPDATE/DELETE) no row affected.</summary>
    private static async Task AssertChangesNothingAsync(string connection, string? setup, string sql)
    {
        try
        {
            var affected = await GozlemeDatabase.InTransactionAsync<long>(connection, setup, $"WITH c AS ({sql} RETURNING 1) SELECT count(*) FROM c");
            Assert.True(affected == 0, $"{sql} changed {affected} rows");
        }
        catch (PostgresException)
        {
            // refused
        }
    }

    [Fact]
    public async Task No_login_and_no_route_changes_a_revision_attestation_or_key()
    {
        var (publisherId, _) = await _s.NewPublisherAsync();
        var (capabilityId, canonical, first) = await _s.PublishedAsync("1.0.0", publisherId);
        var second = await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId, publisherId), RevisionClient.Envelope(canonical, "1.1.0"));
        Assert.Equal(0, (await _s.SuccessionAsync(null, null, "--publisher", publisherId, "--reason", "lost")).Code); // a retirement row to attack
        var snapshot = await SnapshotAsync(publisherId);

        var where = new Dictionary<string, string>
        {
            ["manifest_revisions"] = $"publisher_id = '{publisherId}'",
            ["revision_attestations"] = $"revision_id IN (SELECT id FROM gozleme.manifest_revisions WHERE publisher_id = '{publisherId}')",
            ["publisher_signing_keys"] = $"publisher_id = '{publisherId}'",
            ["signing_key_retirements"] = $"key_id IN (SELECT id FROM gozleme.publisher_signing_keys WHERE publisher_id = '{publisherId}')",
        };
        var set = new Dictionary<string, string>
        {
            ["manifest_revisions"] = "version = '9.9.9'", ["revision_attestations"] = "signature = signature", ["publisher_signing_keys"] = "generation = generation + 10",
            ["signing_key_retirements"] = "reason = 'compromised'",
        };
        var withContext = RevisionScenario.Context(_s.CarolId);
        foreach (var table in Tables)
        {
            foreach (var (login, setup) in new[] { (Db.AppConnectionString, withContext), (Db.MigratorConnectionString, withContext), (Db.MigratorConnectionString, (string?)null) })
            {
                await AssertChangesNothingAsync(login, setup, $"UPDATE gozleme.{table} SET {set[table]} WHERE {where[table]}");
                await AssertChangesNothingAsync(login, setup, $"DELETE FROM gozleme.{table} WHERE {where[table]}");
                await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(login, setup, $"TRUNCATE gozleme.{table} CASCADE"));
            }

            // TRUNCATE bypasses RLS, so it reaches the immutable_row_guard statement trigger even as the owner; UPDATE and
            // DELETE have no policy on these tables, so forced RLS leaves them no row, the operator included.
        }

        var revision = _s.Revision(capabilityId, first.GetProperty("id").GetString()!, publisherId);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            foreach (var path in new[] { revision, revision + "/statement", revision + "/signature", RevisionClient.Keys(_s.OrgA, publisherId) })
            {
                using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                using var response = await _s.Carol.SendAsync(request, TestContext.Current.CancellationToken);
                Assert.True(response.StatusCode == HttpStatusCode.MethodNotAllowed, $"{method} {path}: {response.StatusCode}");
            }
        }

        Assert.Equal(snapshot, await SnapshotAsync(publisherId));
        foreach (var id in new[] { first, second }.Select(r => r.GetProperty("id").GetString()!))
        {
            var (_, read) = await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, id, publisherId));
            Assert.Equal("verified", read.GetProperty("verification").GetProperty("status").GetString());
        }
    }

    public static TheoryData<string, string> Tampers => new()
    {
        { "native", "native_digest_mismatch" },
        { "projection", "projection_digest_mismatch" },
        { "statement-canonical", "signature_invalid" },
        { "statement-whitespace", "malformed_statement" },
        { "signature", "signature_invalid" },
    };

    [Theory]
    [MemberData(nameof(Tampers))]
    public async Task One_altered_stored_byte_fails_verification_with_its_reason(string part, string reason)
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync($"1.0.0-tamper.{part}");
        var id = Guid.Parse(revision.GetProperty("id").GetString()!);
        var (table, column, key) = part switch
        {
            "native" => ("manifest_revisions", "native_document", "id"),
            "projection" => ("manifest_revisions", "projection", "id"),
            "signature" => ("revision_attestations", "signature", "revision_id"),
            _ => ("revision_attestations", "statement", "revision_id"),
        };

        await GozlemeDatabase.WithConnectionAsync(Db.SuperuserConnectionString, async c =>
        {
            byte[] bytes;
            await using (var read = new NpgsqlCommand($"SELECT {column} FROM gozleme.{table} WHERE {key} = $1", c))
            {
                read.Parameters.Add(new NpgsqlParameter { Value = id });
                bytes = (byte[])(await read.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            }

            bytes = Tamper(part, bytes);
            await using (var replica = new NpgsqlCommand("SET session_replication_role = replica", c))
            {
                await replica.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var write = new NpgsqlCommand($"UPDATE gozleme.{table} SET {column} = $1 WHERE {key} = $2", c);
            write.Parameters.Add(new NpgsqlParameter { Value = bytes });
            write.Parameters.Add(new NpgsqlParameter { Value = id });
            Assert.Equal(1, await write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            return 0;
        });

        var (status, read) = await RevisionClient.GetAsync(_s.Erin, _s.Revision(capabilityId, id.ToString()));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("failed", read.GetProperty("verification").GetProperty("status").GetString());
        Assert.Equal(reason, read.GetProperty("verification").GetProperty("reason").GetString());
        var (_, list) = await RevisionClient.GetAsync(_s.Erin, _s.Revisions(capabilityId));
        Assert.Equal(reason, list.GetProperty("revisions")[0].GetProperty("verification").GetProperty("reason").GetString());
    }

    private static byte[] Tamper(string part, byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        switch (part)
        {
            case "statement-canonical":
                {
                    // One hex digit of the native digest: the bytes stay canonical, so only the signature can fail.
                    var at = Encoding.UTF8.GetString(copy).IndexOf("\"digest\":\"sha256:", StringComparison.Ordinal) + "\"digest\":\"sha256:".Length;
                    copy[at] = copy[at] == (byte)'0' ? (byte)'1' : (byte)'0';
                    return copy;
                }

            case "statement-whitespace":
                return [(byte)' ', .. copy];
            case "projection":
                copy[2] ^= 1; // the first key's first letter: still JSON, different bytes
                return copy;
            default:
                copy[0] ^= 1;
                return copy;
        }
    }

    private Task<string> SnapshotAsync(string publisherId) => Db.ScalarAsync<string>($"""
        SELECT concat_ws('|',
          (SELECT string_agg(concat_ws(',', id, sequence, version, native_digest, projection_digest, published_at, encode(sha256(native_document), 'hex'), encode(sha256(projection), 'hex')), ';' ORDER BY id)
             FROM gozleme.manifest_revisions WHERE publisher_id = '{publisherId}'),
          (SELECT string_agg(concat_ws(',', a.revision_id, encode(sha256(a.statement), 'hex'), encode(sha256(a.signature), 'hex'), a.signing_key_id), ';' ORDER BY a.revision_id)
             FROM gozleme.revision_attestations a JOIN gozleme.manifest_revisions r ON r.id = a.revision_id WHERE r.publisher_id = '{publisherId}'),
          (SELECT string_agg(concat_ws(',', id, generation, key_id, encode(sha256(encrypted_private_key), 'hex')), ';' ORDER BY generation)
             FROM gozleme.publisher_signing_keys WHERE publisher_id = '{publisherId}'),
          (SELECT string_agg(concat_ws(',', t.key_id, t.successor_key_id, t.reason, t.retired_at), ';' ORDER BY t.key_id)
             FROM gozleme.signing_key_retirements t JOIN gozleme.publisher_signing_keys k ON k.id = t.key_id WHERE k.publisher_id = '{publisherId}'))
        """);
}
