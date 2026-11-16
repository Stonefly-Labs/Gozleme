using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Tenant row-level security (GOZ-0003; research R1–R7, contracts/tenant-context.md). Security objects
/// only: no column changes and no data migration, so the model snapshot is unchanged.
/// <list type="bullet">
/// <item><c>gozleme_private</c>: <c>pgcrypto</c> and the context key; no privilege for <c>gozleme_app</c>.</item>
/// <item>Helpers owned by the migrator. Definer helpers bypass the policies through the
/// <c>rls_internal()</c> guard, which holds only while an owner-defined definer function runs for another login (R4).</item>
/// <item>RLS enabled and forced on <c>organizations</c>, <c>memberships</c> and <c>invitations</c>, so
/// the owner login is bound too. Every predicate is a <c>CASE</c>, which fixes evaluation order and
/// keeps helpers from recursing through their own policy.</item>
/// </list>
/// </summary>
[DbContext(typeof(GozlemeDbContext))]
[Migration("20261117000000_TenantRls")]
internal sealed class TenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA gozleme_private;
            REVOKE ALL ON SCHEMA gozleme_private FROM PUBLIC;

            DO $$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
                         WHERE e.extname = 'pgcrypto' AND n.nspname <> 'gozleme_private') THEN
                RAISE EXCEPTION 'pgcrypto is already installed in another schema; Gozleme needs it in gozleme_private';
              END IF;
            END $$;
            CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA gozleme_private;

            CREATE TABLE gozleme_private.context_keys (
              id smallint PRIMARY KEY CHECK (id = 1),
              key bytea NOT NULL CHECK (octet_length(key) >= 32),
              provisioned_at timestamptz NOT NULL DEFAULT now()
            );

            -- Verifies v1.<account>.<expires>.<hex hmac-sha256>; accepted while now() < expires <= now() + 10 minutes.
            CREATE FUNCTION gozleme_private.verify_context(token text, OUT account_id uuid, OUT status text)
              LANGUAGE plpgsql STABLE SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              parts text[];
              secret bytea;
              expires timestamptz;
            BEGIN
              IF token IS NULL OR token = '' THEN
                status := 'missing';
                RETURN;
              END IF;
              parts := string_to_array(token, '.');
              IF array_length(parts, 1) IS DISTINCT FROM 4 OR parts[1] <> 'v1'
                 OR parts[2] !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                 OR parts[3] !~ '^[0-9]{1,12}$' OR parts[4] !~ '^[0-9a-f]{64}$' THEN
                status := 'invalid';
                RETURN;
              END IF;
              SELECT k.key INTO secret FROM gozleme_private.context_keys k WHERE k.id = 1;
              IF secret IS NULL OR encode(gozleme_private.hmac(convert_to(parts[1] || '.' || parts[2] || '.' || parts[3], 'UTF8'), secret, 'sha256'), 'hex') <> parts[4] THEN
                status := 'invalid';
                RETURN;
              END IF;
              expires := to_timestamp(parts[3]::bigint);
              IF expires <= now() OR expires > now() + interval '10 minutes' THEN
                status := 'expired';
                RETURN;
              END IF;
              account_id := parts[2]::uuid;
              status := 'ok';
            END $fn$;

            -- Policy guard (R4): true only inside an owner-defined definer helper. For another login,
            -- current_user differs from session_user there. For the owner login itself, the helpers that read
            -- tenant tables set gozleme.rls_helper while they run; without it their own reads would re-enter
            -- the policy and recurse whenever the plan evaluates it per row. The flag counts only when
            -- current_user is the owner, so the serving role gains nothing by setting it, and an owner login
            -- that sets it by hand gains no more than ALTER TABLE ... NO FORCE (contract §4).
            CREATE FUNCTION gozleme.rls_internal() RETURNS boolean LANGUAGE sql STABLE
              AS $fn$ SELECT current_user = 'gozleme_migrator'
                       AND (session_user <> 'gozleme_migrator' OR current_setting('gozleme.rls_helper', true) = 'on') $fn$;

            CREATE FUNCTION gozleme.establish_context(token text) RETURNS text
              LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              result text := (gozleme_private.verify_context(token)).status;
            BEGIN
              PERFORM set_config('gozleme.account_context', CASE WHEN result = 'ok' THEN token ELSE '' END, true);
              RETURN result;
            END $fn$;

            CREATE FUNCTION gozleme.current_account_id() RETURNS uuid
              LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
              AS $fn$ SELECT (gozleme_private.verify_context(current_setting('gozleme.account_context', true))).account_id $fn$;

            -- Helpers that read tenant tables mark themselves for rls_internal() and restore the previous value.
            CREATE FUNCTION gozleme.account_organizations() RETURNS SETOF uuid
              LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              previous text := coalesce(current_setting('gozleme.rls_helper', true), '');
              account uuid := gozleme.current_account_id();
            BEGIN
              PERFORM set_config('gozleme.rls_helper', 'on', true);
              RETURN QUERY SELECT m.organization_id FROM gozleme.memberships m WHERE m.user_id = account;
              PERFORM set_config('gozleme.rls_helper', previous, true);
            END $fn$;

            CREATE FUNCTION gozleme.is_new_organization_of_current_account(org uuid) RETURNS boolean
              LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              previous text := coalesce(current_setting('gozleme.rls_helper', true), '');
              account uuid := gozleme.current_account_id();
              result boolean;
            BEGIN
              PERFORM set_config('gozleme.rls_helper', 'on', true);
              result := EXISTS (SELECT 1 FROM gozleme.organizations o WHERE o.id = org AND o.created_by = account)
                AND NOT EXISTS (SELECT 1 FROM gozleme.memberships m WHERE m.organization_id = org);
              PERFORM set_config('gozleme.rls_helper', previous, true);
              RETURN result;
            END $fn$;

            -- A bearer capability for one invitation: the hex SHA-256 of a presented code (R6). Not a definer.
            CREATE FUNCTION gozleme.invitation_capability() RETURNS bytea
              LANGUAGE sql STABLE SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT CASE WHEN v ~ '^[0-9a-f]{64}$' THEN decode(v, 'hex') END
              FROM (SELECT current_setting('gozleme.invitation_capability', true) AS v) s
            $fn$;

            -- Lock order organization, then invitation (GOZ-0002-IF-20); requires a verified account.
            CREATE FUNCTION gozleme.lock_invited_organization(code_hash bytea) RETURNS uuid
              LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              previous text := coalesce(current_setting('gozleme.rls_helper', true), '');
              result uuid;
            BEGIN
              IF gozleme.current_account_id() IS NULL THEN
                RETURN NULL;
              END IF;
              PERFORM set_config('gozleme.rls_helper', 'on', true);
              SELECT o.id INTO result FROM gozleme.organizations o
              WHERE o.id = (SELECT i.organization_id FROM gozleme.invitations i WHERE i.code_hash = lock_invited_organization.code_hash)
              FOR KEY SHARE OF o;
              PERFORM set_config('gozleme.rls_helper', previous, true);
              RETURN result;
            END $fn$;

            REVOKE EXECUTE ON FUNCTION gozleme_private.verify_context(text) FROM PUBLIC;
            REVOKE EXECUTE ON FUNCTION gozleme.rls_internal(), gozleme.establish_context(text), gozleme.current_account_id(),
              gozleme.account_organizations(), gozleme.is_new_organization_of_current_account(uuid),
              gozleme.invitation_capability(), gozleme.lock_invited_organization(bytea) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION gozleme.rls_internal(), gozleme.establish_context(text), gozleme.current_account_id(),
              gozleme.account_organizations(), gozleme.is_new_organization_of_current_account(uuid),
              gozleme.invitation_capability(), gozleme.lock_invited_organization(bytea) TO gozleme_app;

            ALTER TABLE gozleme.organizations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.organizations FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.memberships ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.memberships FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.invitations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.invitations FORCE ROW LEVEL SECURITY;

            -- organizations: members; the creator also sees its new organization until the first membership exists.
            CREATE POLICY organizations_select ON gozleme.organizations FOR SELECT USING (CASE
              WHEN gozleme.rls_internal() THEN true
              WHEN id IN (SELECT gozleme.account_organizations()) THEN true
              WHEN created_by = (SELECT gozleme.current_account_id()) THEN gozleme.is_new_organization_of_current_account(id)
              ELSE false END);
            CREATE POLICY organizations_insert ON gozleme.organizations FOR INSERT WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE created_by = (SELECT gozleme.current_account_id()) END);
            CREATE POLICY organizations_update ON gozleme.organizations FOR UPDATE
              USING (CASE WHEN gozleme.rls_internal() THEN true ELSE id IN (SELECT gozleme.account_organizations()) END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true ELSE id IN (SELECT gozleme.account_organizations()) END);
            -- No DELETE policy: organizations are never deleted (GOZ-0002).

            -- memberships: an account's own rows across organizations, and every row of its organizations.
            CREATE POLICY memberships_select ON gozleme.memberships FOR SELECT USING (CASE
              WHEN gozleme.rls_internal() THEN true
              WHEN user_id = (SELECT gozleme.current_account_id()) THEN true
              ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY memberships_insert ON gozleme.memberships FOR INSERT WITH CHECK (CASE
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
            CREATE POLICY memberships_update ON gozleme.memberships FOR UPDATE
              USING (CASE WHEN gozleme.rls_internal() THEN true
                     ELSE user_id = (SELECT gozleme.current_account_id()) AND organization_id IN (SELECT gozleme.account_organizations()) END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true
                     ELSE user_id = (SELECT gozleme.current_account_id()) AND organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY memberships_delete ON gozleme.memberships FOR DELETE USING (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE organization_id IN (SELECT gozleme.account_organizations()) END);

            -- invitations: members of the organization, or the holder of one invitation's code.
            CREATE POLICY invitations_select ON gozleme.invitations FOR SELECT USING (CASE
              WHEN gozleme.rls_internal() THEN true
              WHEN organization_id IN (SELECT gozleme.account_organizations()) THEN true
              ELSE code_hash = (SELECT gozleme.invitation_capability()) END);
            CREATE POLICY invitations_insert ON gozleme.invitations FOR INSERT WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE organization_id IN (SELECT gozleme.account_organizations()) AND created_by = (SELECT gozleme.current_account_id()) END);
            CREATE POLICY invitations_update ON gozleme.invitations FOR UPDATE
              USING (CASE WHEN gozleme.rls_internal() THEN true
                     WHEN organization_id IN (SELECT gozleme.account_organizations()) THEN true
                     ELSE code_hash = (SELECT gozleme.invitation_capability()) END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true
                     WHEN organization_id IN (SELECT gozleme.account_organizations()) THEN true
                     ELSE code_hash = (SELECT gozleme.invitation_capability()) AND used_by = (SELECT gozleme.current_account_id()) END);
            CREATE POLICY invitations_delete ON gozleme.invitations FOR DELETE USING (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS invitations_delete ON gozleme.invitations;
            DROP POLICY IF EXISTS invitations_update ON gozleme.invitations;
            DROP POLICY IF EXISTS invitations_insert ON gozleme.invitations;
            DROP POLICY IF EXISTS invitations_select ON gozleme.invitations;
            DROP POLICY IF EXISTS memberships_delete ON gozleme.memberships;
            DROP POLICY IF EXISTS memberships_update ON gozleme.memberships;
            DROP POLICY IF EXISTS memberships_insert ON gozleme.memberships;
            DROP POLICY IF EXISTS memberships_select ON gozleme.memberships;
            DROP POLICY IF EXISTS organizations_update ON gozleme.organizations;
            DROP POLICY IF EXISTS organizations_insert ON gozleme.organizations;
            DROP POLICY IF EXISTS organizations_select ON gozleme.organizations;
            ALTER TABLE gozleme.invitations NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.invitations DISABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.memberships NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.memberships DISABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.organizations NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.organizations DISABLE ROW LEVEL SECURITY;
            DROP FUNCTION IF EXISTS gozleme.lock_invited_organization(bytea);
            DROP FUNCTION IF EXISTS gozleme.invitation_capability();
            DROP FUNCTION IF EXISTS gozleme.is_new_organization_of_current_account(uuid);
            DROP FUNCTION IF EXISTS gozleme.account_organizations();
            DROP FUNCTION IF EXISTS gozleme.current_account_id();
            DROP FUNCTION IF EXISTS gozleme.establish_context(text);
            DROP FUNCTION IF EXISTS gozleme.rls_internal();
            DROP SCHEMA IF EXISTS gozleme_private CASCADE;
            """);
    }
}
