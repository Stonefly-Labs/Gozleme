using System.Net;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

public sealed class StatusEndpointTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(fixture.Database.AppConnectionString);
        (_client, _) = await AuthClient.SignedInAsync(_factory); // GOZ-0001: status requires a session
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_probes_are_healthy(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Status_rejects_anonymous_requests_without_status_data()
    {
        using var anonymous = AuthClient.Create(_factory);
        using var response = await AuthClient.GetAsync(anonymous, "/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.Location is not null, "API never redirects to a login page");
    }

    [Fact]
    public async Task Status_reports_healthy_database_schema_version_and_vector_capability()
    {
        using var response = await _client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.Equal(
            ["checkedAt", "contractVersion", "dependencies", "service", "status", "version"],
            root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("1", root.GetProperty("contractVersion").GetString());
        Assert.Equal("gozleme-api", root.GetProperty("service").GetString());
        Assert.Equal("0.0.0-it", root.GetProperty("version").GetString());
        Assert.Equal("healthy", root.GetProperty("status").GetString());

        var db = Assert.Single(root.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("database", db.GetProperty("name").GetString());
        Assert.Equal("healthy", db.GetProperty("status").GetString());
        Assert.True(db.GetProperty("required").GetBoolean());
        Assert.Equal("20261117000000_TenantRls", db.GetProperty("schemaVersion").GetString());
        Assert.Equal(JsonValueKind.Null, db.GetProperty("message").ValueKind);
        var vector = db.GetProperty("capabilities").GetProperty("vector");
        Assert.True(vector.GetProperty("available").GetBoolean());
        Assert.False(vector.GetProperty("enabled").GetBoolean());
    }
}
