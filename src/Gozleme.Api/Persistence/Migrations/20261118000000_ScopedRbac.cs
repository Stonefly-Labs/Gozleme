using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Built-in organization roles (GOZ-0004; research R1–R5, data-model.md, contracts/permissions.md).
/// <list type="bullet">
/// <item><c>memberships.roles</c>: existing rows read <c>{admin,member}</c> from the column default
/// (maintainer decision, option B), which then becomes <c>{member}</c>. DDL only: no data migration and no
/// lift of the owner binding (GOZ-0003-IF-03).</item>
/// <item>Admin-only writes are enforced in the database too (Q3 = B): policies on <c>organizations</c> and
/// <c>invitations</c> through the non-definer <c>account_is_admin()</c>, and <c>memberships_guard</c> for
/// <c>memberships</c>, whose own policies cannot read their table without recursing. No definer helper is added.</item>
/// </list>
/// </summary>
internal sealed partial class ScopedRbac : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($$"""
            ALTER TABLE gozleme.memberships ADD COLUMN roles text[] NOT NULL DEFAULT '{admin,member}'::text[];
            ALTER TABLE gozleme.memberships ALTER COLUMN roles SET DEFAULT '{member}'::text[];
            ALTER TABLE gozleme.memberships ADD CONSTRAINT ck_memberships_roles CHECK ({{GozlemeDbContext.RolesCheck}});

            -- Not a definer: reads the caller's own membership row through memberships_select (research R3).
            CREATE FUNCTION gozleme.account_is_admin(org uuid) RETURNS boolean LANGUAGE sql STABLE
              SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT EXISTS (SELECT 1 FROM gozleme.memberships m
                             WHERE m.organization_id = org AND m.user_id = gozleme.current_account_id() AND 'admin' = ANY (m.roles)) $fn$;

            -- Serializes membership changes per organization (research R5). Lock order: this, organization, invitation.
            CREATE FUNCTION gozleme.lock_organization_members(org uuid) RETURNS void LANGUAGE sql VOLATILE
              SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT pg_advisory_xact_lock(hashtextextended('gozleme.memberships:' || org::text, 0)) $fn$;

            REVOKE EXECUTE ON FUNCTION gozleme.account_is_admin(uuid), gozleme.lock_organization_members(uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION gozleme.account_is_admin(uuid), gozleme.lock_organization_members(uuid) TO gozleme_app;

            ALTER POLICY organizations_update ON gozleme.organizations
              USING (CASE WHEN gozleme.rls_internal() THEN true ELSE gozleme.account_is_admin(id) END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true ELSE gozleme.account_is_admin(id) END);

            ALTER POLICY invitations_insert ON gozleme.invitations WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE gozleme.account_is_admin(organization_id) AND created_by = (SELECT gozleme.current_account_id()) END);
            ALTER POLICY invitations_update ON gozleme.invitations
              USING (CASE WHEN gozleme.rls_internal() THEN true
                     WHEN gozleme.account_is_admin(organization_id) THEN true
                     ELSE code_hash = (SELECT gozleme.invitation_capability())
                          AND revoked_at IS NULL AND used_at IS NULL AND expires_at > now() END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true
                     WHEN gozleme.account_is_admin(organization_id) THEN true
                     ELSE code_hash = (SELECT gozleme.invitation_capability()) AND used_by = (SELECT gozleme.current_account_id()) END);
            ALTER POLICY invitations_delete ON gozleme.invitations USING (CASE
              WHEN gozleme.rls_internal() THEN true ELSE gozleme.account_is_admin(organization_id) END);

            -- The creator starts as admin; a redeemer joins as member only. Adding another account stays
            -- member-level here and memberships_guard requires admin for it.
            ALTER POLICY memberships_insert ON gozleme.memberships WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              WHEN organization_id IN (SELECT gozleme.account_organizations()) THEN true
              WHEN user_id IS DISTINCT FROM (SELECT gozleme.current_account_id()) THEN false
              WHEN joined_via = 'creator' THEN roles <@ '{admin,member}'::text[] AND gozleme.is_new_organization_of_current_account(organization_id)
              WHEN joined_via = 'invitation' THEN roles = '{member}'::text[] AND EXISTS (
                SELECT 1 FROM gozleme.invitations i
                WHERE i.id = memberships.invitation_id AND i.organization_id = memberships.organization_id
                  AND i.code_hash = (SELECT gozleme.invitation_capability())
                  AND i.revoked_at IS NULL AND i.expires_at > now()
                  AND (i.used_at IS NULL OR i.used_by = memberships.user_id))
              ELSE false END);
            -- Any row of the caller's organizations, so an admin's change to another member applies
            -- (GOZ-0003-IF-05); memberships_guard decides what may change.
            ALTER POLICY memberships_update ON gozleme.memberships
              USING (CASE WHEN gozleme.rls_internal() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);

            -- Role checks that a memberships policy cannot make (research R4). Binds every role, the owner
            -- and superusers included, except foreign-key cascades, which run as the owner one trigger level down.
            CREATE FUNCTION gozleme.memberships_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              account uuid := gozleme.current_account_id();
              org uuid := CASE WHEN TG_OP = 'INSERT' THEN NEW.organization_id ELSE OLD.organization_id END;
              target uuid := CASE WHEN TG_OP = 'INSERT' THEN NEW.user_id ELSE OLD.user_id END;
            BEGIN
              IF gozleme.rls_internal() AND pg_trigger_depth() > 1 THEN
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
              END IF;
              IF TG_OP <> 'DELETE' THEN
                NEW.roles := ARRAY(SELECT DISTINCT r FROM unnest(NEW.roles) r ORDER BY r);
              END IF;
              IF TG_OP = 'UPDATE' AND (NEW.organization_id IS DISTINCT FROM OLD.organization_id OR NEW.user_id IS DISTINCT FROM OLD.user_id
                   OR NEW.joined_at IS DISTINCT FROM OLD.joined_at OR NEW.joined_via IS DISTINCT FROM OLD.joined_via
                   OR NEW.invitation_id IS DISTINCT FROM OLD.invitation_id -- only the FK SET NULL cascade, which passed above
                   OR (NEW.selected_at IS DISTINCT FROM OLD.selected_at AND target IS DISTINCT FROM account)) THEN
                RAISE EXCEPTION 'membership change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              IF TG_OP = 'UPDATE' AND NEW.roles IS NOT DISTINCT FROM OLD.roles THEN
                RETURN NEW;
              END IF;
              IF TG_OP = 'INSERT' AND target IS NOT DISTINCT FROM account THEN
                RETURN NEW; -- creator or redeemer; memberships_insert fixes its roles
              END IF;
              -- A role change, removal, departure or added account: lock before reading authority, so a
              -- concurrent demotion is always seen.
              PERFORM gozleme.lock_organization_members(org);
              IF (TG_OP <> 'DELETE' OR target IS DISTINCT FROM account) AND NOT gozleme.account_is_admin(org) THEN
                RAISE EXCEPTION 'membership change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              -- The caller is still a member here, so it sees the other admins (an AFTER trigger would not).
              IF TG_OP <> 'INSERT' AND 'admin' = ANY (OLD.roles) AND (TG_OP = 'DELETE' OR NOT 'admin' = ANY (NEW.roles))
                 AND NOT EXISTS (SELECT 1 FROM gozleme.memberships m
                                 WHERE m.organization_id = org AND m.user_id <> target AND 'admin' = ANY (m.roles)) THEN
                RAISE EXCEPTION 'last admin' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.memberships_guard() FROM PUBLIC;
            CREATE TRIGGER memberships_guard BEFORE INSERT OR UPDATE OR DELETE ON gozleme.memberships
              FOR EACH ROW EXECUTE FUNCTION gozleme.memberships_guard();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restores the GOZ-0003 policies verbatim (20261117000000_TenantRls).
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS memberships_guard ON gozleme.memberships;
            DROP FUNCTION IF EXISTS gozleme.memberships_guard();

            ALTER POLICY organizations_update ON gozleme.organizations
              USING (CASE WHEN gozleme.rls_internal() THEN true ELSE id IN (SELECT gozleme.account_organizations()) END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true ELSE id IN (SELECT gozleme.account_organizations()) END);
            ALTER POLICY invitations_insert ON gozleme.invitations WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE organization_id IN (SELECT gozleme.account_organizations()) AND created_by = (SELECT gozleme.current_account_id()) END);
            ALTER POLICY invitations_update ON gozleme.invitations
              USING (CASE WHEN gozleme.rls_internal() THEN true
                     WHEN organization_id IN (SELECT gozleme.account_organizations()) THEN true
                     ELSE code_hash = (SELECT gozleme.invitation_capability())
                          AND revoked_at IS NULL AND used_at IS NULL AND expires_at > now() END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true
                     WHEN organization_id IN (SELECT gozleme.account_organizations()) THEN true
                     ELSE code_hash = (SELECT gozleme.invitation_capability()) AND used_by = (SELECT gozleme.current_account_id()) END);
            ALTER POLICY invitations_delete ON gozleme.invitations USING (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            ALTER POLICY memberships_insert ON gozleme.memberships WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              WHEN organization_id IN (SELECT gozleme.account_organizations()) THEN true
              WHEN user_id IS DISTINCT FROM (SELECT gozleme.current_account_id()) THEN false
              WHEN joined_via = 'creator' THEN gozleme.is_new_organization_of_current_account(organization_id)
              WHEN joined_via = 'invitation' THEN EXISTS (
                SELECT 1 FROM gozleme.invitations i
                WHERE i.id = memberships.invitation_id AND i.organization_id = memberships.organization_id
                  AND i.code_hash = (SELECT gozleme.invitation_capability())
                  AND i.revoked_at IS NULL AND i.expires_at > now()
                  AND (i.used_at IS NULL OR i.used_by = memberships.user_id))
              ELSE false END);
            ALTER POLICY memberships_update ON gozleme.memberships
              USING (CASE WHEN gozleme.rls_internal() THEN true
                     ELSE user_id = (SELECT gozleme.current_account_id()) AND organization_id IN (SELECT gozleme.account_organizations()) END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true
                     ELSE user_id = (SELECT gozleme.current_account_id()) AND organization_id IN (SELECT gozleme.account_organizations()) END);

            DROP FUNCTION IF EXISTS gozleme.lock_organization_members(uuid);
            DROP FUNCTION IF EXISTS gozleme.account_is_admin(uuid);
            ALTER TABLE gozleme.memberships DROP CONSTRAINT IF EXISTS ck_memberships_roles;
            ALTER TABLE gozleme.memberships DROP COLUMN IF EXISTS roles;
            """);
    }
}
