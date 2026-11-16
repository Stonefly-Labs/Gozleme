using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0003 US2 (FR-001–FR-005, FR-014, FR-015, SC-001): connected directly to PostgreSQL, the ordinary
/// app role and the owner login see and change only rows of organizations the verified account
/// belongs to, with the application check skipped entirely. Ground truth comes from the superuser.
/// </summary>
public sealed class RlsIsolationTests(PgvectorDatabaseFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Tables = ["organizations", "memberships", "invitations"];

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

    public static TheoryData<string> Roles => new() { "app", "owner" };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Without_a_context_no_tenant_row_is_visible_to_either_role(string role)
    {
        foreach (var table in Tables)
        {
            Assert.True(await TwoTenants.TruthAsync(Db, table, _t.OrgA) > 0);
            Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Connection(role), null, $"SELECT count(*) FROM gozleme.{table}"));
        }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task A_valid_context_sees_exactly_its_own_organizations_rows(string role)
    {
        var alice = GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_t.AliceId));
        Assert.Equal($"{_t.OrgA}", await GozlemeDatabase.InTransactionAsync<string>(Connection(role), alice, "SELECT string_agg(id::text, ',') FROM gozleme.organizations"));
        foreach (var table in new[] { "memberships", "invitations" })
        {
            var visible = await GozlemeDatabase.InTransactionAsync<long>(Connection(role), alice, $"SELECT count(*) FROM gozleme.{table}");
            Assert.Equal(await TwoTenants.TruthAsync(Db, table, _t.OrgA), visible);
            Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Connection(role), alice, $"SELECT count(*) FROM gozleme.{table} WHERE organization_id = $1", _t.OrgB));
        }

        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(Connection(role), alice, "SELECT count(*) FROM gozleme.organizations WHERE id = $1", _t.OrgB));
    }

    [Fact]
    public async Task Writes_into_or_towards_another_organization_are_refused()
    {
        var alice = GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_t.AliceId));
        var app = Db.AppConnectionString;

        // UPDATE and DELETE see no B rows, so they change nothing.
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(app, alice, "WITH c AS (UPDATE gozleme.organizations SET display_name = 'pwned' WHERE id = $1 RETURNING 1) SELECT count(*) FROM c", _t.OrgB));
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(app, alice, "WITH c AS (DELETE FROM gozleme.memberships WHERE organization_id = $1 RETURNING 1) SELECT count(*) FROM c", _t.OrgB));
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(app, alice, "WITH c AS (UPDATE gozleme.invitations SET revoked_at = now() WHERE organization_id = $1 RETURNING 1) SELECT count(*) FROM c", _t.OrgB));
        Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(app, alice, "WITH c AS (DELETE FROM gozleme.invitations WHERE organization_id = $1 RETURNING 1) SELECT count(*) FROM c", _t.OrgB));

        // INSERT into B, and moving A's rows to B, violate the policy check.
        string[] refused =
        [
            $"INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via) VALUES ('{_t.OrgB}', '{_t.AliceId}', now(), 'invitation')",
            $"INSERT INTO gozleme.invitations (id, organization_id, code_hash, created_by, created_at, expires_at) VALUES (gen_random_uuid(), '{_t.OrgB}', sha256('x'::bytea), '{_t.AliceId}', now(), now() + interval '1 day')",
            $"UPDATE gozleme.memberships SET organization_id = '{_t.OrgB}' WHERE organization_id = '{_t.OrgA}' AND user_id = '{_t.AliceId}'",
            $"UPDATE gozleme.invitations SET organization_id = '{_t.OrgB}' WHERE organization_id = '{_t.OrgA}'",
            $"INSERT INTO gozleme.organizations (id, display_name, created_at, created_by, updated_at) VALUES (gen_random_uuid(), 'Forged', now(), '{_t.BobId}', now())",
            "DELETE FROM gozleme.organizations",
        ];
        foreach (var sql in refused)
        {
            if (sql.StartsWith("DELETE FROM gozleme.organizations", StringComparison.Ordinal))
            {
                // No DELETE policy: nothing is ever deleted, even in alice's own organization.
                Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(app, alice, $"WITH c AS ({sql} RETURNING 1) SELECT count(*) FROM c"));
                continue;
            }

            var ex = await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(app, alice, sql));
            Assert.True(TwoTenants.IsRefusal(ex), $"{ex.SqlState} for: {sql}");
        }

        Assert.Equal("Tenant B", await Db.ScalarAsync<string>($"SELECT display_name FROM gozleme.organizations WHERE id = '{_t.OrgB}'"));
        Assert.Equal(1, await TwoTenants.TruthAsync(Db, "memberships", _t.OrgB));
        Assert.Equal(1, await TwoTenants.TruthAsync(Db, "invitations", _t.OrgB));
    }

    public static TheoryData<string, string> InvalidContexts
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var role in new[] { "app", "owner" })
            {
                foreach (var variant in new[] { "none", "forged", "tampered", "expired" })
                {
                    data.Add(role, variant);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidContexts))]
    public async Task Without_a_valid_context_neither_role_can_write_any_tenant_row(string role, string variant)
    {
        var valid = GozlemeDatabase.SignContext(_t.AliceId);
        var token = variant switch
        {
            "none" => null,
            "forged" => _t.AliceId.ToString(),
            "tampered" => valid.Replace(_t.AliceId.ToString(), _t.BobId.ToString(), StringComparison.Ordinal),
            _ => GozlemeDatabase.SignContext(_t.AliceId, DateTimeOffset.UtcNow.AddSeconds(-1)),
        };

        // Set directly, so the policies (not establish_context) judge the value. Targets are alice's own
        // organization A, which a valid alice context could change.
        var setup = token is null ? null : $"SELECT set_config('gozleme.account_context', '{token}', true)";
        var connection = Connection(role);
        var members = await TwoTenants.TruthAsync(Db, "memberships", _t.OrgA);
        var invitations = await TwoTenants.TruthAsync(Db, "invitations", _t.OrgA);
        string[] noRows =
        [
            $"UPDATE gozleme.organizations SET display_name = 'pwned' WHERE id = '{_t.OrgA}'",
            $"UPDATE gozleme.memberships SET joined_via = 'invitation' WHERE organization_id = '{_t.OrgA}'",
            $"DELETE FROM gozleme.memberships WHERE organization_id = '{_t.OrgA}'",
            $"UPDATE gozleme.invitations SET revoked_at = now() WHERE organization_id = '{_t.OrgA}'",
            $"DELETE FROM gozleme.invitations WHERE organization_id = '{_t.OrgA}'",
            $"DELETE FROM gozleme.organizations WHERE id = '{_t.OrgA}'",
        ];
        foreach (var sql in noRows)
        {
            Assert.Equal(0, await GozlemeDatabase.InTransactionAsync<long>(connection, setup, $"WITH c AS ({sql} RETURNING 1) SELECT count(*) FROM c"));
        }

        string[] refused =
        [
            $"INSERT INTO gozleme.organizations (id, display_name, created_at, created_by, updated_at) VALUES (gen_random_uuid(), 'Forged', now(), '{_t.AliceId}', now())",
            $"INSERT INTO gozleme.memberships (organization_id, user_id, joined_at, joined_via) VALUES ('{_t.OrgA}', '{_t.BobId}', now(), 'invitation')",
            $"INSERT INTO gozleme.invitations (id, organization_id, code_hash, created_by, created_at, expires_at) VALUES (gen_random_uuid(), '{_t.OrgA}', sha256('y'::bytea), '{_t.AliceId}', now(), now() + interval '1 day')",
        ];
        foreach (var sql in refused)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(connection, setup, sql));
            Assert.True(TwoTenants.IsRefusal(ex), $"{role}/{variant}: {ex.SqlState} for: {sql}");
        }

        Assert.Equal("Tenant A", await Db.ScalarAsync<string>($"SELECT display_name FROM gozleme.organizations WHERE id = '{_t.OrgA}'"));
        Assert.Equal(members, await TwoTenants.TruthAsync(Db, "memberships", _t.OrgA));
        Assert.Equal(invitations, await TwoTenants.TruthAsync(Db, "invitations", _t.OrgA));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM gozleme.organizations WHERE display_name = 'Forged'"));
    }

    [Fact]
    public async Task The_app_role_cannot_reach_the_key_replace_helpers_or_switch_rls_off()
    {
        string[] denied =
        [
            "SELECT count(*) FROM gozleme_private.context_keys",
            "SELECT (gozleme_private.verify_context('x')).status",
            "CREATE OR REPLACE FUNCTION gozleme.rls_internal() RETURNS boolean LANGUAGE sql AS 'SELECT true'",
            "ALTER TABLE gozleme.organizations NO FORCE ROW LEVEL SECURITY",
            "ALTER TABLE gozleme.memberships DISABLE ROW LEVEL SECURITY",
            "DROP POLICY memberships_select ON gozleme.memberships",
            "ALTER FUNCTION gozleme.current_account_id() OWNER TO gozleme_app",
        ];
        foreach (var sql in denied)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.InTransactionAsync<object>(Db.AppConnectionString, null, sql));
            Assert.True(ex.SqlState is PostgresErrorCodes.InsufficientPrivilege, $"{ex.SqlState} for: {sql}");
        }
    }

    [Fact]
    public async Task Foreign_key_cascades_still_run_under_forced_rls()
    {
        // carol is a member of A and created A's open invitation (GOZ-0002-IF-12).
        await GozlemeDatabase.ExecuteAsync($"DELETE FROM gozleme.users WHERE id = '{_t.CarolId}'", Db.AppConnectionString);
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.memberships WHERE user_id = '{_t.CarolId}'"));
        Assert.Equal(0, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.invitations WHERE created_by = '{_t.CarolId}'"));
        Assert.Equal(1, await Db.ScalarAsync<long>($"SELECT count(*) FROM gozleme.invitations WHERE organization_id = '{_t.OrgA}' AND created_by IS NULL"));
    }

    [Fact]
    public async Task The_unique_code_hash_index_still_serves_the_capability_lookup()
    {
        var plan = await GozlemeDatabase.WithConnectionAsync(Db.AppConnectionString, async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var setup = new NpgsqlCommand($"{GozlemeDatabase.Establish(GozlemeDatabase.SignContext(_t.AliceId))}; SET LOCAL enable_seqscan = off", connection, transaction))
            {
                await setup.ExecuteNonQueryAsync();
            }

            await using var explain = new NpgsqlCommand("EXPLAIN SELECT id FROM gozleme.invitations WHERE code_hash = sha256('x'::bytea)", connection, transaction);
            await using var reader = await explain.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }

            return string.Join(' ', lines);
        });
        Assert.Contains("ix_invitations_code_hash", plan, StringComparison.Ordinal);
    }

    private string Connection(string role) => role == "owner" ? Db.MigratorConnectionString : Db.AppConnectionString;
}
