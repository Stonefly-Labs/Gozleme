---

description: "Task list for GOZ-0007 Immutable revisions and signed attestations"
---

# Tasks: Immutable Revisions and Signed Attestations

**Input**: Design documents from `docs/specs/007-immutable-revisions/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R20), data-model.md, contracts/ (attestation-statement-v1.md, signing-keys.md, revision-api.openapi.yaml, permissions-delta.md), quickstart.md

**Tests**: Required. The spec demands proof that 100% of mutation attempts are refused as both logins (SC-002), that every tamper case fails with the expected reason (SC-003), independent verification of a download (SC-004), a no-secret scan of telemetry, logs, screenshots and errors (SC-005), the role × action matrix and the isolation matrix (SC-006), performance bounds (SC-007), succession proofs (SC-008), regression of all earlier suites (SC-009) and a cumulative browser demo (FR-025; Constitution IV/VI).

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently. All five stories are P1 and run in spec order: US1, US2, US3, US4, US5. Storage (the migration, with its immutability triggers and insert policies), the pure signing core, the key vault, the permission rows and the publish/read endpoints with their full authorization order are foundational, because every story uses them. Each later story adds its own proofs and the product surface it needs. Operator key succession (Q5) belongs to US3 (historical provenance survives a key change).

**Stop rule (plan "Vendoring risk", research R1)**: T001–T002 verify the facts marked **verify at vendoring**. If libsodium does not load in the `mcr.microsoft.com/dotnet/aspnet:10.0` runtime image or in CI, if the RFC 8032 test 1 vector does not sign and verify, or if NSec cannot import and export a raw 32-byte private key seed for AES-GCM wrapping, record the difference in `progress.yaml` `blockers` with evidence and stop for maintainer review. Never fall back silently to another library, algorithm or framing (P1 and P2 are maintainer decisions).

**Agent choices A1–A7** (research R4, R5, R11–R13, R18) were ruled on by the maintainer on 2026-11-18 before implementation: A1–A6 ratified as planned; A7 kept (the seed creates no revision), but the demo publishes to the seeded capability `manifest-demo` with run-unique pre-release versions instead of a run-unique capability (R18).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)

## Path Conventions

API `src/Gozleme.Api/` (new module `src/Gozleme.Api/Revisions/`), API tests `tests/Gozleme.Api.Tests/Revisions/` (unit) and `tests/Gozleme.Api.IntegrationTests/` (Testcontainers, PostgreSQL 17), web `web/src/`, demo `e2e/`, deployment `compose.yaml`, `.env.example`, `deploy/helm/gozleme/`.

---

## Phase 1: Setup (Vendoring, package, configuration)

**Purpose**: Pin the one new dependency, verify the "verify at vendoring" facts, and wire the key-encryption key configuration before any code depends on them

- [X] T001 Add `NSec.Cryptography` 26.4.0 to `Directory.Packages.props` (it brings `libsodium` `[1.0.22, 1.0.23)`) and reference it from `src/Gozleme.Api/Gozleme.Api.csproj`. Run `dotnet list package --vulnerable --include-transitive` and record the output in `docs/specs/007-immutable-revisions/evidence/dependency-check.txt` (research R1, maintainer P1)
- [X] T002 Spike NSec in `tests/Gozleme.Api.Tests/Revisions/Ed25519VendoringTests.cs` (kept as a permanent smoke test): (a) sign the RFC 8032 section 7.1 TEST 1 message (empty) with its secret key and compare the signature byte-for-byte, then verify it; (b) import a raw 32-byte private key seed (`KeyBlobFormat.RawPrivateKey`) with `KeyExportPolicies.AllowPlaintextExport` for creation and `KeyExportPolicies.None` for signing, and export the raw 32-byte public key; (c) export the public key as SubjectPublicKeyInfo PEM (RFC 8410) and confirm `openssl pkey -pubin` accepts it. Run the test in CI and inside the built `src/Gozleme.Api/Dockerfile` runtime image (the runtime image has no SDK, so add a `signing-selftest` branch to `src/Gozleme.Api/Program.cs` that signs and verifies the same vector and exits 0 or 1, and run it with `docker compose run --rm api signing-selftest`). Record results in `research.md` R1 under a "Verified at vendoring" note. Any failure → blocker and stop (stop rule)
- [X] T003 [P] Add NSec.Cryptography (MIT) and libsodium (ISC) with their licence texts and source URLs to `THIRD-PARTY-NOTICES.md`
- [X] T004 [P] Create `src/Gozleme.Api/Revisions/Signing/SigningOptions.cs`: `Gozleme:Signing:KeyEncryptionKey` ("base64 of exactly 32 random bytes"; optional — absent or malformed means publication is unavailable, never a start-up failure; research R6) and `src/Gozleme.Api/Revisions/RevisionOptions.cs` (or extend `ManifestOptions` in `src/Gozleme.Api/Manifests/ManifestValidationLimiter.cs` if that is where `Gozleme:Manifests` binds): `PublishPerMinute` default "10", range "1–10,000", and `RevisionPageSize` default "50", "maximum 100", both validated on start. Add the defaults to `src/Gozleme.Api/appsettings.json` (no KEK value there)
- [X] T005 [P] Add `GOZLEME_SIGNING_KEK` → `Gozleme__Signing__KeyEncryptionKey` to the `api` and `migrate` services in `compose.yaml`, and a throwaway value to `.env.example` with a comment that labels it local-only and shows `openssl rand -base64 32` (contracts/signing-keys.md)
- [X] T006 [P] Add `signing.existingSecret` (default `gozleme-signing`) to `deploy/helm/gozleme/values.yaml`, mount its key `signing-key-encryption-key` as `Gozleme__Signing__KeyEncryptionKey` in `deploy/helm/gozleme/templates/api.yaml` and `deploy/helm/gozleme/templates/migrate-job.yaml` (with `optional: true` on the API so it still boots without it), and document creation and the operator succession Job in `deploy/helm/gozleme/NOTES.txt` and the chart README. Chart values never hold the value (FR-007)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Storage with database-enforced immutability and atomicity, the pure signing core, the key vault, permissions, telemetry, audit and the publish/read endpoints in the full authorization order that every story uses

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Storage

- [X] T007 Create migration `src/Gozleme.Api/Persistence/Migrations/20261121000000_ManifestRevisions.cs` (additive DDL only, schema `gozleme`, following the GOZ-0003 to GOZ-0006 migration rules) with table `publisher_signing_keys` per data-model.md: `id` uuid PK; `organization_id`, `publisher_id` with FK (`publisher_id`, `organization_id`) → `publishers`; `generation` integer "≥ 1; `UNIQUE (publisher_id, generation)`"; `key_id` text `UNIQUE` "`sha256:` + 64 lowercase hex of the raw public key"; `algorithm` text "`CHECK (algorithm = 'Ed25519')`"; `public_key` bytea "`CHECK (octet_length = 32)`"; `encrypted_private_key` bytea "`0x01 ‖ nonce ‖ ct ‖ tag`, `CHECK (octet_length = 61)`"; `created_at` timestamptz; `created_by` uuid NULL "no FK" (R11)
- [X] T008 In the same migration add `signing_key_retirements`: `key_id` uuid PK FK → `publisher_signing_keys(id)` ("one retirement per key"); `organization_id` uuid; `successor_key_id` uuid `UNIQUE` FK → `publisher_signing_keys(id)`; `reason` text "`CHECK (reason IN ('lost', 'compromised'))`"; `retired_at` timestamptz
- [X] T009 In the same migration add `manifest_revisions`: `id` uuid PK; `organization_id`, `publisher_id`, `capability_id` with FK (`capability_id`, `organization_id`) → `capabilities`; `sequence` integer "≥ 1; `UNIQUE (capability_id, sequence)`"; `version` text; `version_key` text with `UNIQUE (capability_id, version_key)`; `family` text (one of the six families, CHECK); `envelope_summary` jsonb; `native_format`, `native_format_version`, `native_encoding` text; `native_document` bytea "`octet_length ≤ 524288`"; `native_digest` text "`sha256:<hex64>`; not unique"; `native_byte_length` integer "`= octet_length(native_document)` (CHECK)"; `projection` bytea; `projection_version` text "`'1'`"; `projection_digest` text `sha256:<hex64>`; `adapter_id`, `adapter_version`, `rule_catalogue_version`, `schema_bundle_version` text; `published_by` uuid "no FK" (R11); `published_at` timestamptz. No FK into these tables uses `ON DELETE CASCADE` or `SET NULL` (R10)
- [X] T010 In the same migration add `revision_attestations`: `revision_id` uuid PK with FK (`revision_id`, `organization_id`) → `manifest_revisions`; `organization_id` uuid; `signing_key_id` uuid FK → `publisher_signing_keys(id)`; `statement_version` smallint "`CHECK (= 1)`"; `statement` bytea; `signature` bytea "`CHECK (octet_length = 64)`"; `created_at` timestamptz. And `manifest_publication_attempts` with the same columns and SQL as `manifest_validation_attempts` (`account_id` FK `users` `ON DELETE CASCADE`, `window_start`, `attempts`) (R15)
- [X] T011 In the same migration add `gozleme.is_operator()`: `STABLE`, not `SECURITY DEFINER`, `SET search_path = pg_catalog, pg_temp`, `EXECUTE` revoked from `PUBLIC` and granted as needed; true exactly when "`current_user = 'gozleme_migrator' AND session_user = 'gozleme_migrator' AND gozleme.current_account_id() IS NULL`" (R9; security-review item)
- [X] T012 In the same migration add the insert-guard triggers (all `SET search_path = pg_catalog, pg_temp`, `EXECUTE` revoked from `PUBLIC`): `manifest_revisions_insert_guard` (sets `published_at := now()`; checks that `publisher_id` is the capability's publisher, that `version_key` equals `version` with any `+build` suffix removed, and that `sequence = coalesce(max(sequence), 0) + 1` for the capability); `revision_attestations_insert_guard` (key belongs to the revision's publisher; `created_at := now()`); `publisher_signing_keys_insert_guard` (`created_at := now()`; generation n > 1 only when `is_operator()` and generation n − 1 exists); `signing_key_retirements_insert_guard` (same publisher and organization for both keys, successor generation = retired + 1, `retired_at := now()`)
- [X] T013 In the same migration add the immutability and atomicity triggers: `BEFORE UPDATE OR DELETE` row triggers and `BEFORE TRUNCATE` statement triggers running the existing `gozleme.immutable_row_guard()` on all four tables (binds the owner too; GOZ-0000-IF-04), and the `manifest_revisions_attested` constraint trigger `DEFERRABLE INITIALLY DEFERRED` `AFTER INSERT` that raises at commit when no `revision_attestations` row exists for the revision (FR-005, R10)
- [X] T014 In the same migration enable and **force** row-level security on the four tenant tables and add policies: reads member-level (`organization_id IN (SELECT gozleme.account_organizations())`) or `is_operator()` for keys and retirements; inserts on `manifest_revisions` and `revision_attestations` only when the account holds `publisher` in `organization_id`, a `held` handle claim names `publisher_id`, and (revisions) `published_by = gozleme.current_account_id()`; inserts on `publisher_signing_keys` for members only at generation 1 with the same role and held-claim check and `created_by = current_account_id()`, or for `is_operator()` at generation > 1; inserts on `signing_key_retirements` for `is_operator()` only. Grant `gozleme_app` `SELECT, INSERT` only on the four tables and `SELECT, INSERT, UPDATE` on `manifest_publication_attempts` as for the validation table (data-model.md)
- [X] T015 Add the five entities and their mappings to `src/Gozleme.Api/Persistence/GozlemeDbContext.cs` (`encrypted_private_key` mapped but never projected by any read query) and regenerate `src/Gozleme.Api/Persistence/Migrations/GozlemeDbContextModelSnapshot.cs` plus the migration's `.Designer.cs`
- [X] T016 Add the four tables to `TableClassification.TenantOwned` and `manifest_publication_attempts` to `GlobalIdentity` in `src/Gozleme.Api/Persistence/TableClassification.cs`; leave `DefinerHelpers` unchanged; update `src/Gozleme.Api/Persistence/RlsCatalog.cs` for the new forced tables and the `is_operator()` predicate (R19)

### Pure signing core

- [X] T017 [P] Create `src/Gozleme.Api/Revisions/CanonicalJson.cs`: the RFC 8785 subset of research R3 — objects, arrays, strings, booleans, `null`, and integers "with magnitude ≤ 2^53 − 1"; members sorted by UTF-16 code units, recursively; strings escaped as ECMAScript `JSON.stringify` (`\" \\ \b \f \n \r \t`, other U+0000–U+001F as `\u00xx` lowercase hex, everything else literal UTF-8); no BOM, no whitespace; throws on a non-integer number, an out-of-range integer, a duplicate member or a lone surrogate. Exposes `byte[] Canonicalize(JsonNode)` and `bool IsCanonical(ReadOnlySpan<byte>)`
- [X] T018 [P] Create `src/Gozleme.Api/Revisions/VersionKey.cs`: `VersionKey.From(string semver)` strips any `+build` suffix (research R8: equality of the remaining string is SemVer 2.0.0 precedence equality)
- [X] T019 [P] Create `src/Gozleme.Api/Revisions/Signing/Ed25519Signer.cs` over NSec: create a key from a fresh 32-byte seed, sign, verify, derive the key ID `sha256:` + lowercase hex of SHA-256 over the 32-byte raw public key (R5, A2), and export SubjectPublicKeyInfo PEM (RFC 8410). Private seeds are held only for one operation and zeroed/disposed after (R6)
- [X] T020 Create `src/Gozleme.Api/Revisions/AttestationStatement.cs`: build statement v1 with exactly the members of contracts/attestation-statement-v1.md (`statementVersion` 1, `type` `gozleme.revision-attestation`, `registryOrigin`, `publisher` `gz://<origin>/pub/<uuid>`, `capability` `gz://<origin>/cap/<uuid>`, `version`, `sequence`, `native` {`format`, `formatVersion`, `encoding`, `digest`, `byteLength`}, `adapter` {`id`, `version`} with the GOZ-0006 adapter ID split at the slash (`mcp/1` → `mcp`, `1`; A1), `projection` {`version`, `digest`}, `publishedAt` RFC 3339 UTC with exactly six fractional digits, `key` {`id`, `algorithm` `Ed25519`}) and serialize it with `CanonicalJson`; parse a statement back for the verifier and the detail view, reporting unknown or missing members at any level. Uses `CanonicalId` for the IDs. No alias, handle, display name, organization name or account member exists (FR-008, FR-012)
- [X] T021 Create `src/Gozleme.Api/Revisions/RevisionVerifier.cs`, pure with no I/O: inputs are the stored revision row values, the attestation (statement bytes, signature, key row) and the publisher's key set; first failure wins in research R14 order — `malformed_statement`, `unknown_statement_version`, `unknown_field`, `unknown_algorithm`, `unknown_key`, `signature_invalid`, `statement_mismatch`, `native_digest_mismatch`, `projection_digest_mismatch` — else `verified`. The result always carries the key status (`active`, or `retired` with time and reason). Never stored (FR-010)
- [X] T022 Create `src/Gozleme.Api/Revisions/Signing/SigningKeyVault.cs`, the single place private material exists: AES-256-GCM (BCL `AesGcm`) wrap of the 32-byte seed as "`0x01 ‖ nonce(12) ‖ ciphertext(32) ‖ tag(16)`" with associated data "`"gozleme.signing-key.v1" ‖ publisher UUID bytes ‖ UTF-8 key ID`"; `TryCreateKey(publisherId)` and `TrySign(keyRow, statementBytes)` return a fixed `signing_unavailable` failure for an absent, malformed or wrong KEK, or a blob bound to another publisher or key. No key material in `ToString`, exceptions or logs. Inject only into the publisher path and the operator command (plan "Key boundary")
- [X] T023 [P] Add `CatalogueVersion` and `BundleVersion` constants ("rule catalogue v1", "schema bundle v1") to `src/Gozleme.Api/Manifests/ManifestRules.cs` and `src/Gozleme.Api/Manifests/SchemaRegistry.cs`, and expose the adapter ID and the accepted result's canonical projection input from `ManifestValidator` without changing its behaviour (FR-001)

