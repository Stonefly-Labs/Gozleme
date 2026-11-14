using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Foundation: the <c>gozleme</c> schema and privileges for the non-bypassing application role.
/// Deliberately creates no tables — identity, organization and catalog belong to later specs.
/// </summary>
[DbContext(typeof(GozlemeDbContext))]
[Migration("20261114000000_Foundation")]
internal sealed class Foundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA IF NOT EXISTS gozleme;
            GRANT USAGE ON SCHEMA gozleme TO gozleme_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA gozleme GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO gozleme_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA gozleme GRANT USAGE, SELECT ON SEQUENCES TO gozleme_app;
            GRANT SELECT ON public."__EFMigrationsHistory" TO gozleme_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE SELECT ON public."__EFMigrationsHistory" FROM gozleme_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA gozleme REVOKE USAGE, SELECT ON SEQUENCES FROM gozleme_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA gozleme REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM gozleme_app;
            DROP SCHEMA IF EXISTS gozleme RESTRICT;
            """);
    }
}
