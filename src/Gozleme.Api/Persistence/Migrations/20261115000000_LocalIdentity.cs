using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Local identity (GOZ-0001): user-only Identity store, server-side sessions and the Data
/// Protection key ring. Additive only, so the previous release keeps running during pre-upgrade.
/// The app role receives DML through the Foundation default privileges.
/// </summary>
internal sealed partial class LocalIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "gozleme");

        migrationBuilder.CreateTable(
            name: "data_protection_keys",
            schema: "gozleme",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                friendly_name = table.Column<string>(type: "text", nullable: true),
                xml = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_data_protection_keys", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            schema: "gozleme",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                last_sign_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                password_hash = table.Column<string>(type: "text", nullable: true),
                security_stamp = table.Column<string>(type: "text", nullable: true),
                concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                phone_number = table.Column<string>(type: "text", nullable: true),
                phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                access_failed_count = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "auth_sessions",
            schema: "gozleme",
            columns: table => new
            {
                key_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                ticket = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_auth_sessions", x => x.key_hash);
                table.ForeignKey(
                    name: "fk_auth_sessions_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_claims",
            schema: "gozleme",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_type = table.Column<string>(type: "text", nullable: true),
                claim_value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_claims", x => x.id);
                table.ForeignKey(
                    name: "fk_user_claims_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_logins",
            schema: "gozleme",
            columns: table => new
            {
                login_provider = table.Column<string>(type: "text", nullable: false),
                provider_key = table.Column<string>(type: "text", nullable: false),
                provider_display_name = table.Column<string>(type: "text", nullable: true),
                user_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_logins", x => new { x.login_provider, x.provider_key });
                table.ForeignKey(
                    name: "fk_user_logins_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_tokens",
            schema: "gozleme",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                login_provider = table.Column<string>(type: "text", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                table.ForeignKey(
                    name: "fk_user_tokens_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_auth_sessions_expires_at",
            schema: "gozleme",
            table: "auth_sessions",
            column: "expires_at");

        migrationBuilder.CreateIndex(
            name: "ix_auth_sessions_user_id",
            schema: "gozleme",
            table: "auth_sessions",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_user_claims_user_id",
            schema: "gozleme",
            table: "user_claims",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_user_logins_user_id",
            schema: "gozleme",
            table: "user_logins",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "email_index",
            schema: "gozleme",
            table: "users",
            column: "normalized_email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "user_name_index",
            schema: "gozleme",
            table: "users",
            column: "normalized_user_name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "auth_sessions",
            schema: "gozleme");

        migrationBuilder.DropTable(
            name: "data_protection_keys",
            schema: "gozleme");

        migrationBuilder.DropTable(
            name: "user_claims",
            schema: "gozleme");

        migrationBuilder.DropTable(
            name: "user_logins",
            schema: "gozleme");

        migrationBuilder.DropTable(
            name: "user_tokens",
            schema: "gozleme");

        migrationBuilder.DropTable(
            name: "users",
            schema: "gozleme");
    }
}
