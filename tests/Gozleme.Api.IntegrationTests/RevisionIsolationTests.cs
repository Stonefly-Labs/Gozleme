using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0007 T064 (FR-018): for the four revision tables a member of organization A, under their own verified context,
/// selects no row of B and inserts none towards B, as <c>gozleme_app</c> and as <c>gozleme_migrator</c>. The operator
/// branch is exact: <c>is_operator()</c> is true only for the owner login with no account context, never for the app
/// role and never for the owner once an account context is established. The shared matrix is in RlsIsolationTests.
/// </summary>
public sealed class RevisionIsolationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;
    private string _foreignCanonical = null!;
    private string _foreignRevision = null!;
    private string _foreignKey = null!;

    public async ValueTask InitializeAsync()
    {
        _s = await RevisionScenario.CreateAsync(fixture.Database);
        await _s.PublishedAsync(); // a row of A for the positive control
        var capability = await OrgClient.GetJsonAsync(_s.BobPublisher, OrgClient.Capability(_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId));
        _foreignCanonical = capability.GetProperty("canonicalId").GetString()!;
        var revision = await RevisionClient.PublishedAsync(_s.BobPublisher, RevisionClient.Revisions(_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId),
            RevisionClient.Envelope(_foreignCanonical, $"1.0.0-iso.{Guid.NewGuid():N}"[..20]));
        _foreignRevision = revision.GetProperty("id").GetString()!;
        _foreignKey = (await RevisionClient.GetAsync(_s.Bob, RevisionClient.Keys(_s.OrgB, _s.ForeignPublisherId))).Body.GetProperty("keys")[0].GetProperty("id").GetString()!;
    }

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    private string[] Logins => [_s.Db.AppConnectionString, _s.Db.MigratorConnectionString];

    [Fact]
    public async Task A_member_of_A_sees_no_revision_row_of_B()
    {
        var carol = RevisionScenario.Context(_s.CarolId);
        foreach (var login in Logins)
        {
            foreach (var table in new[] { "manifest_revisions", "revision_attestations", "publisher_signing_keys", "signing_key_retirements" })
            {
                Assert.Equal(0L, await GozlemeDatabase.InTransactionAsync<long>(login, carol, $"SELECT count(*) FROM gozleme.{table} WHERE organization_id = $1", Guid.Parse(_s.OrgB)));
            }

            Assert.True(await GozlemeDatabase.InTransactionAsync<long>(login, carol, "SELECT count(*) FROM gozleme.manifest_revisions WHERE organization_id = $1", Guid.Parse(_s.OrgA)) > 0);
            Assert.Equal(0L, await GozlemeDatabase.InTransactionAsync<long>(login, carol, "SELECT count(*) FROM gozleme.manifest_revisions WHERE id = $1", Guid.Parse(_foreignRevision)));
        }
    }

    [Fact]
    public async Task A_member_of_A_inserts_no_row_towards_B()
    {
        var carol = RevisionScenario.Context(_s.CarolId);
        const string pk = "decode(repeat('ef', 32), 'hex')";
        string[] inserts =
        [
            RevisionScenario.InsertRevisionSql(_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId, _s.CarolId, sequence: 2, version: "9.0.0"),
            $"""
            INSERT INTO gozleme.revision_attestations (revision_id, organization_id, signing_key_id, statement_version, statement, signature, created_at)
            VALUES ('{_foreignRevision}', '{_s.OrgB}', '{_foreignKey}', 1, convert_to('{"{}"}', 'UTF8'), decode(repeat('00', 64), 'hex'), now())
            """,
            $"""
            INSERT INTO gozleme.publisher_signing_keys (id, organization_id, publisher_id, generation, key_id, algorithm, public_key, encrypted_private_key, created_at, created_by)
            VALUES (gen_random_uuid(), '{_s.OrgB}', '{_s.ForeignPublisherId}', 1, 'sha256:' || encode(sha256({pk}), 'hex'), 'Ed25519', {pk}, '\x01'::bytea || decode(repeat('00', 60), 'hex'), now(), '{_s.CarolId}')
            """,
            $"INSERT INTO gozleme.signing_key_retirements (key_id, organization_id, successor_key_id, reason, retired_at) VALUES ('{_foreignKey}', '{_s.OrgB}', '{_foreignKey}', 'lost', now())",
        ];
        foreach (var login in Logins)
        {
            foreach (var sql in inserts)
            {
                Assert.Equal("42501", await RevisionScenario.FailsAsync(login, carol, sql));
            }
        }

        Assert.Equal(1L, await _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.manifest_revisions WHERE capability_id = '{_s.ForeignCapabilityId}'"));
    }

    [Fact]
    public async Task Is_operator_is_true_only_for_the_owner_without_an_account_context()
    {
        var carol = RevisionScenario.Context(_s.CarolId);
        Assert.False(await GozlemeDatabase.InTransactionAsync<bool>(_s.Db.AppConnectionString, null, "SELECT gozleme.is_operator()"));
        Assert.False(await GozlemeDatabase.InTransactionAsync<bool>(_s.Db.AppConnectionString, carol, "SELECT gozleme.is_operator()"));
        Assert.False(await GozlemeDatabase.InTransactionAsync<bool>(_s.Db.MigratorConnectionString, carol, "SELECT gozleme.is_operator()"));
        Assert.True(await GozlemeDatabase.InTransactionAsync<bool>(_s.Db.MigratorConnectionString, null, "SELECT gozleme.is_operator()"));

        // The app role cannot become the owner for the check: SET ROLE to it is refused.
        Assert.Equal("42501", await RevisionScenario.FailsAsync(_s.Db.AppConnectionString, null, "SET ROLE gozleme_migrator"));
    }
}