### Permissions, telemetry, audit, limiter

- [X] T024 Apply contracts/permissions-delta.md to `src/Gozleme.Api/Organizations/OrganizationPermissions.cs`: new resource `manifest`; `manifest.publish` (`deny deny deny allow deny` for admin/approver/member/publisher/reviewer), `manifest.list` and `manifest.read` (all allow); move `manifest.validate` from `capability` to `manifest` with its role row unchanged; add the endpoint → action rows. Apply the same delta to `docs/architecture/contracts/organization-permissions.md` (FR-016, Q4)
- [X] T025 [P] Create `src/Gozleme.Api/Revisions/RevisionTelemetry.cs` (research R16, FR-021): span `gozleme.manifest.publish` with `gozleme.manifest.family`, `gozleme.manifest.outcome` (`published`, `rejected`, `version_exists`, `forbidden`, `not_found`, `suspended`, `rate_limited`, `too_large`, `unsupported_media_type`, `unavailable`) and opaque organization/publisher/capability/revision IDs only after membership; span `gozleme.manifest.verify` with outcome and reason; counters `gozleme.manifest.publications` {family, outcome} and `gozleme.manifest.verifications` {outcome, reason}. Never record key material, signatures, statements, digests, versions, documents, names or aliases. Register the source and meter with the existing OpenTelemetry setup in `src/Gozleme.Api/Telemetry/`
- [X] T026 [P] Add audit events to `src/Gozleme.Api/Identity/SecurityAudit.cs`: 1501 `gozleme.audit.revision_published` (revision, capability, publisher, organization, account), 1502 `gozleme.audit.signing_key_created` (key, publisher, organization, account), 1503 `gozleme.audit.signing_key_succeeded` (retired key, successor key, publisher, reason), 1504 `gozleme.audit.signing_key_succession_refused` (category; operator only) — opaque IDs only (FR-022)
- [X] T027 [P] Create `src/Gozleme.Api/Revisions/RevisionPublicationLimiter.cs`: same SQL and shape as `ManifestValidationLimiter` over `manifest_publication_attempts`, keyed by account, fixed one-minute window, `Gozleme:Manifests:PublishPerMinute`, returning `Retry-After` seconds; shared across instances and kept across restarts (FR-019, R15)

