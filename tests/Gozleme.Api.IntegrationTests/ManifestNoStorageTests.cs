using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0006 SC-007 (FR-017): validation stores nothing. As the superuser, the <c>gozleme</c> schema (tables, columns,
/// functions, grants), every table's row count and the migration history are identical before and after a batch of
/// accepted and rejected validations, and validation logs no audit event; a denial keeps its existing
/// <c>authz_denied</c> event (FR-023). Own database, so no other test writes meanwhile.
/// </summary>
public sealed class ManifestNoStorageTests : IAsyncLifetime
{
    private GozlemeDatabase _db = null!;
    private GozlemeApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await GozlemeDatabase.StartAsync();
        _factory = new GozlemeApiFactory(_db.AppConnectionString, settings: new Dictionary<string, string?> { ["Gozleme:Manifests:ValidatePerMinute"] = "10000" });
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task A_batch_of_validations_changes_no_schema_row_or_audit_log()
    {
        var (alice, _) = await AuthClient.SignedInAsync(_factory);
        using var _a = alice;
        var org = await OrgClient.CreateAsync(alice, "No storage");
        var (carol, _) = await TwoTenants.AddPublisherMemberAsync(_factory, alice, Guid.Parse(org));
        using var _c = carol;
        var publisherId = (await OrgClient.RegisterPublisherAsync(alice, org)).GetProperty("id").GetString()!;
        var capability = await OrgClient.CreateCapabilityAsync(carol, org, publisherId, "manifest-demo");
        var path = ManifestClient.Validate(org, publisherId, capability.GetProperty("id").GetString()!);
        var canonical = capability.GetProperty("canonicalId").GetString()!;

        var before = await SnapshotAsync();
        var audits = _factory.Logs.Count(IsAudit);
        var denials = _factory.Logs.Count(IsDenial);
        foreach (var sample in ManifestClient.Samples)
        {
            Assert.Equal(sample.Status, (await ManifestClient.SendAsync(carol, path, sample.Body(canonical))).Status);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await ManifestClient.SendAsync(alice, path, "{}"u8.ToArray())).Status);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(denials + 1, _factory.Logs.Count(IsDenial));
        Assert.Equal(audits + 1, _factory.Logs.Count(IsAudit));
    }

    private static bool IsAudit(string line) => line.Contains("Gozleme.Security.Audit", StringComparison.Ordinal);

    private static bool IsDenial(string line) => IsAudit(line) && line.Contains("Authorization denied (manifest.validate)", StringComparison.Ordinal);

    private async Task<string> SnapshotAsync()
    {
        var schema = await _db.ScalarAsync<string>("""
            SELECT concat_ws(E'\n',
              (SELECT string_agg(table_name || '.' || column_name || ':' || data_type, ',' ORDER BY table_name, ordinal_position) FROM information_schema.columns WHERE table_schema = 'gozleme'),
              (SELECT string_agg(p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')', ',' ORDER BY p.proname, 2) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'gozleme'),
              (SELECT string_agg(grantee || ':' || table_name || ':' || privilege_type, ',' ORDER BY grantee, table_name, privilege_type) FROM information_schema.role_table_grants WHERE table_schema = 'gozleme'))
            """, _db.SuperuserConnectionString);
        var tables = await _db.ScalarAsync<string>(
            "SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables WHERE table_schema = 'gozleme' AND table_type = 'BASE TABLE'",
            _db.SuperuserConnectionString);
        var counts = new List<string>();
        foreach (var table in tables.Split(','))
        {
            counts.Add($"{table}={await _db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.\"{table}\"", _db.SuperuserConnectionString)}");
        }

        var history = await _db.ScalarAsync<string>("SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\"", _db.SuperuserConnectionString);
        return string.Join('\n', schema, string.Join(',', counts), history);
    }
}
