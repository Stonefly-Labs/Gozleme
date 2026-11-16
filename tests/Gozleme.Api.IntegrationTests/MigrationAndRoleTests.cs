using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

public sealed class MigrationAndRoleTests(PgvectorDatabaseFixture fixture)
{
    private GozlemeDatabase Db => fixture.Database;

    [Fact]
    public async Task Migrations_are_recorded_exactly_once_and_rerun_is_idempotent()
    {
        Assert.Equal(0, await Db.MigrateAsync());
        Assert.Equal(0, await Db.MigrateAsync());

        var count = await Db.ScalarAsync<long>("""SELECT count(*) FROM public."__EFMigrationsHistory" """);
        var latest = await Db.ScalarAsync<string>("""SELECT max("MigrationId") FROM public."__EFMigrationsHistory" """);

        Assert.Equal(4, count); // Foundation (GOZ-0000) + LocalIdentity (GOZ-0001) + Organizations (GOZ-0002) + TenantRls (GOZ-0003)
        Assert.Equal("20261117000000_TenantRls", latest);
    }

    [Fact]
    public async Task App_role_is_not_superuser_and_cannot_bypass_rls()
    {
        var flags = await Db.ScalarAsync<string>("""
            SELECT concat_ws(',', rolsuper, rolbypassrls, rolcreatedb, rolcreaterole)
            FROM pg_roles WHERE rolname = 'gozleme_app'
            """);
        Assert.Equal("f,f,f,f", flags);

        var migratorFlags = await Db.ScalarAsync<string>(
            "SELECT concat_ws(',', rolsuper, rolbypassrls) FROM pg_roles WHERE rolname = 'gozleme_migrator'");
        Assert.Equal("f,f", migratorFlags);
    }

    [Theory]
    [InlineData("CREATE TABLE gozleme.should_fail (id int)")]
    [InlineData("CREATE TABLE public.should_fail (id int)")]
    [InlineData("CREATE SCHEMA should_fail")]
    public async Task App_role_cannot_perform_ddl(string ddl)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => GozlemeDatabase.ExecuteAsync(ddl, Db.AppConnectionString));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task App_role_can_read_schema_version()
    {
        var latest = await Db.ScalarAsync<string>(
            """SELECT max("MigrationId") FROM public."__EFMigrationsHistory" """, Db.AppConnectionString);
        Assert.Equal("20261117000000_TenantRls", latest);
    }

    [Fact]
    public async Task Identity_tables_are_owned_by_migrator_with_dml_only_for_app_role()
    {
        var owners = await Db.ScalarAsync<string>("""
            SELECT string_agg(DISTINCT tableowner, ',') FROM pg_tables WHERE schemaname = 'gozleme'
            """);
        Assert.Equal("gozleme_migrator", owners);

        var privileges = await Db.ScalarAsync<string>("""
            SELECT string_agg(DISTINCT privilege_type, ',' ORDER BY privilege_type)
            FROM information_schema.role_table_grants
            WHERE grantee = 'gozleme_app' AND table_schema = 'gozleme'
            """);
        Assert.Equal("DELETE,INSERT,SELECT,UPDATE", privileges);
        Assert.False(await Db.ScalarAsync<bool>("SELECT has_schema_privilege('gozleme_app', 'gozleme', 'CREATE')"));
    }
}
