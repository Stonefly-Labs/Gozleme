using System.Net;
using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0003 US3, the stated boundary "never rely on caller-set session variables as trusted identity"
/// (FR-006–FR-011): forged, tampered, expired, future-dated and wrongly keyed contexts identify nobody;
/// a stray tenant setting grants nothing; contexts never outlive their transaction on a pooled
/// connection; a removal takes effect on the next statement although the token is still valid.
/// </summary>
public sealed class TenantContextBoundaryTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private GozlemeApiFactory _factory = null!;
    private TwoTenants _t = null!;

    private GozlemeDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        _t = await TwoTenants.CreateAsync(_factory);
    }

    public async ValueTask DisposeAsync()
    {
        _t.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Forged_tampered_expired_and_wrongly_keyed_contexts_identify_nobody()
    {
        var valid = GozlemeDatabase.SignContext(_t.AliceId);
        var parts = valid.Split('.');
        (string Token, string Status)[] variants =
        [
            ($"{_t.BobId}", "invalid"),                                                              // plain account id
            ($"v1.{_t.BobId}.{parts[2]}.{parts[3]}", "invalid"),                                     // account swapped, MAC kept
            ($"v1.{parts[1]}.{long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) + 1}.{parts[3]}", "invalid"), // expiry extended
            ($"{valid[..^1]}{(valid[^1] == '0' ? '1' : '0')}", "invalid"),                           // MAC bit flipped
            (GozlemeDatabase.SignContext(_t.AliceId, DateTimeOffset.UtcNow.AddSeconds(-1)), "expired"),
            (GozlemeDatabase.SignContext(_t.AliceId, DateTimeOffset.UtcNow.AddMinutes(30)), "expired"), // future-dated beyond 10 minutes
            (GozlemeDatabase.SignContext(_t.AliceId, key: "a-different-key-of-sufficient-length-01"), "invalid"),
            ("v2." + valid[3..], "invalid"),
            (string.Empty, "missing"),
        ];

        foreach (var (token, status) in variants)
        {
            Assert.Equal(status, await GozlemeDatabase.InTransactionAsync<string>(Db.AppConnectionString, null, "SELECT gozleme.establish_context($1)", token));

            // Set directly, bypassing establish_context: policies re-verify on every statement.
            var forged = $"SELECT set_config('gozleme.account_context', '{token}', true)";
            Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, forged, "SELECT count(*) FROM gozleme.organizations"));
            Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, forged, "SELECT count(*) FROM gozleme.memberships"));
            Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, forged, "SELECT count(*) FROM gozleme.invitations"));
        }

        Assert.Equal(1, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, $"SELECT set_config('gozleme.account_context', '{valid}', true)", "SELECT count(*) FROM gozleme.organizations"));
    }

    [Fact]
    public async Task A_stray_tenant_setting_beside_a_valid_context_grants_nothing()
    {
        var setup = $"""
            {GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_t.AliceId))};
            SELECT set_config('gozleme.tenant_id', '{_t.OrgB}', true), set_config('gozleme.organization_id', '{_t.OrgB}', true),
                   set_config('app.tenant_id', '{_t.OrgB}', true), set_config('gozleme.rls_internal', 'on', true),
                   set_config('gozleme.rls_helper', 'on', true)
            """;
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, setup, "SELECT count(*) FROM gozleme.organizations WHERE id = $1", _t.OrgB));
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, setup, "SELECT count(*) FROM gozleme.memberships WHERE organization_id = $1", _t.OrgB));
        Assert.False(await GozlemeDatabase.InTransactionAsync<bool>(Db.AppConnectionString, setup, "SELECT gozleme.rls_internal()"));
    }

    [Fact]
    public async Task An_invitation_capability_reveals_exactly_that_invitation_and_nothing_else()
    {
        var hash = await Db.ScalarAsync<byte[]>($"SELECT code_hash FROM gozleme.invitations WHERE organization_id = '{_t.OrgB}'");
        var setup = $"""
            {GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_t.AliceId))};
            SELECT set_config('gozleme.invitation_capability', '{Convert.ToHexStringLower(hash)}', true)
            """;
        Assert.Equal(1, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, setup, "SELECT count(*) FROM gozleme.invitations WHERE organization_id = $1", _t.OrgB));
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, setup, "SELECT count(*) FROM gozleme.organizations WHERE id = $1", _t.OrgB));
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, setup, "SELECT count(*) FROM gozleme.memberships WHERE organization_id = $1", _t.OrgB));

        // Without the code, guessing gives nothing, and the locking helper needs a known hash and a verified account.
        var alice = GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_t.AliceId));
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, alice, "SELECT count(*) FROM gozleme.invitations WHERE organization_id = $1", _t.OrgB));
        Assert.Null(await GozlemeDatabase.InTransactionAsync<Guid?>(Db.AppConnectionString, alice, "SELECT gozleme.lock_invited_organization(sha256('guess'::bytea))"));
        Assert.Null(await GozlemeDatabase.InTransactionAsync<Guid?>(Db.AppConnectionString, null, "SELECT gozleme.lock_invited_organization($1)", hash));
        Assert.Equal(_t.OrgB, await GozlemeDatabase.InTransactionAsync<Guid?>(Db.AppConnectionString, alice, "SELECT gozleme.lock_invited_organization($1)", hash));
    }

    [Fact]
    public async Task A_context_never_survives_its_transaction_on_a_reused_pooled_connection()
    {
        var ct = TestContext.Current.CancellationToken;

        // One physical connection, and no reset on return to the pool, so only transaction scope can clear it.
        var pooled = new NpgsqlConnectionStringBuilder(Db.AppConnectionString) { Pooling = true, MaxPoolSize = 1, NoResetOnClose = true }.ConnectionString;
        await using var dataSource = new NpgsqlDataSourceBuilder(pooled) { Name = "integration-test-fixture" }.Build();
        int backend;
        await using (var first = await dataSource.OpenConnectionAsync(ct))
        {
            backend = first.ProcessID;
            await using var transaction = await first.BeginTransactionAsync(ct);
            await using var establish = new NpgsqlCommand(GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_t.AliceId)), first, transaction);
            await establish.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await using var second = await dataSource.OpenConnectionAsync(ct);
        Assert.Equal(backend, second.ProcessID);
        await using var probe = new NpgsqlCommand("SELECT coalesce(current_setting('gozleme.account_context', true), '') || '|' || (SELECT count(*) FROM gozleme.organizations)", second);
        Assert.Equal("|0", await probe.ExecuteScalarAsync(ct));

        // Identity lookups need no tenant context on the same connection (GOZ-0001-IF-03).
        await using var identity = new NpgsqlCommand($"SELECT count(*) FROM gozleme.users WHERE id = '{_t.AliceId}'", second);
        Assert.Equal(1L, await identity.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Through_the_api_a_pooled_connection_never_carries_one_accounts_context_into_anothers_request()
    {
        var pooled = new NpgsqlConnectionStringBuilder(Db.AppConnectionString) { Pooling = true, MaxPoolSize = 1, NoResetOnClose = true }.ConnectionString;
        await using var factory = new GozlemeApiFactory(pooled);
        var (alice, _) = await AuthClient.SignedInAsync(factory);
        var (bob, _) = await AuthClient.SignedInAsync(factory);
        using var _a = alice;
        using var _b = bob;
        var orgA = await OrgClient.CreateAsync(alice, "Pooled A");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal([orgA], await OrgClient.IdsAsync(alice));
            Assert.Empty(await OrgClient.IdsAsync(bob));
            using var session = await AuthClient.GetAsync(bob, "/api/v1/auth/session");
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            using var other = await AuthClient.GetAsync(bob, $"{OrgClient.Base}/{orgA}");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }
    }

    [Fact]
    public async Task A_removal_takes_effect_on_the_next_statement_although_the_token_is_still_valid()
    {
        var carol = GozlemeDatabase.SignContext(_t.CarolId);
        var counts = await GozlemeDatabase.WithConnectionAsync(Db.AppConnectionString, async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var establish = new NpgsqlCommand(GozlemeDatabase.Establish(carol), connection, transaction))
            {
                await establish.ExecuteNonQueryAsync();
            }

            await using var count = new NpgsqlCommand($"SELECT count(*) FROM gozleme.organizations WHERE id = '{_t.OrgA}'", connection, transaction);
            var before = (long)(await count.ExecuteScalarAsync())!;
            await GozlemeDatabase.ExecuteAsync($"DELETE FROM gozleme.memberships WHERE organization_id = '{_t.OrgA}' AND user_id = '{_t.CarolId}'", Db.SuperuserConnectionString);
            var after = (long)(await count.ExecuteScalarAsync())!;
            return (before, after);
        });
        Assert.Equal((1L, 0L), counts);
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.AppConnectionString, GozlemeDatabase.Establish(carol), "SELECT count(*) FROM gozleme.invitations"));
    }

    [Fact]
    public async Task Caller_supplied_headers_and_query_values_never_choose_the_account_or_organization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{OrgClient.Base}?organizationId={_t.OrgB}&accountId={_t.BobId}", UriKind.Relative));
        request.Headers.Add("x-gozleme-organization", _t.OrgB.ToString());
        request.Headers.Add("x-gozleme-account", _t.BobId.ToString());
        request.Headers.Add("x-gozleme-tenant", _t.OrgB.ToString());
        using var list = await _t.Alice.SendAsync(request, ct);
        var ids = (await AuthClient.JsonAsync(list)).GetProperty("organizations").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToArray();
        Assert.Equal([_t.OrgA.ToString()], ids);

        using var direct = new HttpRequestMessage(HttpMethod.Get, new Uri($"{OrgClient.Base}/{_t.OrgB}", UriKind.Relative));
        direct.Headers.Add("x-gozleme-account", _t.BobId.ToString());
        using var response = await _t.Alice.SendAsync(direct, ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_forged_or_tampered_session_cookie_selects_no_account_and_no_tenant_data()
    {
        using var client = AuthClient.Create(_factory);
        var email = AuthClient.NewEmail();
        using var registered = await AuthClient.RegisterAsync(client, email, AuthClient.Password);
        var pair = AuthClient.SessionCookiePair(registered);
        Assert.Equal(HttpStatusCode.OK, await AuthClient.ReplayAsync(_factory, pair, OrgClient.Base));

        var value = pair[(AuthClient.CookieName.Length + 1)..];
        string[] forged =
        [
            $"{AuthClient.CookieName}={value[..^2]}{(value[^2] == 'A' ? 'B' : 'A')}{value[^1]}", // tampered
            $"{AuthClient.CookieName}={_t.BobId}",                                                 // an account id as the cookie
            $"{AuthClient.CookieName}={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"userId\":\"{_t.BobId}\"}}"))}",
        ];
        foreach (var cookie in forged)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, cookie, OrgClient.Base));
            Assert.Equal(HttpStatusCode.Unauthorized, await AuthClient.ReplayAsync(_factory, cookie, $"{OrgClient.Base}/{_t.OrgB}"));
        }
    }
}
