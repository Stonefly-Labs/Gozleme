using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0004 US5 (FR-013, FR-013a, maintainer Q3 = B; research R3–R5): connected directly to PostgreSQL
/// with the application skipped, a member cannot escalate or perform admin-only writes, identity columns
/// are immutable, the last admin cannot be removed or demoted, and the guard binds the owner and superuser
/// too. Every statement runs in a transaction that is rolled back.
/// </summary>
public sealed class RbacIsolationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Refused = PostgresErrorCodes.InsufficientPrivilege;

    private GozlemeApiFactory _factory = null!;
    private HttpClient _alice = null!;
    private HttpClient _carol = null!;
    private HttpClient _dave = null!;
    private HttpClient _bob = null!;
    private Guid _aliceId;
    private Guid _carolId;
    private Guid _daveId;
    private Guid _bobId;
    private Guid _orgA; // alice admin; carol and dave members
    private Guid _orgB; // bob admin
    private Guid _carolsInvitation;

    private GozlemeDatabase Db => fixture.Database;

    public async ValueTask InitializeAsync()
    {
        _factory = new GozlemeApiFactory(Db.AppConnectionString);
        (_alice, _) = await AuthClient.SignedInAsync(_factory);
        (_carol, _) = await AuthClient.SignedInAsync(_factory);
        (_dave, _) = await AuthClient.SignedInAsync(_factory);
        (_bob, _) = await AuthClient.SignedInAsync(_factory);
        _orgA = Guid.Parse(await OrgClient.CreateAsync(_alice, "Rbac A"));
        await OrgClient.JoinAsync(_alice, _carol, _orgA.ToString());
        await OrgClient.JoinAsync(_alice, _dave, _orgA.ToString());
        _orgB = Guid.Parse(await OrgClient.CreateAsync(_bob, "Rbac B"));
        (_aliceId, _carolId, _daveId, _bobId) = (await Id(_alice), await Id(_carol), await Id(_dave), await Id(_bob));
        _carolsInvitation = await Db.ScalarAsync<Guid>($"SELECT invitation_id FROM gozleme.memberships WHERE organization_id = '{_orgA}' AND user_id = '{_carolId}'");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { _alice, _carol, _dave, _bob })
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_member_cannot_escalate_or_perform_admin_only_writes()
    {
        var a = _orgA;
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET roles = '{{admin,member}}' WHERE organization_id = '{a}' AND user_id = '{_carolId}'"));
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET roles = '{{member,reviewer}}' WHERE organization_id = '{a}' AND user_id = '{_daveId}'"));
        Assert.Equal(Refused, await Fails(_carolId, $"DELETE FROM gozleme.memberships WHERE organization_id = '{a}' AND user_id = '{_daveId}'"));
        Assert.Equal(Refused, await Fails(_carolId, $"INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via) VALUES ('{a}', '{_bobId}', now(), 'invitation')"));
        Assert.Equal(0, await Rows(_carolId, $"UPDATE gozleme.organizations SET display_name = 'pwned' WHERE id = '{a}'"));
        Assert.Equal(Refused, await Fails(_carolId, $"INSERT INTO gozleme.invitations (id, organization_id, code_hash, created_by, created_at, expires_at) VALUES (gen_random_uuid(), '{a}', sha256('m'::bytea), '{_carolId}', now(), now() + interval '1 day')"));
    }

    [Fact]
    public async Task A_member_may_leave_and_select_but_never_rewrite_identity_columns()
    {
        var own = $"WHERE organization_id = '{_orgA}' AND user_id = '{_carolId}'";
        Assert.Equal(1, await Rows(_carolId, $"DELETE FROM gozleme.memberships {own}"));
        Assert.Equal(1, await Rows(_carolId, $"UPDATE gozleme.memberships SET selected_at = now() {own}"));
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET selected_at = now() WHERE organization_id = '{_orgA}' AND user_id = '{_daveId}'"));
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET joined_via = 'creator' {own}"));
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET joined_at = now() - interval '1 year' {own}"));
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET invitation_id = gen_random_uuid() {own}"));
        // GOZ-0004-IF-14: the provenance link changes only through the FK cascade, never by a write, even to NULL.
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET invitation_id = NULL {own}"));
        Assert.Equal(Refused, await Fails(_carolId, $"UPDATE gozleme.memberships SET invitation_id = NULL WHERE organization_id = '{_orgA}' AND user_id = '{_daveId}'"));
        Assert.Equal(Refused, await Fails(_aliceId, $"UPDATE gozleme.memberships SET invitation_id = NULL WHERE organization_id = '{_orgA}' AND user_id = '{_daveId}'"));
    }

    [Fact]
    public async Task An_admin_manages_its_own_organization_only()
    {
        var a = _orgA;
        Assert.Equal(1, await Rows(_aliceId, $"UPDATE gozleme.organizations SET display_name = 'Renamed' WHERE id = '{a}'"));
        Assert.Equal(1, await Rows(_aliceId, $"INSERT INTO gozleme.invitations (id, organization_id, code_hash, created_by, created_at, expires_at) VALUES (gen_random_uuid(), '{a}', sha256('a'::bytea), '{_aliceId}', now(), now() + interval '1 day')"));
        Assert.Equal(1, await Rows(_aliceId, $"INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via) VALUES ('{a}', '{_bobId}', now(), 'invitation')"));
        Assert.Equal(1, await Rows(_aliceId, $"DELETE FROM gozleme.memberships WHERE organization_id = '{a}' AND user_id = '{_daveId}'"));
        Assert.Equal(1, await Rows(_aliceId, $"UPDATE gozleme.memberships SET roles = '{{reviewer,member,admin,member}}' WHERE organization_id = '{a}' AND user_id = '{_carolId}'"));
        Assert.Equal(
            "{admin,member,reviewer}",
            await Scalar<string>(_aliceId, $"UPDATE gozleme.memberships SET roles = '{{reviewer,member,admin,member}}' WHERE organization_id = '{a}' AND user_id = '{_carolId}' RETURNING roles::text"));

        Assert.Equal(Refused, await Fails(_aliceId, $"UPDATE gozleme.memberships SET organization_id = '{_orgB}' WHERE organization_id = '{a}' AND user_id = '{_carolId}'"));
        Assert.Equal(0, await Rows(_aliceId, $"UPDATE gozleme.memberships SET roles = '{{member}}' WHERE organization_id = '{_orgB}'"));
        Assert.Equal(0, await Rows(_aliceId, $"UPDATE gozleme.organizations SET display_name = 'pwned' WHERE id = '{_orgB}'"));
        Assert.Equal(0, await Rows(_aliceId, $"DELETE FROM gozleme.memberships WHERE organization_id = '{_orgB}'"));
    }

    [Fact]
    public async Task The_last_admin_and_role_set_rules_hold_at_the_database()
    {
        var self = $"WHERE organization_id = '{_orgA}' AND user_id = '{_aliceId}'";
        Assert.Equal(Refused, await Fails(_aliceId, $"UPDATE gozleme.memberships SET roles = '{{member}}' {self}"));
        Assert.Equal(Refused, await Fails(_aliceId, $"DELETE FROM gozleme.memberships {self}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await Fails(_aliceId, $"UPDATE gozleme.memberships SET roles = '{{admin,member,owner}}' WHERE organization_id = '{_orgA}' AND user_id = '{_carolId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await Fails(_aliceId, $"UPDATE gozleme.memberships SET roles = '{{admin}}' WHERE organization_id = '{_orgA}' AND user_id = '{_carolId}'"));
    }

    [Fact]
    public async Task Joining_paths_cannot_choose_extra_roles()
    {
        // A self-insert by invitation may only ever hold {member} (memberships_insert), whatever else it claims.
        Assert.Equal(Refused, await Fails(_bobId, $"INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via, roles) VALUES ('{_orgA}', '{_bobId}', now(), 'invitation', '{{admin,member}}')"));

        // A creator may hold admin and member only.
        var fresh = Guid.NewGuid();
        const string extra = "'{admin,approver,member}'";
        Assert.Equal(Refused, await Fails(_bobId, $"""
            INSERT INTO gozleme.organizations (id, display_name, created_at, created_by, updated_at) VALUES ('{fresh}', 'New', now(), '{_bobId}', now());
            INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via, roles) VALUES ('{fresh}', '{_bobId}', now(), 'creator', {extra})
            """));
    }

    [Fact]
    public async Task The_guard_binds_the_owner_and_the_superuser()
    {
        var change = $"UPDATE gozleme.memberships SET roles = '{{admin,member}}' WHERE organization_id = '{_orgA}' AND user_id = '{_carolId}'";
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Db.MigratorConnectionString, null, $"WITH c AS ({change} RETURNING 1) SELECT count(*) FROM c"));

        const string helper = "SELECT set_config('gozleme.rls_helper', 'on', true)";
        Assert.Equal(Refused, await Code(() => GozlemeDatabase.InTransactionAsync<long>(Db.MigratorConnectionString, helper, change)));
        Assert.Equal(Refused, await Code(() => GozlemeDatabase.InTransactionAsync<long>(Db.MigratorConnectionString, helper, $"DELETE FROM gozleme.memberships WHERE organization_id = '{_orgA}' AND user_id = '{_daveId}'")));
        Assert.Equal(Refused, await Code(() => GozlemeDatabase.InTransactionAsync<long>(Db.SuperuserConnectionString, null, change)));

        // Deleting an invitation still nulls the membership's reference (an FK action, not a member's write).
        Assert.Equal(
            0L,
            await GozlemeDatabase.InTransactionAsync<long>(
                Db.AppConnectionString, GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_aliceId)),
                $"""
                WITH d AS (DELETE FROM gozleme.invitations WHERE id = '{_carolsInvitation}' RETURNING 1)
                SELECT (SELECT count(*) FROM d) - 1
                """));
    }

    [Fact]
    public async Task Concurrent_mutual_demotion_leaves_one_admin_and_a_demoted_admin_cannot_act()
    {
        await OrgClient.SetRolesAsync(_alice, _orgA.ToString(), _carolId.ToString(), "admin");
        var demoteCarol = $"UPDATE gozleme.memberships SET roles = '{{member}}' WHERE organization_id = '{_orgA}' AND user_id = '{_carolId}'";
        var demoteAlice = $"UPDATE gozleme.memberships SET roles = '{{member}}' WHERE organization_id = '{_orgA}' AND user_id = '{_aliceId}'";
        var removeDave = $"DELETE FROM gozleme.memberships WHERE organization_id = '{_orgA}' AND user_id = '{_daveId}'";

        foreach (var second in new[] { demoteAlice, removeDave })
        {
            var outcome = await Race(_aliceId, demoteCarol, _carolId, second);
            Assert.Equal(Refused, outcome); // carol's statement waited for the lock, then saw her demotion
            Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.memberships WHERE organization_id = '{_orgA}' AND 'admin' = ANY (roles)"));
            Assert.Equal(3, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.memberships WHERE organization_id = '{_orgA}'"));
            await OrgClient.SetRolesAsync(_alice, _orgA.ToString(), _carolId.ToString(), "admin");
        }
    }

    /// <summary>First holds the per-organization lock while second waits; first commits; returns second's SQLSTATE (or "ok").</summary>
    private async Task<string> Race(Guid firstAccount, string first, Guid secondAccount, string second)
    {
        // Named, so Npgsql metrics label the pool by name and never by connection string.
        await using var dataSource = new NpgsqlDataSourceBuilder(Db.AppConnectionString) { Name = "integration-test-fixture" }.Build();
        await using var one = await dataSource.OpenConnectionAsync();
        await using var two = await dataSource.OpenConnectionAsync();
        await using var t1 = await one.BeginTransactionAsync();
        await using var t2 = await two.BeginTransactionAsync();
        await Exec(one, t1, GozlemeDatabase.Establish(GozlemeDatabase.SignContext(firstAccount)));
        await Exec(two, t2, GozlemeDatabase.Establish(GozlemeDatabase.SignContext(secondAccount)));
        await Exec(one, t1, first);

        var waiting = Exec(two, t2, second);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted); // blocked on the organization's membership lock
        await t1.CommitAsync();
        try
        {
            await waiting;
            await t2.CommitAsync();
            return "ok";
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static async Task Exec(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> Rows(Guid account, string sql) =>
        await Scalar<long>(account, $"WITH c AS ({sql} RETURNING 1) SELECT count(*) FROM c");

    private Task<T?> Scalar<T>(Guid account, string sql) =>
        GozlemeDatabase.InTransactionAsync<T>(Db.AppConnectionString, GozlemeDatabase.Establish(GozlemeDatabase.SignContext(account)), sql);

    private Task<string> Fails(Guid account, string sql) => Code(() => Scalar<object>(account, sql));

    private static async Task<string> Code<T>(Func<Task<T>> run)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(run);
        return ex.SqlState;
    }

    private static async Task<Guid> Id(HttpClient client) => Guid.Parse(await OrgClient.UserIdAsync(client));
}
