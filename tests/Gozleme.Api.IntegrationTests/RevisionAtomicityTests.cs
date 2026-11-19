using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T047 (FR-005, research R8): the database refuses what the publication transaction must never produce,
/// even through direct SQL as a publisher member. A revision without an attestation cannot commit (deferred
/// constraint trigger), a sequence other than <c>max + 1</c> and a <c>version_key</c> other than the version without
/// <c>+build</c> are refused, a failure after the key insert leaves neither the key nor the revision, and an
/// attestation by a retired key is refused (security review finding 2).
/// </summary>
public sealed class RevisionAtomicityTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    private string InsertRevision(string capabilityId, string publisherId, int sequence = 1, string version = "1.0.0", string? versionKey = null) =>
        RevisionScenario.InsertRevisionSql(_s.OrgA, publisherId, capabilityId, _s.CarolId, sequence, version, versionKey);

    /// <summary>Runs <paramref name="statements"/> as carol through <c>gozleme_app</c> and commits; returns the commit's SQLSTATE, or null.</summary>
    private async Task<string?> CommitAsCarolAsync(params string[] statements)
    {
        // Named: the process-wide Npgsql meter must not label pools with a connection string (TelemetryTests, T067).
        await using var dataSource = new NpgsqlDataSourceBuilder(_s.Db.AppConnectionString) { Name = "integration-test-fixture" }.Build();
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        try
        {
            foreach (var sql in statements.Prepend(RevisionScenario.Context(_s.CarolId)))
            {
                await using var command = new NpgsqlCommand(sql, connection, tx);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await tx.CommitAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private Task<long> CountAsync(string sql) => _s.Db.ScalarAsync<long>(sql);

    [Fact]
    public async Task A_revision_without_an_attestation_cannot_commit()
    {
        var (capabilityId, _) = await _s.NewCapabilityAsync();
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, await CommitAsCarolAsync(InsertRevision(capabilityId, _s.PublisherId)));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
    }

    [Fact]
    public async Task A_gap_in_the_sequence_or_a_wrong_version_key_is_refused()
    {
        var (capabilityId, canonical) = await _s.NewCapabilityAsync();
        await RevisionClient.PublishedAsync(_s.Carol, _s.Revisions(capabilityId), RevisionClient.Envelope(canonical, "1.0.0"));
        foreach (var sql in new[]
        {
            InsertRevision(capabilityId, _s.PublisherId, sequence: 3, version: "2.0.0"),
            InsertRevision(capabilityId, _s.PublisherId, sequence: 1, version: "2.0.0"),
            InsertRevision(capabilityId, _s.PublisherId, sequence: 2, version: "2.0.0+b", versionKey: "2.0.0+b"),
            InsertRevision(capabilityId, _s.PublisherId, sequence: 2, version: "2.0.0", versionKey: "2.0.1"),
        })
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await CommitAsCarolAsync(sql));
        }

        // Another publisher's capability cannot be named either.
        var (other, _) = await _s.NewPublisherAsync();
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await CommitAsCarolAsync(InsertRevision(capabilityId, other, sequence: 2, version: "2.0.0")));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
    }

    [Fact]
    public async Task An_attestation_by_a_retired_key_is_refused()
    {
        var (publisherId, _) = await _s.NewPublisherAsync();
        var (capabilityId, _, _) = await _s.PublishedAsync("1.0.0", publisherId);
        var retired = (await RevisionClient.GetAsync(_s.Erin, RevisionClient.Keys(_s.OrgA, publisherId))).Body.GetProperty("keys")[0].GetProperty("id").GetString();
        Assert.Equal(0, (await _s.SuccessionAsync(null, null, "--publisher", publisherId, "--reason", "compromised")).Code);
        var revision = Guid.CreateVersion7();
        var insert = InsertRevision(capabilityId, publisherId, sequence: 2, version: "2.0.0").Replace("VALUES (gen_random_uuid(),", $"VALUES ('{revision}',", StringComparison.Ordinal);
        var attest = $"""
            INSERT INTO gozleme.revision_attestations (revision_id, organization_id, signing_key_id, statement_version, statement, signature, created_at)
            VALUES ('{revision}', '{_s.OrgA}', '{retired}', 1, convert_to('{"{}"}', 'UTF8'), decode(repeat('00', 64), 'hex'), now())
            """;
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await CommitAsCarolAsync(insert, attest));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{capabilityId}'"));
    }

    [Fact]
    public async Task A_failure_after_the_key_insert_leaves_no_key_and_no_revision()
    {
        var (publisherId, _) = await _s.NewPublisherAsync();
        var (capabilityId, _) = await _s.NewCapabilityAsync(publisherId);
        const string pk = "decode(repeat('cd', 32), 'hex')";
        var insertKey = $"""
            INSERT INTO gozleme.publisher_signing_keys (id, organization_id, publisher_id, generation, key_id, algorithm, public_key, encrypted_private_key, created_at, created_by)
            VALUES (gen_random_uuid(), '{_s.OrgA}', '{publisherId}', 1, 'sha256:' || encode(sha256({pk}), 'hex'), 'Ed25519', {pk}, '\x01'::bytea || decode(repeat('00', 60), 'hex'), now(), '{_s.CarolId}')
            """;
        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, await CommitAsCarolAsync(insertKey, InsertRevision(capabilityId, publisherId)));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.publisher_signing_keys WHERE publisher_id = '{publisherId}'"));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM gozleme.manifest_revisions WHERE publisher_id = '{publisherId}'"));

        // The API's own first publication still creates the publisher's first key afterwards.
        var (_, keys) = await RevisionClient.GetAsync(_s.Erin, RevisionClient.Keys(_s.OrgA, publisherId));
        Assert.Empty(keys.GetProperty("keys").EnumerateArray());
    }
}
