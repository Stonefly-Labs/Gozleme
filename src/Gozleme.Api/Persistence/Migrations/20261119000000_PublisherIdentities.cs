using Gozleme.Api.Publishers;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Publisher identities and canonical capability IDs (GOZ-0005; data-model.md, research R1–R7, verified on PostgreSQL 17).
/// <list type="bullet">
/// <item><c>registry_origin</c> (<c>InstanceReference</c>): one row, recorded by the migrator after this migration,
/// readable by <c>gozleme_app</c> and changeable by nobody.</item>
/// <item><c>publishers</c>, <c>capabilities</c> (<c>TenantOwned</c>, forced RLS): member reads; publisher writes need
/// <c>admin</c> and capability writes <c>publisher</c>, through the non-definer <c>account_has_role</c>; only display
/// names change, for every login.</item>
/// <item><c>handle_claims</c>, <c>alias_reservations</c> (<c>InstanceClaims</c>, RLS enabled but not forced): the
/// installation's namespace. The live partial unique index is the only handle-uniqueness rule; an alias is
/// reserved by trigger in the capability's own statement and kept forever. The owner, with no account context,
/// is the operator (maintainer Q11); <c>handle_claims_guard</c> limits every login to the one-way transitions.</item>
/// </list>
/// Additive DDL only: no data migration, new role, lift of the owner binding or disabled guard (FR-031).
/// </summary>
internal sealed partial class PublisherIdentities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($$"""
            -- Not a definer: reads the caller's own membership row through memberships_select (GOZ-0004-IF-02, research R5).
            CREATE FUNCTION gozleme.account_has_role(org uuid, role text) RETURNS boolean LANGUAGE sql STABLE
              SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT EXISTS (SELECT 1 FROM gozleme.memberships m
                             WHERE m.organization_id = org AND m.user_id = gozleme.current_account_id() AND role = ANY (m.roles)) $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.account_has_role(uuid, text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION gozleme.account_has_role(uuid, text) TO gozleme_app;

            -- Refuses every change of a write-once row, for every login (GOZ-0000-IF-04).
            CREATE FUNCTION gozleme.immutable_row_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              RAISE EXCEPTION '% refused on %', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
            END $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.immutable_row_guard() FROM PUBLIC;

            CREATE TABLE gozleme.registry_origin (
              id smallint PRIMARY KEY CONSTRAINT ck_registry_origin_single CHECK (id = 1),
              origin text NOT NULL CONSTRAINT ck_registry_origin_origin CHECK ({{RegistryOrigin.Check}}),
              recorded_at timestamptz NOT NULL DEFAULT now());
            REVOKE ALL ON gozleme.registry_origin FROM gozleme_app;
            GRANT SELECT ON gozleme.registry_origin TO gozleme_app;
            CREATE TRIGGER registry_origin_guard BEFORE UPDATE OR DELETE ON gozleme.registry_origin
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER registry_origin_truncate BEFORE TRUNCATE ON gozleme.registry_origin
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();

            -- Publisher registrations per account per clock hour (FR-008, research R8): one shared count for every API
            -- replica, kept across restarts. Account-level infrastructure like auth_sessions; rows go with the account.
            CREATE TABLE gozleme.publisher_registration_attempts (
              account_id uuid NOT NULL CONSTRAINT fk_publisher_registration_attempts_users REFERENCES gozleme.users (id) ON DELETE CASCADE,
              window_start timestamptz NOT NULL,
              attempts integer NOT NULL CONSTRAINT ck_publisher_registration_attempts_attempts CHECK (attempts > 0),
              CONSTRAINT pk_publisher_registration_attempts PRIMARY KEY (account_id, window_start));

            CREATE TABLE gozleme.publishers (
              id uuid NOT NULL,
              organization_id uuid NOT NULL,
              handle text NOT NULL,
              display_name text NOT NULL,
              created_at timestamptz NOT NULL,
              created_by uuid,
              updated_at timestamptz NOT NULL,
              CONSTRAINT pk_publishers PRIMARY KEY (id),
              CONSTRAINT ak_publishers_id_organization_id UNIQUE (id, organization_id),
              CONSTRAINT fk_publishers_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_publishers_users_created_by FOREIGN KEY (created_by) REFERENCES gozleme.users (id) ON DELETE SET NULL,
              CONSTRAINT ck_publishers_handle CHECK ({{GozlemeDbContext.HandleCheck}}),
              CONSTRAINT ck_publishers_display_name CHECK ({{GozlemeDbContext.DisplayNameCheck}}));
            CREATE INDEX ix_publishers_organization_id ON gozleme.publishers (organization_id);
            CREATE INDEX ix_publishers_created_by ON gozleme.publishers (created_by);

            CREATE TABLE gozleme.capabilities (
              id uuid NOT NULL,
              organization_id uuid NOT NULL,
              publisher_id uuid NOT NULL,
              name text NOT NULL,
              display_name text NOT NULL,
              created_at timestamptz NOT NULL,
              created_by uuid,
              updated_at timestamptz NOT NULL,
              CONSTRAINT pk_capabilities PRIMARY KEY (id),
              CONSTRAINT ak_capabilities_id_organization_id UNIQUE (id, organization_id),
              CONSTRAINT fk_capabilities_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_capabilities_publishers_publisher_id_organization_id FOREIGN KEY (publisher_id, organization_id)
                REFERENCES gozleme.publishers (id, organization_id) ON DELETE RESTRICT,
              CONSTRAINT fk_capabilities_users_created_by FOREIGN KEY (created_by) REFERENCES gozleme.users (id) ON DELETE SET NULL,
              CONSTRAINT ck_capabilities_name CHECK ({{GozlemeDbContext.CapabilityNameCheck}}),
              CONSTRAINT ck_capabilities_display_name CHECK ({{GozlemeDbContext.DisplayNameCheck}}));
            CREATE UNIQUE INDEX ix_capabilities_publisher_id_name ON gozleme.capabilities (publisher_id, name);
            CREATE INDEX ix_capabilities_publisher_id_organization_id ON gozleme.capabilities (publisher_id, organization_id);
            CREATE INDEX ix_capabilities_organization_id ON gozleme.capabilities (organization_id);
            CREATE INDEX ix_capabilities_created_by ON gozleme.capabilities (created_by);
            REVOKE DELETE, TRUNCATE ON gozleme.publishers, gozleme.capabilities FROM gozleme_app;

            -- Every handle namespace entry, one row each (research R2). Installation-owned: RLS enabled, not forced,
            -- so the owner is the installation authority over it while gozleme_app stays bound by the policies (R4).
            CREATE TABLE gozleme.handle_claims (
              id uuid PRIMARY KEY,
              handle text NOT NULL CONSTRAINT ck_handle_claims_handle CHECK ({{GozlemeDbContext.HandleCheck}}),
              status text NOT NULL CONSTRAINT ck_handle_claims_status CHECK (status IN ('blocked', 'earmarked', 'held', 'released', 'reclaimed')),
              organization_id uuid CONSTRAINT fk_handle_claims_organizations REFERENCES gozleme.organizations (id),
              publisher_id uuid,
              created_at timestamptz NOT NULL DEFAULT now(),
              held_at timestamptz,
              ended_at timestamptz,
              CONSTRAINT fk_handle_claims_publishers FOREIGN KEY (publisher_id, organization_id) REFERENCES gozleme.publishers (id, organization_id),
              CONSTRAINT ck_handle_claims_shape CHECK (CASE status
                WHEN 'blocked' THEN organization_id IS NULL AND publisher_id IS NULL AND held_at IS NULL AND ended_at IS NULL
                WHEN 'earmarked' THEN organization_id IS NOT NULL AND publisher_id IS NULL AND held_at IS NULL AND ended_at IS NULL
                WHEN 'held' THEN organization_id IS NOT NULL AND publisher_id IS NOT NULL AND held_at IS NOT NULL AND ended_at IS NULL
                WHEN 'released' THEN publisher_id IS NULL AND held_at IS NULL AND ended_at IS NOT NULL
                ELSE organization_id IS NOT NULL AND publisher_id IS NOT NULL AND held_at IS NOT NULL AND ended_at IS NOT NULL END));
            CREATE UNIQUE INDEX ux_handle_claims_live ON gozleme.handle_claims (handle) WHERE status IN ('blocked', 'earmarked', 'held');
            CREATE UNIQUE INDEX ux_handle_claims_publisher ON gozleme.handle_claims (publisher_id) WHERE publisher_id IS NOT NULL;
            CREATE INDEX ix_handle_claims_organization_id ON gozleme.handle_claims (organization_id);
            REVOKE ALL ON gozleme.handle_claims FROM gozleme_app;
            GRANT SELECT, INSERT, UPDATE ON gozleme.handle_claims TO gozleme_app;
            ALTER TABLE gozleme.handle_claims ENABLE ROW LEVEL SECURITY;

            -- Every reserved alias, forever (research R5). gozleme_app may insert (through the trigger only) and never read.
            CREATE TABLE gozleme.alias_reservations (
              alias text PRIMARY KEY,
              capability_id uuid NOT NULL CONSTRAINT ux_alias_reservations_capability UNIQUE,
              organization_id uuid NOT NULL,
              created_at timestamptz NOT NULL DEFAULT now(),
              CONSTRAINT fk_alias_reservations_capabilities FOREIGN KEY (capability_id, organization_id) REFERENCES gozleme.capabilities (id, organization_id));
            REVOKE ALL ON gozleme.alias_reservations FROM gozleme_app;
            GRANT INSERT ON gozleme.alias_reservations TO gozleme_app;
            ALTER TABLE gozleme.alias_reservations ENABLE ROW LEVEL SECURITY;
            CREATE TRIGGER alias_reservations_guard BEFORE UPDATE OR DELETE ON gozleme.alias_reservations
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER alias_reservations_truncate BEFORE TRUNCATE ON gozleme.alias_reservations
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();

            ALTER TABLE gozleme.publishers ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.publishers FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.capabilities ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.capabilities FORCE ROW LEVEL SECURITY;

            -- A publisher is active while a held claim names it; suspension is derived and enforced here (FR-037).
            CREATE POLICY publishers_select ON gozleme.publishers FOR SELECT USING (CASE
              WHEN gozleme.rls_internal() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY publishers_insert ON gozleme.publishers FOR INSERT WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE gozleme.account_has_role(organization_id, 'admin') AND created_by = (SELECT gozleme.current_account_id()) END);
            CREATE POLICY publishers_update ON gozleme.publishers FOR UPDATE
              USING (CASE WHEN gozleme.rls_internal() THEN true ELSE gozleme.account_has_role(organization_id, 'admin') END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true
                ELSE gozleme.account_has_role(organization_id, 'admin')
                 AND EXISTS (SELECT 1 FROM gozleme.handle_claims c WHERE c.publisher_id = publishers.id AND c.status = 'held') END);

            CREATE POLICY capabilities_select ON gozleme.capabilities FOR SELECT USING (CASE
              WHEN gozleme.rls_internal() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY capabilities_insert ON gozleme.capabilities FOR INSERT WITH CHECK (CASE
              WHEN gozleme.rls_internal() THEN true
              ELSE gozleme.account_has_role(organization_id, 'publisher') AND created_by = (SELECT gozleme.current_account_id())
               AND EXISTS (SELECT 1 FROM gozleme.handle_claims c WHERE c.publisher_id = capabilities.publisher_id AND c.status = 'held') END);
            CREATE POLICY capabilities_update ON gozleme.capabilities FOR UPDATE
              USING (CASE WHEN gozleme.rls_internal() THEN true ELSE gozleme.account_has_role(organization_id, 'publisher') END)
              WITH CHECK (CASE WHEN gozleme.rls_internal() THEN true
                ELSE gozleme.account_has_role(organization_id, 'publisher')
                 AND EXISTS (SELECT 1 FROM gozleme.handle_claims c WHERE c.publisher_id = capabilities.publisher_id AND c.status = 'held') END);

            -- The application sees only its own organizations' entries; blocked rows have none (research R3).
            CREATE POLICY handle_claims_select ON gozleme.handle_claims FOR SELECT USING (
              organization_id IN (SELECT gozleme.account_organizations()));
            CREATE POLICY handle_claims_insert ON gozleme.handle_claims FOR INSERT WITH CHECK (CASE
              WHEN status <> 'held' THEN false
              ELSE gozleme.account_has_role(organization_id, 'admin')
               AND publisher_id IN (SELECT p.id FROM gozleme.publishers p WHERE p.organization_id = handle_claims.organization_id) END);
            CREATE POLICY handle_claims_update ON gozleme.handle_claims FOR UPDATE
              USING (CASE WHEN status <> 'earmarked' THEN false ELSE gozleme.account_has_role(organization_id, 'admin') END)
              WITH CHECK (CASE WHEN status <> 'held' THEN false
                ELSE gozleme.account_has_role(organization_id, 'admin')
                 AND publisher_id IN (SELECT p.id FROM gozleme.publishers p WHERE p.organization_id = handle_claims.organization_id) END);
            CREATE POLICY alias_reservations_insert ON gozleme.alias_reservations FOR INSERT WITH CHECK (
              gozleme.account_has_role(organization_id, 'publisher'));
            REVOKE DELETE, TRUNCATE ON gozleme.publishers, gozleme.capabilities, gozleme.handle_claims, gozleme.alias_reservations, gozleme.registry_origin FROM gozleme_app;

            -- Only display names change; created_by only to NULL through the account-deletion cascade (research R5).
            CREATE FUNCTION gozleme.publishers_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF TG_OP = 'DELETE' OR NEW.id IS DISTINCT FROM OLD.id OR NEW.organization_id IS DISTINCT FROM OLD.organization_id
                 OR NEW.handle IS DISTINCT FROM OLD.handle OR NEW.created_at IS DISTINCT FROM OLD.created_at
                 OR (NEW.created_by IS DISTINCT FROM OLD.created_by AND NEW.created_by IS NOT NULL) THEN
                RAISE EXCEPTION 'publisher change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $fn$;
            CREATE FUNCTION gozleme.capabilities_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF TG_OP = 'DELETE' OR NEW.id IS DISTINCT FROM OLD.id OR NEW.organization_id IS DISTINCT FROM OLD.organization_id
                 OR NEW.publisher_id IS DISTINCT FROM OLD.publisher_id OR NEW.name IS DISTINCT FROM OLD.name
                 OR NEW.created_at IS DISTINCT FROM OLD.created_at
                 OR (NEW.created_by IS DISTINCT FROM OLD.created_by AND NEW.created_by IS NOT NULL) THEN
                RAISE EXCEPTION 'capability change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.publishers_guard(), gozleme.capabilities_guard() FROM PUBLIC;
            CREATE TRIGGER publishers_guard BEFORE UPDATE OR DELETE ON gozleme.publishers
              FOR EACH ROW EXECUTE FUNCTION gozleme.publishers_guard();
            CREATE TRIGGER publishers_truncate BEFORE TRUNCATE ON gozleme.publishers
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER capabilities_guard BEFORE UPDATE OR DELETE ON gozleme.capabilities
              FOR EACH ROW EXECUTE FUNCTION gozleme.capabilities_guard();
            CREATE TRIGGER capabilities_truncate BEFORE TRUNCATE ON gozleme.capabilities
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();

            -- The claim state machine and who may take each step (research R2, R4), binding every login:
            --   operator = the owner's own login with no account context: insert blocked/earmarked, release an
            --              unused reservation, reclaim a held claim (publisher_id and held_at unchanged);
            --   member   = any other login with an account context (gozleme_app; its policies decide further):
            --              insert held, consume an earmark.
            -- released and reclaimed are terminal; nothing is ever deleted.
            CREATE FUNCTION gozleme.handle_claims_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              owner boolean := current_user = 'gozleme_migrator';
              account uuid := gozleme.current_account_id();
              operator boolean := owner AND session_user = 'gozleme_migrator' AND account IS NULL;
              member boolean := NOT owner AND account IS NOT NULL;
            BEGIN
              IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'handle claim change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              IF TG_OP = 'INSERT' THEN
                IF (NEW.status IN ('blocked', 'earmarked') AND operator) OR (NEW.status = 'held' AND member) THEN
                  RETURN NEW;
                END IF;
                RAISE EXCEPTION 'handle claim change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              IF NEW.id IS DISTINCT FROM OLD.id OR NEW.handle IS DISTINCT FROM OLD.handle OR NEW.created_at IS DISTINCT FROM OLD.created_at
                 OR NEW.organization_id IS DISTINCT FROM OLD.organization_id
                 OR NOT ((OLD.status = 'earmarked' AND NEW.status = 'held' AND member)
                      OR (OLD.status = 'held' AND NEW.status = 'reclaimed' AND operator
                          AND NEW.publisher_id IS NOT DISTINCT FROM OLD.publisher_id AND NEW.held_at IS NOT DISTINCT FROM OLD.held_at)
                      OR (OLD.status IN ('blocked', 'earmarked') AND NEW.status = 'released' AND operator)) THEN
                RAISE EXCEPTION 'handle claim change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.handle_claims_guard() FROM PUBLIC;
            CREATE TRIGGER handle_claims_guard BEFORE INSERT OR UPDATE OR DELETE ON gozleme.handle_claims
              FOR EACH ROW EXECUTE FUNCTION gozleme.handle_claims_guard();
            CREATE TRIGGER handle_claims_truncate BEFORE TRUNCATE ON gozleme.handle_claims
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();

            -- The alias is derived, never supplied: reserved in the capability's own statement with invoker rights.
            CREATE FUNCTION gozleme.capabilities_reserve_alias() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              INSERT INTO gozleme.alias_reservations (alias, capability_id, organization_id)
              SELECT p.handle || '/' || NEW.name, NEW.id, NEW.organization_id FROM gozleme.publishers p WHERE p.id = NEW.publisher_id;
              IF NOT FOUND THEN
                RAISE EXCEPTION 'capability change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NULL;
            END $fn$;
            CREATE FUNCTION gozleme.alias_reservations_insert_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF pg_trigger_depth() < 2 THEN -- only capabilities_reserve_alias may reserve an alias
                RAISE EXCEPTION 'alias reservation refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              RETURN NEW;
            END $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.capabilities_reserve_alias(), gozleme.alias_reservations_insert_guard() FROM PUBLIC;
            CREATE TRIGGER capabilities_reserve_alias AFTER INSERT ON gozleme.capabilities
              FOR EACH ROW EXECUTE FUNCTION gozleme.capabilities_reserve_alias();
            CREATE TRIGGER alias_reservations_insert_guard BEFORE INSERT ON gozleme.alias_reservations
              FOR EACH ROW EXECUTE FUNCTION gozleme.alias_reservations_insert_guard();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- One statement: the publishers/capabilities policies read handle_claims and the handle_claims policies
            -- read publishers, so no single-table order can drop them.
            DROP TABLE IF EXISTS gozleme.alias_reservations, gozleme.handle_claims, gozleme.capabilities, gozleme.publishers;
            DROP TABLE IF EXISTS gozleme.registry_origin;
            DROP TABLE IF EXISTS gozleme.publisher_registration_attempts;
            DROP FUNCTION IF EXISTS gozleme.alias_reservations_insert_guard();
            DROP FUNCTION IF EXISTS gozleme.capabilities_reserve_alias();
            DROP FUNCTION IF EXISTS gozleme.handle_claims_guard();
            DROP FUNCTION IF EXISTS gozleme.capabilities_guard();
            DROP FUNCTION IF EXISTS gozleme.publishers_guard();
            DROP FUNCTION IF EXISTS gozleme.immutable_row_guard();
            DROP FUNCTION IF EXISTS gozleme.account_has_role(uuid, text);
            """);
    }
}
