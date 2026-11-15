using System.Net;
using System.Net.Http.Json;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US3 (FR-018, FR-019, SC-005): account lockout and per-client / global rate limits.</summary>
public sealed class LockoutAndRateLimitTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;

    private GozlemeDatabase Db => fixture.Database;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Five_failures_lock_the_account_even_against_the_correct_password_and_revoke_sessions()
    {
        var (victim, email) = await AuthClient.SignedInAsync(_factory);
        using var _ = victim;
        using var attacker = AuthClient.Create(_factory);
        for (var i = 1; i <= 4; i++)
        {
            (await AuthClient.SignInAsync(attacker, email, "wrong-password-attempt")).Dispose();
        }

        Assert.Equal(HttpStatusCode.OK, (await AuthClient.GetAsync(victim, "/api/v1/auth/session")).StatusCode);
        (await AuthClient.SignInAsync(attacker, email, "wrong-password-attempt")).Dispose(); // 5th: locks

        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.SignInAsync(attacker, email, AuthClient.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.GetAsync(victim, "/api/v1/auth/session")).StatusCode);
        var lockedFor = (double)await Db.ScalarAsync<decimal>($"SELECT extract(epoch FROM lockout_end - now()) FROM gozleme.users WHERE email = '{email}'");
        Assert.InRange(lockedFor, 14 * 60, 15 * 60 + 5);
    }

    [Fact]
    public async Task Lockout_expires_and_a_successful_sign_in_resets_the_failure_count()
    {
        var (owner, email) = await AuthClient.SignedInAsync(_factory);
        owner.Dispose();
        using var client = AuthClient.Create(_factory);
        for (var i = 0; i < 5; i++)
        {
            (await AuthClient.SignInAsync(client, email, "wrong-password-attempt")).Dispose();
        }

        await GozlemeDatabase.ExecuteAsync($"UPDATE gozleme.users SET lockout_end = now() - interval '1 second' WHERE email = '{email}'", Db.AppConnectionString);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(client, email, AuthClient.Password)).StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<int>($"SELECT access_failed_count FROM gozleme.users WHERE email = '{email}'"));
    }

    [Fact]
    public async Task Unknown_emails_never_create_accounts()
    {
        var email = AuthClient.NewEmail("ghost");
        using var client = AuthClient.Create(_factory);
        for (var i = 0; i < 6; i++)
        {
            (await AuthClient.SignInAsync(client, email, "wrong-password-attempt")).Dispose();
        }

        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.users WHERE email = '{email}'"));
    }

    [Fact]
    public async Task Per_client_limit_throttles_with_retry_after_and_trusts_forwarded_clients_only_from_trusted_proxies()
    {
        await using var factory = new GozlemeApiFactory(
            Db.AppConnectionString,
            new Dictionary<string, string?>
            {
                ["Gozleme:Auth:RateLimit:SignInPerMinute"] = "3",
                ["Gozleme:Network:TrustedProxies"] = "10.0.0.0/8",
            },
            TestPeerAddress.Register);

        // Behind the trusted web tier, distinct browser clients get independent budgets.
        var firstClient = await SignInsAsync(factory, peer: "10.1.1.1", forwardedFor: "203.0.113.5", count: 4);
        var secondClient = await SignInsAsync(factory, peer: "10.1.1.1", forwardedFor: "203.0.113.6", count: 3);
        Assert.Equal([401, 401, 401, 429], firstClient);
        Assert.Equal([401, 401, 401], secondClient);

        // An untrusted peer cannot pick its own identity: every spoofed address shares the peer's budget.
        var spoofed = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            spoofed.AddRange(await SignInsAsync(factory, peer: "198.51.100.7", forwardedFor: $"203.0.113.{100 + i}", count: 1));
        }

        Assert.Equal([401, 401, 401, 429], spoofed);

        using var client = AuthClient.Create(factory);
        using var request = SignInRequest("198.51.100.7", null);
        using var throttled = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(int.Parse(throttled.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("""{"error":"rate_limited"}""", await throttled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Global_ceiling_bounds_all_clients_of_an_instance()
    {
        await using var factory = new GozlemeApiFactory(
            Db.AppConnectionString,
            new Dictionary<string, string?>
            {
                ["Gozleme:Auth:RateLimit:GlobalPerMinute"] = "4",
                ["Gozleme:Network:TrustedProxies"] = "10.0.0.0/8",
            },
            TestPeerAddress.Register);

        var codes = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            codes.AddRange(await SignInsAsync(factory, peer: "10.1.1.1", forwardedFor: $"203.0.113.{i + 1}", count: 1));
        }

        Assert.Equal([401, 401, 401, 401, 429], codes);
    }

    [Fact]
    public async Task Out_of_bounds_settings_fail_fast_at_start_up()
    {
        await using var factory = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Auth:Lockout:MaxFailedAttempts"] = "1000" });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private static async Task<int[]> SignInsAsync(GozlemeApiFactory factory, string peer, string forwardedFor, int count)
    {
        using var client = AuthClient.Create(factory);
        var codes = new int[count];
        for (var i = 0; i < count; i++)
        {
            using var request = SignInRequest(peer, forwardedFor);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            codes[i] = (int)response.StatusCode;
        }

        return codes;
    }

    private static HttpRequestMessage SignInRequest(string peer, string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/auth/sign-in", UriKind.Relative))
        {
            Content = JsonContent.Create(new { email = AuthClient.NewEmail("rl"), password = "wrong-password-attempt" }),
        };
        request.Headers.Add(TestPeerAddress.Header, peer);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return request;
    }
}
