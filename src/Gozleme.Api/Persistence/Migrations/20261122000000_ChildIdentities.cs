using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Child capability identities and projection generations (GOZ-0008; data-model.md, research R4–R6, R13).
/// <list type="bullet">
/// <item><c>capability_children</c>, <c>revision_children</c>, <c>projection_generations</c> (<c>TenantOwned</c>,
/// forced RLS): member reads; inserts need <c>publisher</c> and a held claim, and members insert non-duplicate
/// children and generation 1 only; the operator inserts install-step duplicates and later generations. All three are
/// write-once for every login, including the owner (<c>immutable_row_guard</c>).</item>
/// <item>The database checks the C# assignment (R4): one matchable child per key (a partial unique index on the
/// guard-derived <c>native_name_digest</c>, maintainer 2026-11-19 "native-name-bound"), a statement-level check of
/// every child revision record against its projection entry, and deferred completeness triggers.</item>
/// <item><c>manifest_revisions_select</c> gains the operator branch, so the <c>migrate</c> install step and
/// <c>projection-regenerate</c> read projections (maintainer-approved A2, security-review item).</item>
/// </list>
/// Trigger functions only: no callable function, no definer. Additive DDL and one replaced policy; no data migration
/// (the install step in <c>migrate</c> gives existing revisions their children, R6).
/// </summary>
internal sealed partial class ChildIdentities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE gozleme.capability_children (
              id uuid NOT NULL,
              organization_id uuid NOT NULL,
              capability_id uuid NOT NULL,
              kind text NOT NULL CONSTRAINT ck_capability_children_kind
                CHECK (kind IN ('tool', 'resource', 'resource-template', 'prompt', 'skill', 'operation', 'webhook', 'channel')),
              -- No length bound: validate bounds neither value names nor derived "<METHOD> <path>" names (maintainer
              -- 2026-11-19), so the match index keys on the digest below and never on the name itself.
              native_name text NOT NULL,
              native_name_digest bytea NOT NULL,
              is_duplicate boolean NOT NULL DEFAULT false,
              ordinal integer NOT NULL CONSTRAINT ck_capability_children_ordinal CHECK (ordinal >= 1),
              first_revision_id uuid NOT NULL,
              created_at timestamptz NOT NULL,
              CONSTRAINT pk_capability_children PRIMARY KEY (id),
              CONSTRAINT ak_capability_children_id_organization_id UNIQUE (id, organization_id),
              CONSTRAINT ix_capability_children_capability_id_ordinal UNIQUE (capability_id, ordinal),
              CONSTRAINT fk_capability_children_organizations FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_capability_children_capabilities FOREIGN KEY (capability_id, organization_id)
                REFERENCES gozleme.capabilities (id, organization_id) ON DELETE RESTRICT,
              CONSTRAINT fk_capability_children_first_revision FOREIGN KEY (first_revision_id, organization_id)
                REFERENCES gozleme.manifest_revisions (id, organization_id) ON DELETE RESTRICT);
            CREATE INDEX ix_capability_children_organization_id ON gozleme.capability_children (organization_id);
            CREATE INDEX ix_capability_children_first_revision_id ON gozleme.capability_children (first_revision_id);
            -- One matchable child per (kind, native name) of a capability (R4); duplicates are never matched.
            CREATE UNIQUE INDEX ix_capability_children_match ON gozleme.capability_children (capability_id, kind, native_name_digest)
              WHERE NOT is_duplicate;

            CREATE TABLE gozleme.revision_children (
              revision_id uuid NOT NULL,
              entry_index integer NOT NULL CONSTRAINT ck_revision_children_entry_index CHECK (entry_index >= 0),
              organization_id uuid NOT NULL,
              child_id uuid NOT NULL,
              kind text NOT NULL,
              native_name text NOT NULL,
              title text,
              CONSTRAINT pk_revision_children PRIMARY KEY (revision_id, entry_index),
              CONSTRAINT ix_revision_children_revision_id_child_id UNIQUE (revision_id, child_id),
              CONSTRAINT fk_revision_children_organizations FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_revision_children_revisions FOREIGN KEY (revision_id, organization_id)
                REFERENCES gozleme.manifest_revisions (id, organization_id) ON DELETE RESTRICT,
              CONSTRAINT fk_revision_children_children FOREIGN KEY (child_id, organization_id)
                REFERENCES gozleme.capability_children (id, organization_id) ON DELETE RESTRICT);
            CREATE INDEX ix_revision_children_child_id ON gozleme.revision_children (child_id);
            CREATE INDEX ix_revision_children_organization_id ON gozleme.revision_children (organization_id);

            CREATE TABLE gozleme.projection_generations (
              id uuid NOT NULL,
              organization_id uuid NOT NULL,
              revision_id uuid NOT NULL,
              generation integer NOT NULL CONSTRAINT ck_projection_generations_generation CHECK (generation >= 1),
              adapter_id text NOT NULL,
              adapter_version text NOT NULL,
              projection_version text NOT NULL,
              projection bytea,
              projection_digest text,
              created_at timestamptz NOT NULL,
              CONSTRAINT pk_projection_generations PRIMARY KEY (id),
              CONSTRAINT ix_projection_generations_revision_id_generation UNIQUE (revision_id, generation),
              -- Generation 1 is the revision's own attested projection: no copy that could diverge (FR-009).
              CONSTRAINT ck_projection_generations_first CHECK ((generation = 1) = (projection IS NULL AND projection_digest IS NULL)),
              CONSTRAINT ck_projection_generations_digest CHECK (projection_digest IS NULL OR projection_digest = 'sha256:' || encode(sha256(projection), 'hex')),
              CONSTRAINT fk_projection_generations_organizations FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_projection_generations_revisions FOREIGN KEY (revision_id, organization_id)
                REFERENCES gozleme.manifest_revisions (id, organization_id) ON DELETE RESTRICT);
            CREATE INDEX ix_projection_generations_organization_id ON gozleme.projection_generations (organization_id);

            -- Write-once: gozleme_app reads and inserts only.
            REVOKE ALL ON gozleme.capability_children, gozleme.revision_children, gozleme.projection_generations FROM gozleme_app;
            GRANT SELECT, INSERT ON gozleme.capability_children, gozleme.revision_children, gozleme.projection_generations TO gozleme_app;

            -- A child of the revision's own capability, the next gap-free ordinal (the capability lock is held), and the
            -- match digest and chronology derived here for every login.
            CREATE FUNCTION gozleme.capability_children_insert_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM gozleme.manifest_revisions r
                             WHERE r.id = NEW.first_revision_id AND r.organization_id = NEW.organization_id AND r.capability_id = NEW.capability_id)
                 OR NEW.ordinal IS DISTINCT FROM (SELECT coalesce(max(c.ordinal), 0) + 1 FROM gozleme.capability_children c WHERE c.capability_id = NEW.capability_id) THEN
                RAISE EXCEPTION 'capability child refused' USING ERRCODE = 'insufficient_privilege', TABLE = 'capability_children';
              END IF;
              NEW.native_name_digest := sha256(convert_to(NEW.native_name, 'UTF8'));
              NEW.created_at := now();
              RETURN NEW;
            END $fn$;

            -- Generation max + 1; generation 1 carries the revision's adapter and projection version; a later generation
            -- is the operator's only and declares the same (kind, nativeName) multiset as the stored projection (FR-011a).
            CREATE FUNCTION gozleme.projection_generations_insert_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              rev record;
            BEGIN
              SELECT r.adapter_id, r.adapter_version, r.projection_version, r.projection INTO rev
              FROM gozleme.manifest_revisions r WHERE r.id = NEW.revision_id AND r.organization_id = NEW.organization_id;
              IF NOT FOUND
                 OR NEW.generation IS DISTINCT FROM (SELECT coalesce(max(g.generation), 0) + 1 FROM gozleme.projection_generations g WHERE g.revision_id = NEW.revision_id) THEN
                RAISE EXCEPTION 'projection generation refused' USING ERRCODE = 'insufficient_privilege', TABLE = 'projection_generations';
              END IF;
              IF NEW.generation = 1 THEN
                IF NEW.adapter_id IS DISTINCT FROM rev.adapter_id OR NEW.adapter_version IS DISTINCT FROM rev.adapter_version
                   OR NEW.projection_version IS DISTINCT FROM rev.projection_version THEN
                  RAISE EXCEPTION 'projection generation refused' USING ERRCODE = 'insufficient_privilege', TABLE = 'projection_generations';
                END IF;
              ELSIF NOT gozleme.is_operator() OR NEW.projection IS NULL OR EXISTS (
                  (SELECT e ->> 'kind', e ->> 'nativeName' FROM jsonb_array_elements(coalesce(convert_from(rev.projection, 'UTF8')::jsonb -> 'entries', '[]'::jsonb)) e
                   EXCEPT ALL
                   SELECT e ->> 'kind', e ->> 'nativeName' FROM jsonb_array_elements(coalesce(convert_from(NEW.projection, 'UTF8')::jsonb -> 'entries', '[]'::jsonb)) e)
                  UNION ALL
                  (SELECT e ->> 'kind', e ->> 'nativeName' FROM jsonb_array_elements(coalesce(convert_from(NEW.projection, 'UTF8')::jsonb -> 'entries', '[]'::jsonb)) e
                   EXCEPT ALL
                   SELECT e ->> 'kind', e ->> 'nativeName' FROM jsonb_array_elements(coalesce(convert_from(rev.projection, 'UTF8')::jsonb -> 'entries', '[]'::jsonb)) e)) THEN
                RAISE EXCEPTION 'projection generation refused' USING ERRCODE = 'insufficient_privilege', TABLE = 'projection_generations';
              END IF;
              NEW.created_at := now();
              RETURN NEW;
            END $fn$;

            -- R4: every child revision record equals its projection entry, and its child is the capability's child of that
            -- kind (and native name, unless an install-step duplicate). Each affected projection is parsed once per
            -- statement. A duplicate child appears only in the revision that created it, and only the operator records it.
            CREATE FUNCTION gozleme.revision_children_entry_check() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF EXISTS (
                  WITH revs AS MATERIALIZED (
                    SELECT r.id, r.organization_id, r.capability_id, convert_from(r.projection, 'UTF8')::jsonb -> 'entries' AS entries
                    FROM gozleme.manifest_revisions r WHERE r.id IN (SELECT DISTINCT n.revision_id FROM new_rows n))
                  SELECT 1 FROM new_rows n
                    LEFT JOIN revs v ON v.id = n.revision_id AND v.organization_id = n.organization_id
                    LEFT JOIN gozleme.capability_children c ON c.id = n.child_id AND c.organization_id = n.organization_id
                  WHERE v.id IS NULL OR c.id IS NULL
                     OR c.capability_id <> v.capability_id OR c.kind <> n.kind
                     OR (NOT c.is_duplicate AND c.native_name <> n.native_name)
                     OR (c.is_duplicate AND (NOT gozleme.is_operator() OR c.first_revision_id <> n.revision_id))
                     OR jsonb_typeof(v.entries) IS DISTINCT FROM 'array' OR n.entry_index >= jsonb_array_length(v.entries)
                     OR (v.entries -> n.entry_index ->> 'kind') IS DISTINCT FROM n.kind
                     OR (v.entries -> n.entry_index ->> 'nativeName') IS DISTINCT FROM n.native_name
                     OR (v.entries -> n.entry_index ->> 'title') IS DISTINCT FROM n.title) THEN
                RAISE EXCEPTION 'revision child refused' USING ERRCODE = 'insufficient_privilege', TABLE = 'revision_children';
              END IF;
              RETURN NULL;
            END $fn$;

            -- At commit, a child has its record in the revision that created it.
            CREATE FUNCTION gozleme.capability_children_recorded() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM gozleme.revision_children rc WHERE rc.revision_id = NEW.first_revision_id AND rc.child_id = NEW.id) THEN
                RAISE EXCEPTION 'capability child has no revision record' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              RETURN NULL;
            END $fn$;

            -- FR-007, SC-002: at commit, a revision published from now on has generation 1 and one record per entry.
            CREATE FUNCTION gozleme.manifest_revisions_children() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE
              entries jsonb := convert_from(NEW.projection, 'UTF8')::jsonb -> 'entries';
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM gozleme.projection_generations g WHERE g.revision_id = NEW.id AND g.generation = 1)
                 OR (SELECT count(*) FROM gozleme.revision_children rc WHERE rc.revision_id = NEW.id)
                    <> (CASE WHEN jsonb_typeof(entries) = 'array' THEN jsonb_array_length(entries) ELSE 0 END) THEN
                RAISE EXCEPTION 'manifest revision has no complete children' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              RETURN NULL;
            END $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.capability_children_insert_guard(), gozleme.projection_generations_insert_guard(),
              gozleme.revision_children_entry_check(), gozleme.capability_children_recorded(), gozleme.manifest_revisions_children() FROM PUBLIC;

            CREATE TRIGGER capability_children_insert_guard BEFORE INSERT ON gozleme.capability_children
              FOR EACH ROW EXECUTE FUNCTION gozleme.capability_children_insert_guard();
            CREATE TRIGGER projection_generations_insert_guard BEFORE INSERT ON gozleme.projection_generations
              FOR EACH ROW EXECUTE FUNCTION gozleme.projection_generations_insert_guard();
            CREATE TRIGGER revision_children_entry_check AFTER INSERT ON gozleme.revision_children
              REFERENCING NEW TABLE AS new_rows FOR EACH STATEMENT EXECUTE FUNCTION gozleme.revision_children_entry_check();
            CREATE CONSTRAINT TRIGGER capability_children_recorded AFTER INSERT ON gozleme.capability_children
              DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION gozleme.capability_children_recorded();
            CREATE CONSTRAINT TRIGGER manifest_revisions_children AFTER INSERT ON gozleme.manifest_revisions
              DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION gozleme.manifest_revisions_children();

            -- Immutability for every login, including the owner (FR-003, FR-017).
            CREATE TRIGGER capability_children_guard BEFORE UPDATE OR DELETE ON gozleme.capability_children
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER capability_children_truncate BEFORE TRUNCATE ON gozleme.capability_children
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER revision_children_guard BEFORE UPDATE OR DELETE ON gozleme.revision_children
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER revision_children_truncate BEFORE TRUNCATE ON gozleme.revision_children
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER projection_generations_guard BEFORE UPDATE OR DELETE ON gozleme.projection_generations
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER projection_generations_truncate BEFORE TRUNCATE ON gozleme.projection_generations
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();

            ALTER TABLE gozleme.capability_children ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.capability_children FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.revision_children ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.revision_children FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.projection_generations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.projection_generations FORCE ROW LEVEL SECURITY;

            -- Member reads; the operator reads across tenants (install step, re-projection).
            CREATE POLICY capability_children_select ON gozleme.capability_children FOR SELECT USING (CASE
              WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY revision_children_select ON gozleme.revision_children FOR SELECT USING (CASE
              WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY projection_generations_select ON gozleme.projection_generations FOR SELECT USING (CASE
              WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);

            -- Inserts: publisher and a held claim for the capability's publisher, as for revisions (FR-018); members insert
            -- non-duplicate children and generation 1 only.
            CREATE POLICY capability_children_insert ON gozleme.capability_children FOR INSERT WITH CHECK (CASE
              WHEN gozleme.is_operator() THEN true
              ELSE NOT is_duplicate AND gozleme.account_has_role(organization_id, 'publisher')
               AND EXISTS (SELECT 1 FROM gozleme.capabilities cap JOIN gozleme.handle_claims c ON c.publisher_id = cap.publisher_id
                           WHERE cap.id = capability_children.capability_id AND c.status = 'held') END);
            CREATE POLICY revision_children_insert ON gozleme.revision_children FOR INSERT WITH CHECK (CASE
              WHEN gozleme.is_operator() THEN true
              ELSE gozleme.account_has_role(organization_id, 'publisher')
               AND EXISTS (SELECT 1 FROM gozleme.manifest_revisions r JOIN gozleme.handle_claims c ON c.publisher_id = r.publisher_id
                           WHERE r.id = revision_children.revision_id AND c.status = 'held') END);
            CREATE POLICY projection_generations_insert ON gozleme.projection_generations FOR INSERT WITH CHECK (CASE
              WHEN gozleme.is_operator() THEN true
              ELSE generation = 1 AND gozleme.account_has_role(organization_id, 'publisher')
               AND EXISTS (SELECT 1 FROM gozleme.manifest_revisions r JOIN gozleme.handle_claims c ON c.publisher_id = r.publisher_id
                           WHERE r.id = projection_generations.revision_id AND c.status = 'held') END);

            -- A2 (maintainer-approved, security-review item): the operator reads revisions for the install step and
            -- projection-regenerate. gozleme_app can never satisfy is_operator().
            DROP POLICY manifest_revisions_select ON gozleme.manifest_revisions;
            CREATE POLICY manifest_revisions_select ON gozleme.manifest_revisions FOR SELECT USING (CASE
              WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY manifest_revisions_select ON gozleme.manifest_revisions;
            CREATE POLICY manifest_revisions_select ON gozleme.manifest_revisions FOR SELECT USING (
              organization_id IN (SELECT gozleme.account_organizations()));
            DROP TRIGGER IF EXISTS manifest_revisions_children ON gozleme.manifest_revisions;
            DROP TABLE IF EXISTS gozleme.revision_children, gozleme.projection_generations, gozleme.capability_children;
            DROP FUNCTION IF EXISTS gozleme.manifest_revisions_children();
            DROP FUNCTION IF EXISTS gozleme.capability_children_recorded();
            DROP FUNCTION IF EXISTS gozleme.revision_children_entry_check();
            DROP FUNCTION IF EXISTS gozleme.projection_generations_insert_guard();
            DROP FUNCTION IF EXISTS gozleme.capability_children_insert_guard();
            """);
    }
}