### Publication, reads and endpoints

- [X] T028 Create `src/Gozleme.Api/Revisions/RevisionContracts.cs` per contracts/revision-api.openapi.yaml: `RevisionSummary`, `RevisionList {publisherSuspended, revisions, next}`, `RevisionDetail`, `Verification {status, reason, key}`, `KeyStatus`, `SigningKeyList`, and `{error}` bodies `forbidden`, `suspended`, `version_exists`, `rate_limited`, `unsupported_media_type`, `too_large`, `unavailable` (A4). The detail's `attestation.proof` is the fixed text of research R17
- [X] T029 Create `src/Gozleme.Api/Revisions/RevisionStore.cs` under `TenantTransaction`: insert revision, attestation and generation-1 key (`ON CONFLICT (publisher_id, generation) DO NOTHING` then read back; R7); list by `sequence` ascending with `after`/`limit` paging; read one revision with its attestation, key and retirement; read the publisher's key list (never selecting `encrypted_private_key` except in the publisher path); suspended state from the held-claim check in `PublisherStore`
- [X] T030 Create `src/Gozleme.Api/Revisions/RevisionPublisher.cs`, one transaction (R7, R8, R12 steps 10–13): read `now()` at transaction start; take `pg_advisory_xact_lock_shared(hashtextextended('gozleme.signing:' || publisher_id, 0))` and `pg_advisory_xact_lock(hashtextextended('gozleme.revisions:' || capability_id, 0))`; check `version_key` (`409 version_exists`, content never compared, identical replays included); assign `sequence = max + 1`; get or create the generation-1 key through the vault (`503 unavailable` on `signing_unavailable`); canonical projection bytes and digest; build and sign the statement; insert revision, key if new, attestation; commit; then write audit 1501 and, only when this transaction inserted the key, 1502. Map a unique violation to `409 version_exists` and an RLS refusal (`42501`) to `409 suspended`
- [X] T031 Create `src/Gozleme.Api/Revisions/RevisionEndpoints.cs` and `RevisionServiceCollectionExtensions.cs`, and register them in `src/Gozleme.Api/GozlemeApp.cs` on the authorized organizations route group: the 9 routes of research R13 with their actions; publish in the order origin verified (`503`) → membership (identical empty `404`) → `manifest.publish` (`403`) → publisher and capability lookup (`404`) → suspension (`409 suspended`) → budget (`429` with `Retry-After`) → media type (`415`) → body "≤ 1 MiB" (`413`) → GOZ-0006 validation unchanged (rejection answered exactly as validate: status and body) → `RevisionPublisher` → `201` with the detail and `Location`. Reads follow the same order through lookup, then verify each row and the detail. The manifest is never parsed before the budget check. Keep the same-origin and `x-gozleme-client` filters on the unsafe route (FR-017, FR-020)

