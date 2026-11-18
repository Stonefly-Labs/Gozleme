using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gozleme.Api.Persistence.Migrations;

/// <summary>
/// Immutable manifest revisions and signed attestations (GOZ-0007; data-model.md, research R7–R11, R15).
/// <list type="bullet">
/// <item><c>publisher_signing_keys</c>, <c>signing_key_retirements</c>, <c>manifest_revisions</c>,
/// <c>revision_attestations</c> (<c>TenantOwned</c>, forced RLS): member reads; revision, attestation and first-key
/// inserts need <c>publisher</c> and a held claim; successor keys and retirements are the operator's only. All four
/// are write-once for every login, including the owner (<c>immutable_row_guard</c>, GOZ-0000-IF-04).</item>
/// <item><c>manifest_publication_attempts</c> (<c>GlobalIdentity</c>): the shared publication budget, the same shape
/// as <c>manifest_validation_attempts</c>.</item>
/// <item><c>is_operator()</c>: not a definer; the owner's own login with no account context (the test
/// <c>handle_claims_guard</c> already uses), which <c>gozleme_app</c> can never satisfy (R9).</item>
/// <item>A deferred constraint trigger refuses to commit a revision without its attestation (FR-005).</item>
/// </list>
/// No foreign key points from these tables to <c>users</c> (R11), and none into them cascades or sets null (R10).
/// Additive DDL only: no data migration, new role, lift of the owner binding or disabled guard.
/// </summary>
internal sealed partial class ManifestRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- The operator (R9): the owner's own login with no account context. Not a definer, so a call from
            -- gozleme_app evaluates current_user = gozleme_app and is false.
            CREATE FUNCTION gozleme.is_operator() RETURNS boolean LANGUAGE sql STABLE
              SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT current_user = 'gozleme_migrator' AND session_user = 'gozleme_migrator' AND gozleme.current_account_id() IS NULL $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.is_operator() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION gozleme.is_operator() TO gozleme_app;

            CREATE TABLE gozleme.publisher_signing_keys (
              id uuid NOT NULL,
              organization_id uuid NOT NULL,
              publisher_id uuid NOT NULL,
              generation integer NOT NULL CONSTRAINT ck_publisher_signing_keys_generation CHECK (generation >= 1),
              key_id text NOT NULL,
              algorithm text NOT NULL CONSTRAINT ck_publisher_signing_keys_algorithm CHECK (algorithm = 'Ed25519'),
              public_key bytea NOT NULL CONSTRAINT ck_publisher_signing_keys_public_key CHECK (octet_length(public_key) = 32),
              encrypted_private_key bytea NOT NULL
                CONSTRAINT ck_publisher_signing_keys_encrypted_private_key CHECK (octet_length(encrypted_private_key) = 61 AND get_byte(encrypted_private_key, 0) = 1),
              created_at timestamptz NOT NULL,
              created_by uuid,
              CONSTRAINT pk_publisher_signing_keys PRIMARY KEY (id),
              CONSTRAINT ak_publisher_signing_keys_id_organization_id UNIQUE (id, organization_id),
              CONSTRAINT ix_publisher_signing_keys_publisher_id_generation UNIQUE (publisher_id, generation),
              CONSTRAINT ix_publisher_signing_keys_key_id UNIQUE (key_id),
              -- Anyone can recompute the key ID from the downloaded public key (R5, A2).
              CONSTRAINT ck_publisher_signing_keys_key_id CHECK (key_id = 'sha256:' || encode(sha256(public_key), 'hex')),
              CONSTRAINT fk_publisher_signing_keys_organizations FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_publisher_signing_keys_publishers FOREIGN KEY (publisher_id, organization_id)
                REFERENCES gozleme.publishers (id, organization_id) ON DELETE RESTRICT);
            CREATE INDEX ix_publisher_signing_keys_organization_id ON gozleme.publisher_signing_keys (organization_id);

            CREATE TABLE gozleme.signing_key_retirements (
              key_id uuid NOT NULL,
              organization_id uuid NOT NULL,
              successor_key_id uuid NOT NULL,
              reason text NOT NULL CONSTRAINT ck_signing_key_retirements_reason CHECK (reason IN ('lost', 'compromised')),
              retired_at timestamptz NOT NULL,
              CONSTRAINT pk_signing_key_retirements PRIMARY KEY (key_id),
              CONSTRAINT ix_signing_key_retirements_successor_key_id UNIQUE (successor_key_id),
              CONSTRAINT ck_signing_key_retirements_distinct CHECK (key_id <> successor_key_id),
              CONSTRAINT fk_signing_key_retirements_organizations FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_signing_key_retirements_key FOREIGN KEY (key_id, organization_id)
                REFERENCES gozleme.publisher_signing_keys (id, organization_id) ON DELETE RESTRICT,
              CONSTRAINT fk_signing_key_retirements_successor FOREIGN KEY (successor_key_id, organization_id)
                REFERENCES gozleme.publisher_signing_keys (id, organization_id) ON DELETE RESTRICT);
            CREATE INDEX ix_signing_key_retirements_organization_id ON gozleme.signing_key_retirements (organization_id);

            CREATE TABLE gozleme.manifest_revisions (
              id uuid NOT NULL,
              organization_id uuid NOT NULL,
              publisher_id uuid NOT NULL,
              capability_id uuid NOT NULL,
              sequence integer NOT NULL CONSTRAINT ck_manifest_revisions_sequence CHECK (sequence >= 1),
              version text NOT NULL CONSTRAINT ck_manifest_revisions_version CHECK (length(version) BETWEEN 5 AND 256),
              version_key text NOT NULL,
              family text NOT NULL CONSTRAINT ck_manifest_revisions_family CHECK (family IN ('mcp', 'a2a', 'api', 'event', 'skill', 'model')),
              envelope_summary jsonb NOT NULL,
              native_format text NOT NULL,
              native_format_version text NOT NULL,
              native_encoding text NOT NULL CONSTRAINT ck_manifest_revisions_native_encoding CHECK (native_encoding IN ('json', 'yaml', 'markdown')),
              native_document bytea NOT NULL CONSTRAINT ck_manifest_revisions_native_document CHECK (octet_length(native_document) <= 524288),
              native_digest text NOT NULL CONSTRAINT ck_manifest_revisions_native_digest CHECK (native_digest ~ '^sha256:[0-9a-f]{64}$'),
              native_byte_length integer NOT NULL,
              projection bytea NOT NULL,
              projection_version text NOT NULL CONSTRAINT ck_manifest_revisions_projection_version CHECK (projection_version = '1'),
              projection_digest text NOT NULL CONSTRAINT ck_manifest_revisions_projection_digest CHECK (projection_digest ~ '^sha256:[0-9a-f]{64}$'),
              adapter_id text NOT NULL,
              adapter_version text NOT NULL,
              rule_catalogue_version text NOT NULL,
              schema_bundle_version text NOT NULL,
              published_by uuid NOT NULL,
              published_at timestamptz NOT NULL,
              CONSTRAINT pk_manifest_revisions PRIMARY KEY (id),
              CONSTRAINT ak_manifest_revisions_id_organization_id UNIQUE (id, organization_id),
              CONSTRAINT ix_manifest_revisions_capability_id_sequence UNIQUE (capability_id, sequence),
              CONSTRAINT ix_manifest_revisions_capability_id_version_key UNIQUE (capability_id, version_key),
              CONSTRAINT ck_manifest_revisions_native_byte_length CHECK (native_byte_length = octet_length(native_document)),
              CONSTRAINT fk_manifest_revisions_organizations FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_manifest_revisions_publishers FOREIGN KEY (publisher_id, organization_id)
                REFERENCES gozleme.publishers (id, organization_id) ON DELETE RESTRICT,
              CONSTRAINT fk_manifest_revisions_capabilities FOREIGN KEY (capability_id, organization_id)
                REFERENCES gozleme.capabilities (id, organization_id) ON DELETE RESTRICT);
            CREATE INDEX ix_manifest_revisions_organization_id ON gozleme.manifest_revisions (organization_id);
            CREATE INDEX ix_manifest_revisions_publisher_id ON gozleme.manifest_revisions (publisher_id);

            CREATE TABLE gozleme.revision_attestations (
              revision_id uuid NOT NULL,
              organization_id uuid NOT NULL,
              signing_key_id uuid NOT NULL,
              statement_version smallint NOT NULL CONSTRAINT ck_revision_attestations_statement_version CHECK (statement_version = 1),
              statement bytea NOT NULL,
              signature bytea NOT NULL CONSTRAINT ck_revision_attestations_signature CHECK (octet_length(signature) = 64),
              created_at timestamptz NOT NULL,
              CONSTRAINT pk_revision_attestations PRIMARY KEY (revision_id),
              CONSTRAINT fk_revision_attestations_organizations FOREIGN KEY (organization_id) REFERENCES gozleme.organizations (id) ON DELETE RESTRICT,
              CONSTRAINT fk_revision_attestations_revisions FOREIGN KEY (revision_id, organization_id)
                REFERENCES gozleme.manifest_revisions (id, organization_id) ON DELETE RESTRICT,
              CONSTRAINT fk_revision_attestations_keys FOREIGN KEY (signing_key_id, organization_id)
                REFERENCES gozleme.publisher_signing_keys (id, organization_id) ON DELETE RESTRICT);
            CREATE INDEX ix_revision_attestations_organization_id ON gozleme.revision_attestations (organization_id);
            CREATE INDEX ix_revision_attestations_signing_key_id ON gozleme.revision_attestations (signing_key_id);

            -- Publications per account per clock minute (FR-019, R15): one shared count for every API replica.
            CREATE TABLE gozleme.manifest_publication_attempts (
              account_id uuid NOT NULL CONSTRAINT fk_manifest_publication_attempts_users REFERENCES gozleme.users (id) ON DELETE CASCADE,
              window_start timestamptz NOT NULL,
              attempts integer NOT NULL CONSTRAINT ck_manifest_publication_attempts_attempts CHECK (attempts > 0),
              CONSTRAINT pk_manifest_publication_attempts PRIMARY KEY (account_id, window_start));

            -- Write-once: gozleme_app reads and inserts only (R10).
            REVOKE ALL ON gozleme.publisher_signing_keys, gozleme.signing_key_retirements, gozleme.manifest_revisions,
              gozleme.revision_attestations FROM gozleme_app;
            GRANT SELECT, INSERT ON gozleme.publisher_signing_keys, gozleme.signing_key_retirements, gozleme.manifest_revisions,
              gozleme.revision_attestations TO gozleme_app;

            -- Insert guards: chronology from the database clock and the derivations the statement relies on, for every login.
            CREATE FUNCTION gozleme.publisher_signing_keys_insert_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF NEW.generation > 1 AND NOT (gozleme.is_operator() AND EXISTS (
                   SELECT 1 FROM gozleme.publisher_signing_keys k
                   WHERE k.publisher_id = NEW.publisher_id AND k.organization_id = NEW.organization_id AND k.generation = NEW.generation - 1)) THEN
                RAISE EXCEPTION 'signing key change refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              NEW.created_at := now();
              RETURN NEW;
            END $fn$;

            CREATE FUNCTION gozleme.signing_key_retirements_insert_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF NOT EXISTS (
                   SELECT 1 FROM gozleme.publisher_signing_keys retired JOIN gozleme.publisher_signing_keys successor
                     ON successor.publisher_id = retired.publisher_id AND successor.organization_id = retired.organization_id
                    AND successor.generation = retired.generation + 1
                   WHERE retired.id = NEW.key_id AND successor.id = NEW.successor_key_id AND retired.organization_id = NEW.organization_id) THEN
                RAISE EXCEPTION 'signing key retirement refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              NEW.retired_at := now();
              RETURN NEW;
            END $fn$;

            -- The capability's own publisher, version_key = version without +build, and a gap-free sequence (R8). The
            -- publication holds the per-capability advisory lock, so max + 1 here sees every committed revision.
            CREATE FUNCTION gozleme.manifest_revisions_insert_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM gozleme.capabilities c
                             WHERE c.id = NEW.capability_id AND c.organization_id = NEW.organization_id AND c.publisher_id = NEW.publisher_id)
                 OR NEW.version_key IS DISTINCT FROM split_part(NEW.version, '+', 1)
                 OR NEW.sequence IS DISTINCT FROM (SELECT coalesce(max(r.sequence), 0) + 1 FROM gozleme.manifest_revisions r WHERE r.capability_id = NEW.capability_id) THEN
                RAISE EXCEPTION 'manifest revision refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              NEW.published_at := now();
              RETURN NEW;
            END $fn$;

            CREATE FUNCTION gozleme.revision_attestations_insert_guard() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM gozleme.manifest_revisions r JOIN gozleme.publisher_signing_keys k
                               ON k.publisher_id = r.publisher_id AND k.organization_id = r.organization_id
                             WHERE r.id = NEW.revision_id AND r.organization_id = NEW.organization_id AND k.id = NEW.signing_key_id)
                 -- A retired key never signs again (FR-007a), whatever path inserts the attestation.
                 OR EXISTS (SELECT 1 FROM gozleme.signing_key_retirements x WHERE x.key_id = NEW.signing_key_id) THEN
                RAISE EXCEPTION 'revision attestation refused' USING ERRCODE = 'insufficient_privilege';
              END IF;
              NEW.created_at := now();
              RETURN NEW;
            END $fn$;

            -- FR-005: an unsigned revision cannot commit, even through SQL.
            CREATE FUNCTION gozleme.manifest_revisions_attested() RETURNS trigger
              LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM gozleme.revision_attestations a WHERE a.revision_id = NEW.id) THEN
                RAISE EXCEPTION 'manifest revision has no attestation' USING ERRCODE = 'integrity_constraint_violation';
              END IF;
              RETURN NULL;
            END $fn$;
            REVOKE EXECUTE ON FUNCTION gozleme.publisher_signing_keys_insert_guard(), gozleme.signing_key_retirements_insert_guard(),
              gozleme.manifest_revisions_insert_guard(), gozleme.revision_attestations_insert_guard(), gozleme.manifest_revisions_attested() FROM PUBLIC;

            CREATE TRIGGER publisher_signing_keys_insert_guard BEFORE INSERT ON gozleme.publisher_signing_keys
              FOR EACH ROW EXECUTE FUNCTION gozleme.publisher_signing_keys_insert_guard();
            CREATE TRIGGER signing_key_retirements_insert_guard BEFORE INSERT ON gozleme.signing_key_retirements
              FOR EACH ROW EXECUTE FUNCTION gozleme.signing_key_retirements_insert_guard();
            CREATE TRIGGER manifest_revisions_insert_guard BEFORE INSERT ON gozleme.manifest_revisions
              FOR EACH ROW EXECUTE FUNCTION gozleme.manifest_revisions_insert_guard();
            CREATE TRIGGER revision_attestations_insert_guard BEFORE INSERT ON gozleme.revision_attestations
              FOR EACH ROW EXECUTE FUNCTION gozleme.revision_attestations_insert_guard();
            CREATE CONSTRAINT TRIGGER manifest_revisions_attested AFTER INSERT ON gozleme.manifest_revisions
              DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION gozleme.manifest_revisions_attested();

            -- Immutability for every login, including the owner (R10).
            CREATE TRIGGER publisher_signing_keys_guard BEFORE UPDATE OR DELETE ON gozleme.publisher_signing_keys
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER publisher_signing_keys_truncate BEFORE TRUNCATE ON gozleme.publisher_signing_keys
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER signing_key_retirements_guard BEFORE UPDATE OR DELETE ON gozleme.signing_key_retirements
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER signing_key_retirements_truncate BEFORE TRUNCATE ON gozleme.signing_key_retirements
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER manifest_revisions_guard BEFORE UPDATE OR DELETE ON gozleme.manifest_revisions
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER manifest_revisions_truncate BEFORE TRUNCATE ON gozleme.manifest_revisions
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER revision_attestations_guard BEFORE UPDATE OR DELETE ON gozleme.revision_attestations
              FOR EACH ROW EXECUTE FUNCTION gozleme.immutable_row_guard();
            CREATE TRIGGER revision_attestations_truncate BEFORE TRUNCATE ON gozleme.revision_attestations
              FOR EACH STATEMENT EXECUTE FUNCTION gozleme.immutable_row_guard();

            ALTER TABLE gozleme.publisher_signing_keys ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.publisher_signing_keys FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.signing_key_retirements ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.signing_key_retirements FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.manifest_revisions ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.manifest_revisions FORCE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.revision_attestations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE gozleme.revision_attestations FORCE ROW LEVEL SECURITY;

            -- Keys: member reads; a member inserts generation 1 only (lazy creation, R7) with publisher and a held claim;
            -- the operator reads across tenants and inserts successors only (R9).
            CREATE POLICY publisher_signing_keys_select ON gozleme.publisher_signing_keys FOR SELECT USING (CASE
              WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY publisher_signing_keys_insert ON gozleme.publisher_signing_keys FOR INSERT WITH CHECK (CASE
              WHEN gozleme.is_operator() THEN generation > 1 AND created_by IS NULL
              ELSE generation = 1 AND gozleme.account_has_role(organization_id, 'publisher')
               AND created_by = (SELECT gozleme.current_account_id())
               AND EXISTS (SELECT 1 FROM gozleme.handle_claims c WHERE c.publisher_id = publisher_signing_keys.publisher_id AND c.status = 'held') END);

            CREATE POLICY signing_key_retirements_select ON gozleme.signing_key_retirements FOR SELECT USING (CASE
              WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END);
            CREATE POLICY signing_key_retirements_insert ON gozleme.signing_key_retirements FOR INSERT WITH CHECK (gozleme.is_operator());

            -- Revisions and attestations: member reads; inserts need publisher and a held claim, so suspension is
            -- enforced here as well as in the handler (FR-015).
            CREATE POLICY manifest_revisions_select ON gozleme.manifest_revisions FOR SELECT USING (
              organization_id IN (SELECT gozleme.account_organizations()));
            CREATE POLICY manifest_revisions_insert ON gozleme.manifest_revisions FOR INSERT WITH CHECK (
              gozleme.account_has_role(organization_id, 'publisher') AND published_by = (SELECT gozleme.current_account_id())
              AND EXISTS (SELECT 1 FROM gozleme.handle_claims c WHERE c.publisher_id = manifest_revisions.publisher_id AND c.status = 'held'));

            CREATE POLICY revision_attestations_select ON gozleme.revision_attestations FOR SELECT USING (
              organization_id IN (SELECT gozleme.account_organizations()));
            CREATE POLICY revision_attestations_insert ON gozleme.revision_attestations FOR INSERT WITH CHECK (
              gozleme.account_has_role(organization_id, 'publisher')
              AND EXISTS (SELECT 1 FROM gozleme.manifest_revisions r JOIN gozleme.handle_claims c ON c.publisher_id = r.publisher_id
                          WHERE r.id = revision_attestations.revision_id AND c.status = 'held'));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS gozleme.revision_attestations, gozleme.manifest_revisions, gozleme.signing_key_retirements,
              gozleme.publisher_signing_keys, gozleme.manifest_publication_attempts;
            DROP FUNCTION IF EXISTS gozleme.manifest_revisions_attested();
            DROP FUNCTION IF EXISTS gozleme.revision_attestations_insert_guard();
            DROP FUNCTION IF EXISTS gozleme.manifest_revisions_insert_guard();
            DROP FUNCTION IF EXISTS gozleme.signing_key_retirements_insert_guard();
            DROP FUNCTION IF EXISTS gozleme.publisher_signing_keys_insert_guard();
            DROP FUNCTION IF EXISTS gozleme.is_operator();
            """);
    }
}
