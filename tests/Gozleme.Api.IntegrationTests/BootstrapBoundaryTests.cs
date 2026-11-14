using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0000 boundary (FR-012): product identity, organizations and catalog are not implemented
/// prematurely — no endpoints answer and no tables exist.
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
    [InlineData("POST", "/api/v1/auth/login")]
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
    public async Task Gozleme_schema_exists_with_no_tables()
    {
        var db = fixture.Database;
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM pg_namespace WHERE nspname = 'gozleme'"));
        Assert.Equal(0, await db.ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'gozleme'"));
    }

    [Fact]
    public async Task Only_migration_history_table_exists_outside_system_schemas()
    {
        var tables = await fixture.Database.ScalarAsync<string>("""
            SELECT coalesce(string_agg(schemaname || '.' || tablename, ',' ORDER BY tablename), '')
            FROM pg_tables
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
            """);
        Assert.Equal("public.__EFMigrationsHistory", tables);
    }
}