**Checkpoint**: Foundation ready — publish, list and read work through the API with every database guard in place

---

## Phase 3: User Story 1 — Publish two revisions of a capability (Priority: P1) 🎯 MVP

**Goal**: A `publisher` member validates and publishes two versions in the browser; both appear in order and verify

**Independent Test**: As carol, publish two valid manifests with different run-unique versions to the seeded capability `manifest-demo`; both are listed in sequence order with **Verified**, and the API returns the same records

### Tests for User Story 1

- [X] T032 [P] [US1] `tests/Gozleme.Api.Tests/Revisions/CanonicalJsonTests.cs`: the number-free RFC 8785 Appendix B vectors, UTF-16 key order with non-BMP member names (differs from code-point order), every escape, refusal of floats, out-of-range integers, duplicates and lone surrogates
- [X] T033 [P] [US1] `tests/Gozleme.Api.Tests/Revisions/AttestationStatementTests.cs`: the exact member set, field values from a revision, `publishedAt` with six fractional digits, adapter split (A1), byte-identical canonical output for identical input, and Ed25519 signature determinism
- [X] T034 [P] [US1] `tests/Gozleme.Api.Tests/Revisions/VersionKeyTests.cs` and `SigningKeyVaultTests.cs`: `1.0.0+a` and `1.0.0+b` share a key, `1.0.0-rc.1` and `1.0.0` do not; AES-GCM round trip, a blob moved to another publisher or key ID fails, absent/malformed/wrong KEK → `signing_unavailable`, no key material in `ToString` or exception messages
- [X] T035 [US1] `tests/Gozleme.Api.IntegrationTests/RevisionPublishTests.cs`: two revisions in order (sequence 1, 2; first unchanged), `201` body and `Location`, every FR-002 column stored (native bytes byte-for-byte, digests, byte length, adapter and projection versions, catalogue and bundle versions, `published_by`, `published_at` equal to the statement's `publishedAt`), the first publication creates exactly one generation-1 key readable through the key list, and a lower version after a higher one (`1.2.5` after `2.0.0`) is accepted
- [X] T036 [US1] In `RevisionPublishTests.cs`: a GOZ-0006 rejection (malformed, too large, unprocessable) answers byte-identically to the validate endpoint for the same body and stores nothing (FR-006, US1-AS4); concurrent first publications for one publisher (same and different capabilities) create exactly one key

### Implementation for User Story 1

- [X] T037 [P] [US1] Create `web/src/lib/revisions.ts`: types mirroring `RevisionContracts`, the fixed proof wording of research R17, verification reason → message map, and messages for `version_exists`, `forbidden`, `suspended`, `rate_limited`, `unavailable` and not found
- [X] T038 [P] [US1] Create the BFF route `web/src/app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/route.ts`: `POST` (guarded by `isSameOrigin`, forwarding the same body as validate with the `x-gozleme-client` header) and `GET` (list, forwarding `after` and `limit`), passing status and body through as the validate route does
- [X] T039 [P] [US1] Create `web/src/components/PublishRevision.tsx` (+ `PublishRevision.test.tsx`): shown only after an accepted validation and only with `manifest.publish`; posts the validated body; on `201` navigates to the new revision; renders each error message
- [X] T040 [US1] Wire **Publish revision** into `web/src/components/ManifestValidator.tsx` and the page `web/src/app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/manifest/page.tsx` (pass the member's `manifest.publish` permission)
- [X] T041 [P] [US1] Create `web/src/components/VerificationBadge.tsx` and `RevisionList.tsx` (+ tests): sequence, version, publication time, publishing account (opaque ID), shortened copyable native digest and the badge; paging with `next`; no "latest" label
- [X] T042 [US1] Create the page `web/src/app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/page.tsx` (signed-in session required) and add a **Revisions** link to `web/src/components/CapabilityList.tsx`
- [X] T043 [US1] Create `web/src/components/RevisionDetail.tsx` and `AttestationPanel.tsx` (+ tests) and the page `…/capabilities/[capabilityId]/revisions/[revisionId]/page.tsx`: tabs Native, Projection and Attestation; the attestation shows the statement fields, key ID, algorithm, statement version, verification result and the fixed proof wording

**Checkpoint**: User Story 1 is demonstrable in the browser and the API

---

## Phase 4: User Story 2 — A published signed revision can never be changed (Priority: P1)

**Goal**: Every mutation path is refused, and out-of-band tampering is reported as failed, never verified

**Independent Test**: After two publications, republish version 1 (different, identical and `+build` bytes), send `PUT`/`PATCH`/`DELETE`, run `UPDATE`/`DELETE`/`TRUNCATE` as both logins; all refused, both still verify; a superuser byte change gives `failed`

### Tests for User Story 2

- [X] T044 [P] [US2] In `tests/Gozleme.Api.IntegrationTests/RevisionPublishTests.cs`: republishing an existing version with different bytes, identical bytes and as `1.0.0+build.7` → `409 version_exists` with nothing stored; concurrent publications of one version → exactly one row and one `409` (US2-AS1, AS5)
- [X] T045 [P] [US2] `tests/Gozleme.Api.IntegrationTests/RevisionImmutabilityTests.cs`: `UPDATE`, `DELETE` and `TRUNCATE` on all four tables refused as `gozleme_app` (with an account context) and as `gozleme_migrator`; `PUT`, `PATCH` and `DELETE` on the revision route → `405`; no route updates or deletes attestations or keys; both revisions still verify afterwards (FR-004, SC-002)
- [X] T046 [P] [US2] In `RevisionImmutabilityTests.cs`: as the superuser with `SET session_replication_role = replica` and RLS bypassed, alter one byte of a dedicated revision's native document, projection, statement and signature (each on its own revision) and assert `failed` with, respectively, `native_digest_mismatch`, `projection_digest_mismatch`, `signature_invalid` for a statement byte that keeps the bytes canonical (`malformed_statement` when it does not), and `signature_invalid` (first failure wins, contracts/attestation-statement-v1.md order) (SC-003)
- [X] T047 [P] [US2] `tests/Gozleme.Api.IntegrationTests/RevisionAtomicityTests.cs`: a direct SQL insert of a revision without an attestation cannot commit (deferred trigger); a sequence other than `max + 1` and a wrong `version_key` are refused; a failure after the key insert leaves no key and no revision (FR-005)

### Implementation for User Story 2

- [X] T048 [US2] Map explicit `PUT`, `PATCH` and `DELETE` on the revision route in `src/Gozleme.Api/Revisions/RevisionEndpoints.cs` to `405` (or confirm the router answers `405` and pin it), and render the failed badge with its reason and a "never verified" message in `web/src/components/VerificationBadge.tsx` and `AttestationPanel.tsx`
- [X] T049 [US2] Handle `version_exists` in `web/src/components/PublishRevision.tsx` with a message that points to the **Revisions** list (clients recover by listing)

**Checkpoint**: User Stories 1 and 2 work independently

---

## Phase 5: User Story 3 — Verify attestations and see historical provenance (Priority: P1)

**Goal**: Members download everything needed to verify independently; provenance survives renames, handle reclaims and operator key succession

**Independent Test**: Verify a download with Node/OpenSSL; rename the capability and reclaim the handle — earlier revisions keep canonical IDs and verify; run `signing-key-succession --reason lost` — old revisions verify with the retired key shown, new ones use generation 2

### Tests for User Story 3

- [X] T050 [P] [US3] `tests/Gozleme.Api.Tests/Revisions/RevisionVerifierTests.cs`: one case per reason in contract order, including a retired key that still verifies with its status and reason reported
- [X] T051 [P] [US3] `tests/Gozleme.Api.IntegrationTests/RevisionDownloadTests.cs` (or within `RevisionPublishTests.cs`): each download's media type, `Content-Disposition: attachment`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`; native bytes equal the submitted bytes; SHA-256 of native and projection downloads equals the statement digests; the PEM verifies the statement with .NET's OpenSSL-backed check or a shelled `openssl pkeyutl -verify -rawin` when available (FR-011)
- [X] T052 [P] [US3] `tests/Gozleme.Api.IntegrationTests/RevisionProvenanceTests.cs`: after a capability rename and a `handle-reclaim`, earlier revisions still name the same canonical IDs, contain no alias, handle or display name, still verify, and carry `publisherSuspended: true`; the stored projection is returned, never re-derived (US3-AS3–AS5, FR-014, FR-015)
- [X] T053 [P] [US3] `tests/Gozleme.Api.IntegrationTests/SigningKeySuccessionTests.cs`: `--publisher … --reason lost` and `compromised`; `--undecryptable --reason lost` after a KEK change (publication `503` before, succeeds after); a retired key still verifies earlier revisions and new revisions use the successor; a publication racing a succession either commits with the old key before the retirement or signs with the new key, never failing and never leaving two active keys; works for a suspended publisher while publication stays refused; exit codes 0/1/2/64; output and audit hold opaque IDs only; `gozleme_app` cannot insert a generation > 1 key or a retirement (SC-008)

### Implementation for User Story 3

- [X] T054 [US3] Add the four download routes and the public-key route to `src/Gozleme.Api/Revisions/RevisionEndpoints.cs` with the media types and headers of contracts/attestation-statement-v1.md (native by encoding: `application/json`, `application/yaml` or `text/markdown`, `charset=utf-8`; projection and statement `application/json`; signature `application/octet-stream` 64 raw bytes; public key `application/x-pem-file` SPKI PEM)
- [X] T055 [US3] Create `src/Gozleme.Api/Revisions/SigningKeyOperatorCommands.cs` and dispatch `signing-key-succession` in `src/Gozleme.Api/Program.cs` beside the handle commands: connect as `ConnectionStrings:GozlemeMigrator` with no account context; per publisher take `pg_advisory_xact_lock(hashtextextended('gozleme.signing:' || publisher_id, 0))`, read generation n, create n + 1 under the current KEK, insert the retirement (reason, successor), commit, write audit 1503 (1504 on refusal); `--undecryptable` iterates publishers whose active key fails to decrypt; print `<publisherId> retired <keyUuid> successor <keyUuid>`; exits 0/1/2/64; span `gozleme.operator.signing_key_succession` via `PublisherTelemetry.StartOperator` (R9, FR-007a)
- [X] T056 [P] [US3] Create the BFF routes `web/src/app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/[revisionId]/[part]/route.ts` (only `native`, `projection`, `statement`, `signature`; anything else 404) and `…/publishers/[publisherId]/signing-keys/[keyId]/public-key/route.ts`, streaming bytes and headers unchanged, plus a `GET` for the revision detail and the key list
- [X] T057 [US3] Add downloads to `web/src/components/AttestationPanel.tsx`, the key status with retired time and reason and a prominent `compromised` banner, and the suspended-publisher banner to `RevisionList.tsx` and `RevisionDetail.tsx` (+ tests)
- [X] T058 [US3] Create `web/src/components/SigningKeyList.tsx` (+ test) and the page `web/src/app/organizations/[organizationId]/publishers/[publisherId]/signing-keys/page.tsx`: generations with key ID, algorithm, created time, active/retired, reason, successor; "No key yet" when empty; succession described as a local operator action only

**Checkpoint**: User Stories 1–3 work independently

---

## Phase 6: User Story 4 — No delegation or transfer chain is advertised (Priority: P1)

**Goal**: No surface claims delegation, transfer, chains, transparency or a publisher-held key; the verifier refuses anything unknown

**Independent Test**: Boundary routes answer 404; the verifier refuses an added `delegation` member, an unknown version, an unknown algorithm and a foreign key ID; a wording scan of the contract, API and UI finds nothing

### Tests for User Story 4

- [X] T059 [P] [US4] In `tests/Gozleme.Api.Tests/Revisions/RevisionVerifierTests.cs`: statements with an added `delegation` member (top level and nested), `statementVersion` 2, `key.algorithm` `ES256`, and a key ID of another publisher's key → `failed` with `unknown_field`, `unknown_statement_version`, `unknown_algorithm`, `unknown_key` (US4-AS2; A6 — no public verify endpoint)
- [X] T060 [P] [US4] `tests/Gozleme.Api.IntegrationTests/RevisionBoundaryTests.cs`: `…/signing-keys/rotate`, `/replace`, `/transfer`, `/delegate` and `POST /api/v1/publish` → `404` for every method; API response bodies contain none of "delegat", "transfer", "chain of trust", "transparency", "publisher-held" (case-insensitive); a manifest carrying a `signature` claim is refused `reserved_claim` `422` exactly as validate (US4-AS3–AS5, FR-013)
- [X] T061 [P] [US4] Web wording guard in `web/src/components/AttestationPanel.test.tsx` (and the list, detail and key list tests): rendered output contains none of the banned phrases and contains the fixed proof wording

### Implementation for User Story 4

- [X] T062 [US4] Add a contract wording check (script under `scripts/` or a unit test reading `docs/specs/007-immutable-revisions/contracts/`) that the statement contract defines no delegation, transfer, chain, witness or transparency member except in its "Unsupported in v1" statement (US4-AS1)

**Checkpoint**: User Stories 1–4 work independently

---

## Phase 7: User Story 5 — Only publisher members publish; members read; others see nothing (Priority: P1)

**Goal**: The role matrix, identical empty 404s, suspension refusal and the rate limit hold in the API and the database

**Independent Test**: For each built-in role, publish/list/read against the contract table; non-member and cross-organization IDs give the identical empty 404; the isolation matrix shows no cross-tenant row for either login

### Tests for User Story 5

- [X] T063 [P] [US5] `tests/Gozleme.Api.IntegrationTests/RevisionAuthorizationTests.cs`: role × {publish, list, read, four downloads, key list, public key} against the contract table (admin without `publisher` → `403`, manifest not parsed); non-member, unknown, malformed, foreign and misplaced organization/publisher/capability/revision/key IDs → identical empty `404`; suspended → `409 suspended` in the API and a direct SQL insert refused by the write policy; registry origin not verified → `503`; role loss takes effect on the next request; the 11th publication in a minute → `429` with `Retry-After` and the `rate_limited` outcome (SC-006, US5)
- [X] T064 [P] [US5] `tests/Gozleme.Api.IntegrationTests/RevisionIsolationTests.cs` and the shared matrix in `RlsIsolationTests.cs`: for the four tables, a member of organization A under their own context selects no row of B and can insert none, as `gozleme_app` and as `gozleme_migrator` with an account context; `is_operator()` is false for `gozleme_app` and for the owner with an account context (FR-018)
- [X] T065 [P] [US5] Update `tests/Gozleme.Api.IntegrationTests/EndpointAuthorizationTests.cs` and `OrganizationPermissionContractTests.cs` deliberately: 22 actions, 27 scoped endpoints, `manifest.validate` on resource `manifest`, and the new endpoint → action rows; update `tests/Gozleme.Api.Tests/OrganizationPermissionsTests.cs`

### Implementation for User Story 5

- [X] T066 [US5] Hide **Publish revision** without `manifest.publish` in `web/src/components/PublishRevision.tsx` and `ManifestValidator.tsx`, and render `forbidden`, `suspended` and `rate_limited` (+ tests for alice and a suspended publisher)

**Checkpoint**: All user stories are independently functional

---

## Phase 8: Polish & Cross-Cutting Concerns (pins, telemetry, performance, demo, evidence)

**Purpose**: Deliberate pin updates, telemetry and performance proofs, the cumulative demo and its evidence

- [X] T067 [P] Update pins: migration count to 8 in `tests/Gozleme.Api.IntegrationTests/MigrationAndRoleTests.cs` (and any other migration pin); `BootstrapBoundaryTests.cs` `(manifest|revision|projection)` now expects exactly `manifest_publication_attempts, manifest_revisions, manifest_validation_attempts, revision_attestations`; `RlsCatalogTests.cs` for the new forced tables; `ManifestNoStorageTests.cs` still proves validate writes no revision row (R19, FR-026)
- [X] T068 [P] `tests/Gozleme.Api.IntegrationTests/RevisionTelemetryTests.cs`: publish and verify spans and counters with bounded labels; the operator span; no KEK, private or encrypted key bytes, statement, signature (base64 and hex), digest, version, document, name or alias in spans, events, metric labels or captured logs (FR-021, SC-005)
- [X] T069 [P] `tests/Gozleme.Api.IntegrationTests/RevisionPerformanceTests.cs`: publishing a manifest with a 512 KiB native document completes in under 2 s and verifying a revision in under 1 s (SC-007)
- [X] T070 Create `e2e/tests/db.ts` (run `psql` in the Compose `postgres` service as `gozleme_app` with an account context, as `gozleme_migrator`, and as the superuser for tamper) and `e2e/tests/operator.ts` (`docker compose run --rm migrate handle-reclaim …` and `signing-key-succession …`), reusing `e2e/tests/compose.ts`
- [X] T071 Create `e2e/tests/revision-demo.spec.ts` covering quickstart.md steps 1–8: two publications by carol in the browser with screenshots of the list, the three detail tabs and the key view; API and `psql` refusals as both logins; superuser tamper on dedicated run-unique revisions with a **Failed** screenshot; Node `crypto.verify(null, …)` plus digest and re-canonicalization checks for one revision of every family; rename and reclaim on `revdemo-<runid>` with the **Suspended publisher** screenshot and `409 suspended`; `lost` and `compromised` succession with screenshots; boundary 404s and the DOM/response wording scan; roles (alice `403`, dave identical `404`, 11 publications → `429`); Jaeger spans `gozleme.manifest.publish`, `gozleme.manifest.verify` and `gozleme.operator.signing_key_succession` with trace screenshots; and the no-secret scan of Jaeger, Loki and API logs for the KEK, the run's statement and signature (base64 and hex), native digest, version string and capability name (FR-025)
- [X] T072 Add project `goz-0007` (`testMatch: /revision-demo\.spec\.ts/`, `dependencies: ["goz-0006"]`) to `e2e/playwright.config.ts`, add `"goz-0007": "007-immutable-revisions"` to `specDirs` in `e2e/tests/evidence.ts`, and write `docs/specs/007-immutable-revisions/evidence/demo-run.json` and `revision-evidence.json` (tamper and succession run IDs) with sanitized screenshots under `evidence/screenshots/` and regression screenshots under `evidence/screenshots/regression-goz-NNNN/`
- [ ] T073 Run `make test`, `make lint`, `make build`, `make validate` and `make demo` (all suites `goz-0000` … `goz-0007`); record results in `docs/specs/007-immutable-revisions/progress.yaml` `evidence` and update the GOZ-0007 registry entry gates in `.gozleme/spec-registry.yaml` (SC-009)
- [X] T074 Record the security-review items for convergence in `progress.yaml` (plan "Post-design re-check"): the `is_operator()` branch on forced-RLS tables, KEK handling and associated-data binding, no FK on `published_by`/`created_by` (R11), the deferred attestation trigger, the canonical JSON writer, the no-secret scan coverage and the proof wording — to be reviewed with `review-security` during convergence

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: T001 → T002 (stop rule gate); T003–T006 in parallel after T001.
- **Foundational (Phase 2)**: depends on T002 passing. T007–T014 are one migration file, in order; T015–T016 after T014. T017–T019 and T023 in parallel; T020 after T017 and T019; T021 after T020; T022 after T019 and T004. T024–T027 in parallel. T028 → T029 → T030 (needs T020–T022, T027) → T031 (needs T024, T025).
- **User stories (Phases 3–7)**: all depend on Phase 2. They can proceed in parallel; in spec order US1 → US5 for a single implementer. US2–US5 web tasks extend US1's components (T039–T043), so do US1's web tasks first when one person works on the UI.
- **Polish (Phase 8)**: T067–T069 after Phase 2; T070–T072 after all stories; T073 last before convergence.

### User Story Dependencies

- **US1**: none beyond Foundational.
- **US2**: none beyond Foundational for its API and database tests; T048–T049 extend US1's components.
- **US3**: T054 and T055 are independent of US1; T056–T058 extend US1's detail page.
- **US4**: none beyond Foundational for T059–T060; T061 needs US1/US3 components.
- **US5**: none beyond Foundational for T063–T065; T066 extends T039.

### Within Each User Story

- Tests are written first and fail before the implementation they cover.
- API before BFF route before component before page.

## Parallel Opportunities

- Setup: T003, T004, T005, T006 together after T001.
- Foundational: T017, T018, T019, T023 together; T024, T025, T026, T027 together.
- US1: T032, T033, T034 together; T037, T038, T039, T041 together.
- US2: T044–T047 together.
- US3: T050–T053 together; T056 alongside T054–T055.
- US4: T059–T061 together.
- US5: T063–T065 together.
- Polish: T067, T068, T069 together.

## Parallel Example: User Story 1

```bash
Task: "CanonicalJsonTests in tests/Gozleme.Api.Tests/Revisions/CanonicalJsonTests.cs"
Task: "AttestationStatementTests in tests/Gozleme.Api.Tests/Revisions/AttestationStatementTests.cs"
Task: "VersionKeyTests and SigningKeyVaultTests in tests/Gozleme.Api.Tests/Revisions/"
Task: "revisions types and wording in web/src/lib/revisions.ts"
Task: "BFF route in web/src/app/api/.../capabilities/[capabilityId]/revisions/route.ts"
Task: "PublishRevision component in web/src/components/PublishRevision.tsx"
Task: "RevisionList and VerificationBadge in web/src/components/"
```

## Implementation Strategy

1. **Vendoring first**: T001–T006, honouring the stop rule. A libsodium load or NSec import/export failure stops the run for maintainer review.
2. **MVP**: Foundational + US1 — two signed revisions published in the browser, listed in order and verified.
3. Add US2 (immutability proofs and tamper) and US3 (downloads, independent verification, provenance, key succession).
4. Add US4 (boundary and wording proofs) and US5 (role matrix, isolation, suspension, rate limit).
5. Polish: pins, telemetry and performance suites, the `goz-0007` demo and evidence. Then converge (with `review-security` on the items in T074), run the post-convergence impact analysis and the roadmap sync per `drive-speckit`.

**Total**: 74 tasks (Setup 6, Foundational 25, US1 12, US2 6, US3 9, US4 4, US5 4, Polish 8).
