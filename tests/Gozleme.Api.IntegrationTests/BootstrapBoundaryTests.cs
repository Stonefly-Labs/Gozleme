using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0000 boundary (FR-012), narrowed by GOZ-0001 (FR-037, IF-01): organizations, catalog, user
/// listing and publishing still do not exist; local identity now owns exactly its own tables.
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
    [InlineData("GET", "/api/v1/organizations")]
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
    public async Task Gozleme_schema_holds_exactly_the_identity_tables()
    {
        var db = fixture.Database;
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM pg_namespace WHERE nspname = 'gozleme'"));
        var tables = await db.ScalarAsync<string>("SELECT string_agg(tablename, ',' ORDER BY tablename) FROM pg_tables WHERE schemaname = 'gozleme'");
        Assert.Equal("auth_sessions,data_protection_keys,user_claims,user_logins,user_tokens,users", tables);
    }

    [Fact]
    public async Task Only_migration_history_table_exists_outside_system_schemas()
    {
        var tables = await fixture.Database.ScalarAsync<string>("""
            SELECT coalesce(string_agg(schemaname || '.' || tablename, ',' ORDER BY tablename), '')
            FROM pg_tables
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema', 'gozleme')
            """);
        Assert.Equal("public.__EFMigrationsHistory", tables);
    }
}
