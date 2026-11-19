using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0008 T050 (FR-003, FR-004, FR-017, US4): <c>UPDATE</c>, <c>DELETE</c> and <c>TRUNCATE</c> on the three child
/// tables change nothing as <c>gozleme_app</c> with an account context or as <c>gozleme_migrator</c> (with or without
/// one, so the operator too) or as the superuser (T073); the API has no write route on the child and projection paths (<c>405</c>).
/// </summary>
public sealed class ChildImmutabilityTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private RevisionScenario _s = null!;

    public async ValueTask InitializeAsync() => _s = await RevisionScenario.CreateAsync(fixture.Database);

    public ValueTask DisposeAsync() => _s.DisposeAsync();

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
    public async Task No_login_changes_deletes_or_truncates_a_child_record_or_generation()
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync();
        var revisionId = revision.GetProperty("id").GetString()!;
        Assert.Equal(0, (await _s.RegenerateAsync(null, null, "--revision", revisionId)).Code); // a generation 2 row to attack
        string Snapshot() => _s.Db.ScalarAsync<string>($"""
            SELECT (SELECT string_agg(id::text || kind || native_name || ordinal || is_duplicate, ',' ORDER BY ordinal) FROM gozleme.capability_children WHERE capability_id = '{capabilityId}')
              || (SELECT string_agg(entry_index || child_id::text || coalesce(title, '-'), ',' ORDER BY entry_index) FROM gozleme.revision_children WHERE revision_id = '{revisionId}')
              || (SELECT string_agg(id::text || generation || coalesce(projection_digest, '-'), ',' ORDER BY generation) FROM gozleme.projection_generations WHERE revision_id = '{revisionId}')
            """).GetAwaiter().GetResult();
        var before = Snapshot();

        var cases = new (string Table, string Set, string Where)[]
        {
            ("capability_children", "native_name = 'renamed'", $"capability_id = '{capabilityId}'"),
            ("capability_children", "is_duplicate = true", $"capability_id = '{capabilityId}'"),
            ("revision_children", "child_id = child_id", $"revision_id = '{revisionId}'"),
            ("revision_children", "title = 'changed'", $"revision_id = '{revisionId}'"),
            ("projection_generations", "generation = generation + 10", $"revision_id = '{revisionId}'"),
        };
        var withContext = RevisionScenario.Context(_s.CarolId);
        foreach (var (login, setup) in new[] { (_s.Db.AppConnectionString, withContext), (_s.Db.MigratorConnectionString, withContext), (_s.Db.MigratorConnectionString, (string?)null) })
        {
            foreach (var (table, set, where) in cases)
            {
                await AssertChangesNothingAsync(login, setup, $"UPDATE gozleme.{table} SET {set} WHERE {where}");
                await AssertChangesNothingAsync(login, setup, $"DELETE FROM gozleme.{table} WHERE {where}");
                await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(login, setup, $"TRUNCATE gozleme.{table} CASCADE"));
            }
        }

        // T073 (US4/AC1, FR-017): the superuser bypasses RLS, so every attempt reaches the guard and is refused outright.
        foreach (var (table, set, where) in cases)
        {
            foreach (var sql in new[] { $"UPDATE gozleme.{table} SET {set} WHERE {where}", $"DELETE FROM gozleme.{table} WHERE {where}", $"TRUNCATE gozleme.{table} CASCADE" })
            {
                await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(_s.Db.SuperuserConnectionString, null, sql));
            }
        }

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task The_child_and_projection_routes_have_no_write_method()
    {
        var (capabilityId, _, revision) = await _s.PublishedAsync();
        var revisionId = revision.GetProperty("id").GetString()!;
        var (_, children) = await RevisionClient.GetAsync(_s.Erin, ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, revisionId));
        var childId = ChildClient.Ids(children)[0];
        var (_, generations) = await RevisionClient.GetAsync(_s.Erin, ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId));
        var generationId = ChildClient.Ids(generations, "id")[0];

        foreach (var path in new[]
        {
            ChildClient.Children(_s.OrgA, _s.PublisherId, capabilityId), ChildClient.Child(_s.OrgA, _s.PublisherId, capabilityId, childId),
            ChildClient.RevisionChildren(_s.OrgA, _s.PublisherId, capabilityId, revisionId), ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId),
            $"{ChildClient.Projections(_s.OrgA, _s.PublisherId, capabilityId, revisionId)}/{generationId}",
        })
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
                using var response = await _s.Carol.SendAsync(request, TestContext.Current.CancellationToken);
                Assert.True(response.StatusCode == HttpStatusCode.MethodNotAllowed, $"{method} {path}: {response.StatusCode}");
            }
        }
    }
}
