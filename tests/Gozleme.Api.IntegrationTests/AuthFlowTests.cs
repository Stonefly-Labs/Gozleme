using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US1 (FR-001–FR-013, FR-015): registration, sign-in, session cookie and session lifetime.</summary>
public sealed class AuthFlowTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
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
    public async Task Register_signs_in_with_a_hardened_session_cookie()
    {
        using var client = AuthClient.Create(_factory);
        var email = AuthClient.NewEmail();
        using var response = await AuthClient.RegisterAsync(client, email, AuthClient.Password);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await AuthClient.JsonAsync(response);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.False(body.GetProperty("emailVerified").GetBoolean());
        Assert.True(Guid.TryParse(body.GetProperty("userId").GetString(), out _));

        var cookie = AuthClient.SessionSetCookie(response)!.ToLowerInvariant();
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("expires=", cookie, StringComparison.Ordinal); // browser-session cookie; lifetime is server-side

        using var session = await AuthClient.GetAsync(client, "/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(email, (await AuthClient.JsonAsync(session)).GetProperty("email").GetString());
        Assert.False(await Db.ScalarAsync<bool>($"SELECT email_confirmed FROM gozleme.users WHERE email = '{email}'"));
        Assert.StartsWith("AQAAAA", await Db.ScalarAsync<string>($"SELECT password_hash FROM gozleme.users WHERE email = '{email}'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cookie_is_not_secure_when_operator_disables_it_for_plain_http()
    {
        await using var factory = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Auth:SecureCookies"] = "false" });
        using var client = AuthClient.Create(factory);
        using var response = await AuthClient.RegisterAsync(client, AuthClient.NewEmail(), AuthClient.Password);
        Assert.DoesNotContain("secure", AuthClient.SessionSetCookie(response)!.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Email_is_trimmed_and_case_insensitive_and_unique()
    {
        var email = AuthClient.NewEmail("Mixed.Case");
        using (var client = AuthClient.Create(_factory))
        using (var registered = await AuthClient.RegisterAsync(client, $"  {email.ToUpperInvariant()} ", AuthClient.Password))
        {
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }

        using (var client = AuthClient.Create(_factory))
        using (var signedIn = await AuthClient.SignInAsync(client, email.ToLowerInvariant(), AuthClient.Password))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        using (var client = AuthClient.Create(_factory))
        using (var duplicate = await AuthClient.RegisterAsync(client, email.ToLowerInvariant(), "another-long-passphrase"))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Equal("""{"error":"registration_failed"}""", await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("not-an-email", AuthClient.Password, "email")]
    [InlineData("a\u0001b@it.gozleme.test", AuthClient.Password, "email")]
    [InlineData("nodot@localhost", AuthClient.Password, "email")]
    [InlineData(null, AuthClient.Password, "email")]
    [InlineData("{email}", "short-pass", "password")]
    [InlineData("{email}", "password1234", "password")]
    [InlineData("{email}", "{email}", "password")]
    [InlineData("{email}", null, "password")]
    public async Task Invalid_registration_is_rejected_with_field_errors(string? email, string? password, string field)
    {
        var real = AuthClient.NewEmail();
        email = email?.Replace("{email}", real, StringComparison.Ordinal);
        password = password?.Replace("{email}", real, StringComparison.Ordinal);
        using var client = AuthClient.Create(_factory);
        using var response = await AuthClient.RegisterAsync(client, email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await AuthClient.JsonAsync(response);
        Assert.Equal("validation_failed", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("fields").TryGetProperty(field, out _));
        Assert.Null(AuthClient.SessionSetCookie(response));
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.users WHERE email = '{real}'"));
    }

    [Fact]
    public async Task Oversized_and_malformed_input_is_a_client_error_never_a_server_error()
    {
        using var client = AuthClient.Create(_factory);
        using var longPassword = await AuthClient.RegisterAsync(client, AuthClient.NewEmail(), new string('x', 129));
        Assert.Equal(HttpStatusCode.BadRequest, longPassword.StatusCode);

        using var longEmail = await AuthClient.RegisterAsync(client, new string('a', 250) + "@it.gozleme.test", AuthClient.Password);
        Assert.Equal(HttpStatusCode.BadRequest, longEmail.StatusCode);

        using var huge = await client.PostAsync(
            new Uri("/api/v1/auth/sign-in", UriKind.Relative),
            new StringContent($$"""{"email":"x@it.gozleme.test","password":"{{new string('p', 20_000)}}"}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.InRange((int)huge.StatusCode, 400, 499);

        using var malformed = await client.PostAsync(
            new Uri("/api/v1/auth/sign-in", UriKind.Relative),
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Every_sign_in_issues_a_new_session_and_retires_the_previous_one()
    {
        var (client, email) = await AuthClient.SignedInAsync(_factory);
        using var _ = client;
        using var first = await AuthClient.SignInAsync(client, email, AuthClient.Password);
        using var second = await AuthClient.SignInAsync(client, email, AuthClient.Password);
        var firstPair = AuthClient.SessionCookiePair(first);
        var secondPair = AuthClient.SessionCookiePair(second);

        Assert.NotEqual(firstPair, secondPair);
        Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, firstPair));
        Assert.Equal(HttpStatusCode.OK, await AuthClient.ReplayAsync(_factory, secondPair));
    }

    [Fact]
    public async Task Session_survives_an_api_restart_through_the_database_key_ring()
    {
        string pair;
        await using (var before = new GozlemeApiFactory(Db.AppConnectionString))
        {
            using var client = AuthClient.Create(before);
            using var registered = await AuthClient.RegisterAsync(client, AuthClient.NewEmail(), AuthClient.Password);
            pair = AuthClient.SessionCookiePair(registered);
            Assert.Equal(HttpStatusCode.OK, await AuthClient.ReplayAsync(before, pair));
        }

        await using var after = new GozlemeApiFactory(Db.AppConnectionString);
        Assert.Equal(HttpStatusCode.OK, await AuthClient.ReplayAsync(after, pair));
        Assert.Equal(HttpStatusCode.OK, await AuthClient.ReplayAsync(after, pair, "/api/v1/status"));
    }

    [Fact]
    public async Task Session_rows_store_only_a_key_hash_and_expire_absolutely()
    {
        using var client = AuthClient.Create(_factory);
        var email = AuthClient.NewEmail();
        using var registered = await AuthClient.RegisterAsync(client, email, AuthClient.Password);
        var pair = AuthClient.SessionCookiePair(registered);
        var userId = (await AuthClient.JsonAsync(registered)).GetProperty("userId").GetString();

        var row = await Db.ScalarAsync<string>($"""
            SELECT octet_length(key_hash) || ',' || (expires_at - created_at BETWEEN interval '11 hours 59 minutes' AND interval '12 hours 1 minute')
            FROM gozleme.auth_sessions WHERE user_id = '{userId}'
            """);
        Assert.Equal("32,true", row);

        await GozlemeDatabase.ExecuteAsync($"UPDATE gozleme.auth_sessions SET expires_at = now() - interval '1 second' WHERE user_id = '{userId}'", Db.AppConnectionString);
        Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, pair));
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.auth_sessions WHERE user_id = '{userId}'"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("gozleme_session=")]
    [InlineData("gozleme_session=CfDJ8-not-a-real-ticket")]
    public async Task Forged_or_garbage_cookies_are_anonymous(string cookie)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, cookie));
    }
}
