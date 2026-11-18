using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// The shared manifest validation budget (GOZ-0006 maintainer decision 2026-11-18, "shared-rate-limit", T099):
/// <c>manifest_validation_attempts</c> (<c>GlobalIdentity</c>) counts validations per account per clock minute, one
/// count for every API replica, like <c>publisher_registration_attempts</c>. It holds an account, a window and a
/// count, never manifest data (FR-017).
/// </summary>
[DbContext(typeof(GozlemeDbContext))]
[Migration("20261120000000_ManifestValidationAttempts")]
internal sealed class ManifestValidationAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE gozleme.manifest_validation_attempts (
              account_id uuid NOT NULL CONSTRAINT fk_manifest_validation_attempts_users REFERENCES gozleme.users (id) ON DELETE CASCADE,
              window_start timestamptz NOT NULL,
              attempts integer NOT NULL CONSTRAINT ck_manifest_validation_attempts_attempts CHECK (attempts > 0),
              CONSTRAINT pk_manifest_validation_attempts PRIMARY KEY (account_id, window_start));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS gozleme.manifest_validation_attempts;");
    }
}
