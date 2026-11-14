using System.Net;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// Stops and restarts a dedicated database container: degraded status, readiness 503,
/// liveness 200, automatic recovery, and no secret leakage in responses or logs.
/// </summary>
public sealed class DatabaseOutageTests : IAsyncLifetime
{
    private GozlemeDatabase _db = null!;
    private GozlemeApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await GozlemeDatabase.StartAsync();
        _factory = new GozlemeApiFactory(_db.AppConnectionString);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Outage_degrades_then_recovers_without_leaking_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var bodies = new List<string>();

        var healthy = await GetStatusAsync(bodies, ct);
        Assert.Equal("healthy", healthy.GetProperty("status").GetString());

        await _db.StopAsync();

        var degraded = await GetStatusAsync(bodies, ct);
        Assert.Equal("degraded", degraded.GetProperty("status").GetString());
        var db = Assert.Single(degraded.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("unavailable", db.GetProperty("status").GetString());
        Assert.Equal("Database unreachable", db.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, db.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, db.GetProperty("latencyMs").ValueKind);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusCodeAsync("/health/ready", bodies, ct));
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/health/live", bodies, ct));

        await _db.StartAgainAsync();

        var recovered = await WaitForAsync(async () => await GetStatusAsync(bodies, ct), s => s.GetProperty("status").GetString() == "healthy", ct);
        Assert.Equal("20261114000000_Foundation", recovered.GetProperty("dependencies")[0].GetProperty("schemaVersion").GetString());
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/health/ready", bodies, ct));

        string[] forbidden =
        [
            GozlemeDatabase.AppPassword,
            GozlemeDatabase.MigratorPassword,
            GozlemeDatabase.SuperuserPassword,
            "Password=",
            "Username=",
            "Host=",
            "127.0.0.1",
            "gozleme_app",
            "   at ", // stack trace frames
        ];
        var logs = string.Join('\n', _factory.Logs);
        foreach (var secret in forbidden)
        {
            Assert.DoesNotContain(bodies, b => b.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }

        Assert.Contains("Database probe failed", logs, StringComparison.Ordinal);
    }

    private async Task<JsonElement> GetStatusAsync(List<string> bodies, CancellationToken ct)
    {
        var body = await _client.GetStringAsync(new Uri("/api/v1/status", UriKind.Relative), ct);
        bodies.Add(body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpStatusCode> StatusCodeAsync(string path, List<string> bodies, CancellationToken ct)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative), ct);
        bodies.Add(await response.Content.ReadAsStringAsync(ct));
        return response.StatusCode;
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> probe, Func<T, bool> done, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await probe();
            if (done(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(500, ct);
        }
    }
}
