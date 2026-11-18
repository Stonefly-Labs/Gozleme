using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0000 boundary (FR-012), narrowed by GOZ-0001 (FR-037, IF-01) and GOZ-0002 (FR-025, IF-10):
/// catalog, user listing and publishing still do not exist; identity and organizations own exactly
/// their own tables. GOZ-0006 (FR-021, FR-027) re-asserts them: manifest validation adds no table and no migration.
/// </summary>
public sealed class BootstrapBoundaryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("GET", "/api/v1/catalog")]
    [InlineData("GET", "/api/v1/capabilities")]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("GET", "/api/v1/search?q=mcp")]
    [InlineData("POST", "/api/v1/auth/login")] // the GOZ-0001 endpoint is /api/v1/auth/sign-in
    [InlineData("POST", "/api/v1/publish")]
    [InlineData("GET", "/login")]
    public async Task Product_endpoints_do_not_exist(string method, string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Manifest_validation_adds_only_its_rate_limit_counter()
    {
        // GOZ-0006 FR-017 as clarified (T099): validation stores no manifest, only its shared counter. GOZ-0007 R19
        // adds exactly the publication counter, the write-once revisions and their attestations (deliberate pin).
        var db = fixture.Database;
        Assert.Equal("manifest_publication_attempts,manifest_revisions,manifest_validation_attempts,revision_attestations", await db.ScalarAsync<string>(
            "SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_tables WHERE tablename ~ '(manifest|revision|projection)'"));
        foreach (var counter in new[] { "manifest_validation_attempts", "manifest_publication_attempts" })
        {
            Assert.Equal("account_id,window_start,attempts", await db.ScalarAsync<string>(
                $"SELECT string_agg(column_name, ',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema = 'gozleme' AND table_name = '{counter}'"));
        }

        Assert.Equal("20261121000000_ManifestRevisions", await db.ScalarAsync<string>("""SELECT max("MigrationId") FROM public."__EFMigrationsHistory" """));
    }

    [Fact]
    public async Task Gozleme_schema_holds_exactly_the_identity_and_organization_tables()
    {
        var db = fixture.Database;
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM pg_namespace WHERE nspname = 'gozleme'"));
        var tables = await db.ScalarAsync<string>("SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_tables WHERE schemaname = 'gozleme'");
        Assert.Equal("alias_reservations,auth_sessions,capabilities,data_protection_keys,handle_claims,invitations,manifest_publication_attempts,manifest_revisions,manifest_validation_attempts,memberships,organizations,publisher_registration_attempts,publisher_signing_keys,publishers,registry_origin,revision_attestations,signing_key_retirements,user_claims,user_logins,user_tokens,users", tables);
    }

    [Fact]
    public async Task Only_migration_history_and_the_context_key_tables_exist_outside_system_schemas()
    {
        // GOZ-0003 adds gozleme_private.context_keys, which gozleme_app cannot reach (RlsCatalogTests).
        var tables = await fixture.Database.ScalarAsync<string>("""
            SELECT coalesce(string_agg(schemaname || '.' || tablename, ',' ORDER BY schemaname, tablename), '')
            FROM pg_tables
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema', 'gozleme')
            """);
        Assert.Equal("gozleme_private.context_keys,public.__EFMigrationsHistory", tables);
    }
}
