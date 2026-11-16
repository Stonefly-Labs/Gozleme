using System.Net;
using Gozleme.Api.Identity;
using Gozleme.Api.IntegrationTests.Infrastructure;

namespace Gozleme.Api.IntegrationTests;

/// <summary>US4 (FR-006, FR-024, FR-026, FR-027, FR-035): operator commands and the registration switch.</summary>
public sealed class OperatorCommandTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string NewPassword = "operator-chosen-passphrase";

    private GozlemeApiFactory _factory = null!;

    private GozlemeDatabase Db => fixture.Database;

    public ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reset_password_replaces_the_password_clears_lockout_and_ends_sessions(bool viaStdin)
    {
        var (session, email) = await AuthClient.SignedInAsync(_factory);
        using var _ = session;
        await LockAsync(email);
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, NewPassword + "\n", TestContext.Current.CancellationToken);
        try
        {
            var (code, stdout, stderr) = await RunAsync(
                "reset-password",
                viaStdin ? ["--email", email, "--password-stdin"] : ["--email", email, "--password-file", file],
                stdin: viaStdin ? NewPassword + "\n" : string.Empty);

            Assert.Equal(OperatorCommands.Ok, code);
            AssertNoPersonalData(email, stdout + stderr);
            Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.GetAsync(session, "/api/v1/auth/session")).StatusCode);
            using var client = AuthClient.Create(_factory);
            Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.SignInAsync(client, email, AuthClient.Password)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(client, email, NewPassword)).StatusCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Reset_password_enforces_the_policy_without_touching_the_account()
    {
        var (session, email) = await AuthClient.SignedInAsync(_factory);
        using var _ = session;
        var (code, _, stderr) = await RunAsync("reset-password", ["--email", email, "--password-stdin"], stdin: "password1234");
        Assert.Equal(OperatorCommands.PolicyFailure, code);
        AssertNoPersonalData(email, stderr);
        using var client = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(client, email, AuthClient.Password)).StatusCode);
    }

    [Fact]
    public async Task Password_arguments_and_unknown_accounts_are_refused()
    {
        Assert.Equal(OperatorCommands.Usage, (await RunAsync("reset-password", ["--email", "x@it.gozleme.test", "--password", NewPassword])).Code);
        Assert.Equal(OperatorCommands.Usage, (await RunAsync("reset-password", ["--email", "x@it.gozleme.test", $"--password={NewPassword}"])).Code);
        Assert.Equal(OperatorCommands.Usage, (await RunAsync("unlock", [])).Code);

        var ghost = AuthClient.NewEmail("ghost");
        Assert.Equal(OperatorCommands.UnknownAccount, (await RunAsync("reset-password", ["--email", ghost, "--password-stdin"], stdin: NewPassword)).Code);
        Assert.Equal(OperatorCommands.UnknownAccount, (await RunAsync("unlock", ["--email", ghost])).Code);
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.users WHERE email = '{ghost}'"));
    }

    [Fact]
    public async Task Unlock_lets_a_locked_account_sign_in_immediately()
    {
        var (session, email) = await AuthClient.SignedInAsync(_factory);
        session.Dispose();
        await LockAsync(email);
        using var client = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthClient.SignInAsync(client, email, AuthClient.Password)).StatusCode);

        var (code, stdout, _) = await RunAsync("unlock", ["--email", email]);
        Assert.Equal(OperatorCommands.Ok, code);
        AssertNoPersonalData(email, stdout);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(client, email, AuthClient.Password)).StatusCode);
    }

    [Fact]
    public async Task Seed_is_deterministic_and_idempotent()
    {
        var settings = $"--{OperatorCommands.DemoPasswordKey}=it-demo-seed-passphrase";
        Assert.Equal(OperatorCommands.Ok, (await RunAsync("seed", [settings])).Code);
        var ids = await Db.ScalarAsync<string>("SELECT string_agg(id::text, ',' ORDER BY email) FROM gozleme.users WHERE email LIKE '%@demo.gozleme.test'");
        await LockAsync("lockout@demo.gozleme.test");

        Assert.Equal(OperatorCommands.Ok, (await RunAsync("seed", [settings])).Code);
        Assert.Equal(ids, await Db.ScalarAsync<string>("SELECT string_agg(id::text, ',' ORDER BY email) FROM gozleme.users WHERE email LIKE '%@demo.gozleme.test'"));
        Assert.Equal(OperatorCommands.DemoAccounts.Length, ids.Split(',').Length);
        using var client = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(client, "lockout@demo.gozleme.test", "it-demo-seed-passphrase")).StatusCode);
        Assert.Equal(OperatorCommands.Usage, (await RunAsync("seed", [])).Code);
    }

    [Fact]
    public async Task Seed_restores_the_demo_organization_to_exactly_alice_and_carol_with_no_invitations()
    {
        var settings = $"--{OperatorCommands.DemoPasswordKey}=it-demo-seed-passphrase";
        var (first, stdout, _) = await RunAsync("seed", [settings]);
        Assert.Equal(OperatorCommands.Ok, first);
        Assert.Contains($"Seeded demo organization {OperatorCommands.DemoOrganizationId}", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorCommands.DemoOrganizationName, stdout, StringComparison.Ordinal);

        // Drift the fixture the way a demo run does: rename, add bob, remove carol, leave an invitation.
        using var alice = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(alice, "alice@demo.gozleme.test", "it-demo-seed-passphrase")).StatusCode);
        using var bob = AuthClient.Create(_factory);
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(bob, "bob@demo.gozleme.test", "it-demo-seed-passphrase")).StatusCode);
        var org = OperatorCommands.DemoOrganizationId.ToString();
        (await OrgClient.RenameAsync(alice, org, "Drifted")).Dispose();
        await OrgClient.JoinAsync(alice, bob, org);
        var carol = (await OrgClient.MembersAsync(alice, org)).Single(m => m.GetProperty("email").GetString() == "carol@demo.gozleme.test").GetProperty("userId").GetString()!;
        (await OrgClient.RemoveAsync(alice, org, carol)).Dispose();
        await OrgClient.InviteAsync(alice, org);

        Assert.Equal(OperatorCommands.Ok, (await RunAsync("seed", [settings])).Code);
        // Seeding resets the demo passwords, which ends their sessions; sign in afresh.
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(alice, "alice@demo.gozleme.test", "it-demo-seed-passphrase")).StatusCode);
        using var get = await AuthClient.GetAsync(alice, $"{OrgClient.Base}/{org}");
        Assert.Equal(OperatorCommands.DemoOrganizationName, (await AuthClient.JsonAsync(get)).GetProperty("displayName").GetString());
        var members = await OrgClient.MembersAsync(alice, org);
        Assert.Equal(["alice@demo.gozleme.test", "carol@demo.gozleme.test"], members.Select(m => m.GetProperty("email").GetString()));
        Assert.Equal(["creator", "invitation"], members.Select(m => m.GetProperty("joinedVia").GetString()));
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.invitations WHERE organization_id = '{org}'"));
    }

    [Fact]
    public async Task Closed_registration_refuses_new_accounts_but_existing_accounts_sign_in()
    {
        var (owner, email) = await AuthClient.SignedInAsync(_factory);
        owner.Dispose();
        await using var closed = new GozlemeApiFactory(Db.AppConnectionString, new Dictionary<string, string?> { ["Gozleme:Auth:RegistrationEnabled"] = "false" });
        using var client = AuthClient.Create(closed);

        using var options = await AuthClient.GetAsync(client, "/api/v1/auth/options");
        var body = await AuthClient.JsonAsync(options);
        Assert.False(body.GetProperty("registrationEnabled").GetBoolean());
        Assert.Equal(0, body.GetProperty("externalProviders").GetArrayLength());

        var newcomer = AuthClient.NewEmail("newcomer");
        using var refused = await AuthClient.RegisterAsync(client, newcomer, AuthClient.Password);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("""{"error":"registration_closed"}""", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.users WHERE email = '{newcomer}'"));
        Assert.Equal(HttpStatusCode.OK, (await AuthClient.SignInAsync(client, email, AuthClient.Password)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("POST", "/api/v1/auth/reset-password")]
    [InlineData("POST", "/api/v1/auth/unlock")]
    [InlineData("POST", "/api/v1/admin/users/unlock")]
    public async Task No_http_admin_surface_exists(string method, string path)
    {
        var (client, _) = await AuthClient.SignedInAsync(_factory);
        using var _ = client;
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task LockAsync(string email) =>
        await GozlemeDatabase.ExecuteAsync($"UPDATE gozleme.users SET lockout_end = now() + interval '15 minutes', access_failed_count = 0 WHERE email = '{email}'", Db.AppConnectionString);

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(string command, string[] args, string stdin = "")
    {
        using var input = new StringReader(stdin);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await OperatorCommands.RunAsync(command, [.. args, $"--ConnectionStrings:Gozleme={Db.AppConnectionString}", $"--Gozleme:Rls:ContextKey={GozlemeDatabase.ContextKey}"], input, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static void AssertNoPersonalData(string email, string output)
    {
        Assert.DoesNotContain(email, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(NewPassword, output, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthClient.Password, output, StringComparison.Ordinal);
    }
}
