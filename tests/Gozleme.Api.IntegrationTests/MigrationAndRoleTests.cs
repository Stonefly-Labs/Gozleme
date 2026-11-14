using Gozleme.Api.IntegrationTests.Infrastructure;
using Npgsql;

namespace Gozleme.Api.IntegrationTests;

public sealed class MigrationAndRoleTests(PgvectorDatabaseFixture fixture)
{
    private GozlemeDatabase Db => fixture.Database;

    [Fact]
    public async Task Foundation_migration_is_recorded_exactly_once_and_rerun_is_idempotent()
    {
        Assert.Equal(0, await Db.MigrateAsync());
        Assert.Equal(0, await Db.MigrateAsync());

        var count = await Db.ScalarAsync<long>("""SELECT count(*) FROM public."__EFMigrationsHistory" """);
        var latest = await Db.ScalarAsync<string>("""SELECT max("MigrationId") FROM public."__EFMigrationsHistory" """);

        Assert.Equal(1, count);
        Assert.Equal("20261114000000_Foundation", latest);
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
        Assert.Equal("20261114000000_Foundation", latest);
    }
}
