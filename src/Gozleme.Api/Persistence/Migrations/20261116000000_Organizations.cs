using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Organizations and role-neutral membership (GOZ-0002): organizations, memberships and invitations.
/// Additive only, so the previous release keeps running during pre-upgrade. The app role receives DML
/// through the Foundation default privileges. No RLS policy yet (GOZ-0003); every table carries an
/// explicit tenant key (<c>organizations.id</c>, <c>organization_id</c>) so RLS needs no data migration.
/// </summary>
internal sealed partial class Organizations : Migration
{
    private static readonly string[] InvitationListColumns = ["organization_id", "created_at"];
    private static readonly string[] ContextColumns = ["user_id", "selected_at"];
    private static readonly bool[] ContextDescending = [false, true];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organizations",
            schema: "gozleme",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_organizations", x => x.id);
                table.ForeignKey(
                    name: "fk_organizations_users_created_by",
                    column: x => x.created_by,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "invitations",
            schema: "gozleme",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                used_by = table.Column<Guid>(type: "uuid", nullable: true),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                revoked_by = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_invitations", x => x.id);
                table.CheckConstraint("ck_invitations_code_hash_length", "octet_length(code_hash) = 32");
                table.CheckConstraint("ck_invitations_used_or_revoked", "NOT (used_at IS NOT NULL AND revoked_at IS NOT NULL)");
                table.ForeignKey(
                    name: "fk_invitations_organizations_organization_id",
                    column: x => x.organization_id,
                    principalSchema: "gozleme",
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_invitations_users_created_by",
                    column: x => x.created_by,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_invitations_users_revoked_by",
                    column: x => x.revoked_by,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_invitations_users_used_by",
                    column: x => x.used_by,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "memberships",
            schema: "gozleme",
            columns: table => new
            {
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                joined_via = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                invitation_id = table.Column<Guid>(type: "uuid", nullable: true),
                selected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_memberships", x => new { x.organization_id, x.user_id });
                table.CheckConstraint("ck_memberships_joined_via", "joined_via IN ('creator', 'invitation')");
                table.ForeignKey(
                    name: "fk_memberships_invitations_invitation_id",
                    column: x => x.invitation_id,
                    principalSchema: "gozleme",
                    principalTable: "invitations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_memberships_organizations_organization_id",
                    column: x => x.organization_id,
                    principalSchema: "gozleme",
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_memberships_users_user_id",
                    column: x => x.user_id,
                    principalSchema: "gozleme",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_invitations_code_hash",
            schema: "gozleme",
            table: "invitations",
            column: "code_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_invitations_created_by",
            schema: "gozleme",
            table: "invitations",
            column: "created_by");

        migrationBuilder.CreateIndex(
            name: "ix_invitations_organization_id_created_at",
            schema: "gozleme",
            table: "invitations",
            columns: InvitationListColumns);

        migrationBuilder.CreateIndex(
            name: "ix_invitations_revoked_by",
            schema: "gozleme",
            table: "invitations",
            column: "revoked_by");

        migrationBuilder.CreateIndex(
            name: "ix_invitations_used_by",
            schema: "gozleme",
            table: "invitations",
            column: "used_by");

        migrationBuilder.CreateIndex(
            name: "ix_memberships_invitation_id",
            schema: "gozleme",
            table: "memberships",
            column: "invitation_id");

        migrationBuilder.CreateIndex(
            name: "ix_memberships_user_id_selected_at",
            schema: "gozleme",
            table: "memberships",
            columns: ContextColumns,
            descending: ContextDescending);

        migrationBuilder.CreateIndex(
            name: "ix_organizations_created_by",
            schema: "gozleme",
            table: "organizations",
            column: "created_by");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "memberships",
            schema: "gozleme");

        migrationBuilder.DropTable(
            name: "invitations",
            schema: "gozleme");

        migrationBuilder.DropTable(
            name: "organizations",
            schema: "gozleme");
    }
}
