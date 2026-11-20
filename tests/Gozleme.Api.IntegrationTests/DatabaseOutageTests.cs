using System.Net;
using System.Text.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// Stops and restarts a dedicated database container: readiness 503, liveness 200, automatic
/// recovery, and no secret leakage in responses or logs. GOZ-0001 (FR-021a, fail closed): while the
/// database is down a session cannot be verified, so protected, identity and organization endpoints answer
/// <c>503 {"error":"unavailable"}</c> without status data; the same session works again on recovery.
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
        (_client, _) = await AuthClient.SignedInAsync(_factory);
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

        using (var unavailable = await _client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), ct))
        {
            var body = await unavailable.Content.ReadAsStringAsync(ct);
            bodies.Add(body);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("""{"error":"unavailable"}""", body);
            Assert.True(unavailable.Headers.CacheControl?.NoStore);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusCodeAsync("/api/v1/auth/session", bodies, ct));

        // GOZ-0002 (FR-022): organization endpoints fail closed the same way.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusCodeAsync("/api/v1/organizations", bodies, ct));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusCodeAsync($"/api/v1/organizations/{Guid.NewGuid()}/members", bodies, ct));
        using (var redeem = await OrgClient.RedeemAsync(_client, "ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ"))
        {
            var body = await redeem.Content.ReadAsStringAsync(ct);
            bodies.Add(body);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, redeem.StatusCode);
            Assert.Equal("""{"error":"unavailable"}""", body);
        }

        using (var signIn = await AuthClient.SignInAsync(_client, AuthClient.NewEmail(), AuthClient.Password))
        {
            bodies.Add(await signIn.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, signIn.StatusCode);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusCodeAsync("/health/ready", bodies, ct));
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/health/live", bodies, ct));

        await _db.StartAgainAsync();

        // The same session is honoured again once it can be verified.
        var recovered = await WaitForAsync(async () => await TryGetStatusAsync(bodies, ct), s => s?.GetProperty("status").GetString() == "healthy", ct);
        Assert.NotNull(recovered);
        Assert.Equal("20261122000000_ChildIdentities", recovered.Value.GetProperty("dependencies")[0].GetProperty("schemaVersion").GetString());
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

    private async Task<JsonElement?> TryGetStatusAsync(List<string> bodies, CancellationToken ct)
    {
        using var response = await _client.GetAsync(new Uri("/api/v1/status", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        bodies.Add(body);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(body).RootElement.Clone() : null;
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
