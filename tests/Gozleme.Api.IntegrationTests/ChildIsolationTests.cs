using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 T053 (FR-013, FR-018): for the three child tables a member of organization A, under their own verified
/// context, selects no row of B and inserts none towards B, as <c>gozleme_app</c> and as <c>gozleme_migrator</c>; the
/// owner without a context (the operator) sees B's rows, which is how <c>projection-regenerate</c> reads them (A2). The
/// shared matrix is in RlsIsolationTests.
/// </summary>
public sealed class ChildIsolationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Tables = ["capability_children", "revision_children", "projection_generations"];

    private RevisionScenario _s = null!;
    private string _foreignRevision = null!;
    private string _foreignChild = null!;

    public async ValueTask InitializeAsync()
    {
        _s = await RevisionScenario.CreateAsync(fixture.Database);
        await _s.PublishedAsync(); // rows of A for the positive control
        var capability = await OrgClient.GetJsonAsync(_s.BobPublisher, OrgClient.Capability(_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId));
        var revision = await RevisionClient.PublishedAsync(_s.BobPublisher, RevisionClient.Revisions(_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId),
            ChildClient.Mcp(capability.GetProperty("canonicalId").GetString()!, $"1.0.0-iso.{Guid.NewGuid():N}"[..20], ("tool", "search", null)));
        _foreignRevision = revision.GetProperty("id").GetString()!;
        var (_, children) = await RevisionClient.GetAsync(_s.Bob, ChildClient.RevisionChildren(_s.OrgB, _s.ForeignPublisherId, _s.ForeignCapabilityId, _foreignRevision));
        _foreignChild = ChildClient.Ids(children)[0];
    }

    public ValueTask DisposeAsync() => _s.DisposeAsync();

    private string[] Logins => [_s.Db.AppConnectionString, _s.Db.MigratorConnectionString];

    [Fact]
    public async Task A_member_of_A_sees_no_child_row_of_B_and_the_operator_does()
    {
        var carol = RevisionScenario.Context(_s.CarolId);
        foreach (var table in Tables)
        {
            foreach (var login in Logins)
            {
                Assert.True(await GozlemeDatabase.InTransactionAsync<long>(login, carol, $"SELECT count(*) FROM gozleme.{table} WHERE organization_id = $1", Guid.Parse(_s.OrgA)) > 0);
                Assert.Equal(0L, await GozlemeDatabase.InTransactionAsync<long>(login, carol, $"SELECT count(*) FROM gozleme.{table} WHERE organization_id = $1", Guid.Parse(_s.OrgB)));
            }

            Assert.True(await GozlemeDatabase.InTransactionAsync<long>(_s.Db.MigratorConnectionString, null, $"SELECT count(*) FROM gozleme.{table} WHERE organization_id = $1", Guid.Parse(_s.OrgB)) > 0);
            Assert.Equal(0L, await GozlemeDatabase.InTransactionAsync<long>(_s.Db.AppConnectionString, null, $"SELECT count(*) FROM gozleme.{table}"));
        }
    }

    [Fact]
    public async Task A_member_of_A_inserts_no_child_row_towards_B()
    {
        var carol = RevisionScenario.Context(_s.CarolId);
        string[] inserts =
        [
            $"""
            INSERT INTO gozleme.capability_children (id, organization_id, capability_id, kind, native_name, is_duplicate, ordinal, first_revision_id, created_at)
            VALUES (gen_random_uuid(), '{_s.OrgB}', '{_s.ForeignCapabilityId}', 'tool', 'planted', false, 2, '{_foreignRevision}', now())
            """,
            $"""
            INSERT INTO gozleme.revision_children (revision_id, organization_id, entry_index, child_id, kind, native_name, title)
            VALUES ('{_foreignRevision}', '{_s.OrgB}', 1, '{_foreignChild}', 'tool', 'search', NULL)
            """,
            $"""
            INSERT INTO gozleme.projection_generations (id, organization_id, revision_id, generation, adapter_id, adapter_version, projection_version, projection, projection_digest, created_at)
            VALUES (gen_random_uuid(), '{_s.OrgB}', '{_foreignRevision}', 2, 'mcp', '1', '1', convert_to('{"{}"}', 'UTF8'), 'sha256:' || encode(sha256(convert_to('{"{}"}', 'UTF8')), 'hex'), now())
            """,
        ];
        foreach (var login in Logins)
        {
            foreach (var sql in inserts)
            {
                Assert.Equal("42501", await RevisionScenario.FailsAsync(login, carol, sql));
            }
        }

        Assert.Equal(1L, await _s.Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.capability_children WHERE capability_id = '{_s.ForeignCapabilityId}'"));
    }
}
