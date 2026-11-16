using Gozleme.Api.IntegrationTests.Infrastructure;
using Gozleme.Api.Persistence;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

/// <summary>
/// GOZ-0003 US4 (FR-017–FR-019, SC-002): every table is classified and every tenant-owned table forces
/// RLS with policies. Each negative case changes the catalog as the owner inside a transaction that is
/// rolled back, so the shared database is never altered.
/// </summary>
public sealed class RlsCatalogTests(PgvectorDatabaseFixture fixture)
{
    private GozlemeDatabase Db => fixture.Database;

    [Fact]
    public async Task The_migrated_database_has_no_violations()
    {
        var violations = await GozlemeDatabase.WithConnectionAsync(Db.AppConnectionString, c => RlsCatalog.ViolationsAsync(c));
        Assert.Empty(violations);
    }

    [Fact]
    public void Identity_tables_are_global_and_tenant_tables_are_disjoint_from_them()
    {
        Assert.Equal(["auth_sessions", "data_protection_keys", "user_claims", "user_logins", "user_tokens", "users"], TableClassification.GlobalIdentity.Order(StringComparer.Ordinal));
        Assert.Equal(["invitations", "memberships", "organizations"], TableClassification.TenantOwned.Order(StringComparer.Ordinal));
        Assert.Empty(TableClassification.SharedPublic);
    }

    public static TheoryData<string, string> Violations => new()
    {
        { "CREATE TABLE gozleme.rogue (id int)", "gozleme.rogue is not classified" },
        { "ALTER TABLE gozleme.memberships DISABLE ROW LEVEL SECURITY", "gozleme.memberships is tenant-owned but row-level security is not enabled" },
        { "ALTER TABLE gozleme.invitations NO FORCE ROW LEVEL SECURITY", "gozleme.invitations is tenant-owned but row-level security is not forced" },
        {
            "DROP POLICY organizations_select ON gozleme.organizations; DROP POLICY organizations_insert ON gozleme.organizations; DROP POLICY organizations_update ON gozleme.organizations",
            "gozleme.organizations is tenant-owned but has no policy"
        },
        { "CREATE FUNCTION gozleme.rogue() RETURNS int LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS 'SELECT 1'", "gozleme.rogue is SECURITY DEFINER but not a pinned helper" },
        { "CREATE OR REPLACE FUNCTION gozleme.lock_invited_organization(code_hash bytea) RETURNS uuid LANGUAGE sql SECURITY DEFINER AS 'SELECT NULL::uuid'", "gozleme.lock_invited_organization is SECURITY DEFINER without a fixed search_path" },
        { "GRANT EXECUTE ON FUNCTION gozleme.current_account_id() TO PUBLIC", "gozleme.current_account_id is executable by PUBLIC" },
        { "GRANT USAGE ON SCHEMA gozleme_private TO gozleme_app", "gozleme_app has a privilege on schema gozleme_private" },
        { "CREATE VIEW gozleme.rogue_view AS SELECT id FROM gozleme.organizations", "gozleme.rogue_view is a view" },
        { "CREATE MATERIALIZED VIEW gozleme.rogue_matview AS SELECT id FROM gozleme.organizations WITH NO DATA", "gozleme.rogue_matview is a materialized view" },
        { "CREATE SCHEMA rogue; CREATE FUNCTION rogue.peek() RETURNS bigint LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS 'SELECT count(*) FROM gozleme.organizations'", "rogue.peek is SECURITY DEFINER but not a pinned helper" },
        { "CREATE FUNCTION public.peek() RETURNS bigint LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS 'SELECT count(*) FROM gozleme.organizations'", "public.peek is SECURITY DEFINER but not a pinned helper" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task Each_kind_of_violation_is_reported(string change, string expected)
    {
        var violations = await InRolledBackChangeAsync(change, null);
        Assert.Contains(violations, v => v.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_table_classified_shared_or_public_is_a_violation_until_GOZ_0012()
    {
        var violations = await InRolledBackChangeAsync(null, new HashSet<string>(StringComparer.Ordinal) { "users" });
        Assert.Contains("gozleme.users is classified shared/public, which is reserved until GOZ-0012 defines its access path", violations);
    }

    private Task<IReadOnlyList<string>> InRolledBackChangeAsync(string? change, IReadOnlySet<string>? sharedPublic) =>
        GozlemeDatabase.WithConnectionAsync(Db.MigratorConnectionString, async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync();
            if (change is not null)
            {
                await using var alter = new NpgsqlCommand(change, connection, transaction);
                await alter.ExecuteNonQueryAsync();
            }

            var violations = await RlsCatalog.ViolationsAsync(connection, sharedPublic);
            await transaction.RollbackAsync();
            return violations;
        });
}
