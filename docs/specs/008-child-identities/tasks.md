---

description: "Task list for GOZ-0008 Child capability identities and projections"
---

# Tasks: Child Capability Identities and Projections

**Input**: Design documents from `docs/specs/008-child-identities/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R14), data-model.md, contracts/ (child-identity.md, projection-generations.md, publish-delta.md, children-api.openapi.yaml, permissions-delta.md), quickstart.md

**Tests**: Required. The spec demands proof that every revision has as many child records as entries (SC-002), deterministic matching (SC-003), that no child changes or disappears (SC-004), that no cross-tenant, signed-out or malformed request leaks existence (SC-005), a 1,000-entry performance bound (SC-006), a no-name scan of telemetry and logs (SC-007), immutability for every login (FR-017), regression of all earlier suites (FR-022) and a cumulative browser demo (Constitution IV/VI).

**Organization**: Tasks are grouped by user story. US1–US4 are P1 and US5 is P2; they run in spec order. Storage (the migration with every guard), the pure matcher, the duplicate rule, the publication changes and the `migrate` install step are foundational: every story publishes through them, and the demo stack already holds GOZ-0007 revisions, so the install step must run before any new publication to `manifest-demo` can succeed (A7). Each story then adds its proofs and the product surface it needs. Projection generation endpoints and the re-projection command belong to US3; the install-step proofs belong to US5.

**Maintainer rulings (2026-11-19, before tasks)**: A2 (operator read branch on `manifest_revisions_select`), A4 (`projection-regenerate` does not re-run validation rules), A7 (`503 unavailable` for the rollout gap) and A8 (child views and child revision records use generation 1 titles) were approved as planned. A1, A3, A5 and A6 stay disclosed for veto. Any implementation detail that would change accepted or refused behaviour beyond these documents is a question for the maintainer, never an agent choice.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)

## Path Conventions

API `src/Gozleme.Api/` (new module `src/Gozleme.Api/Children/`), API unit tests `tests/Gozleme.Api.Tests/Children/`, integration tests `tests/Gozleme.Api.IntegrationTests/` (Testcontainers, PostgreSQL 17), web `web/src/`, demo `e2e/`. No Compose, Helm, environment or package change.

---

## Phase 1: Setup (Configuration and module skeleton)

**Purpose**: Bind the one new setting and register the empty module so later tasks only add code

- [X] T001 Add `ChildPageSize` to the `Gozleme:Manifests` options (where `RevisionPageSize` binds, `src/Gozleme.Api/Revisions/RevisionServiceCollectionExtensions.cs` or `RevisionOptions`): default "50", "maximum 100", validated on start; add the default to `src/Gozleme.Api/appsettings.json` (data-model "Configuration")
- [X] T002 [P] Create `src/Gozleme.Api/Children/ChildServiceCollectionExtensions.cs` (`AddChildren()`, `MapChildEndpoints()` stubs) and call both from `src/Gozleme.Api/GozlemeApp.cs` beside the revision module

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Storage with database-enforced assignment, immutability and completeness; the pure matcher and duplicate rule; publication that records children and generation 1 in its transaction; and the install step for existing revisions

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Storage (migration `20261122000000_ChildIdentities`, one file, in order)

- [X] T003 Create `src/Gozleme.Api/Persistence/Migrations/20261122000000_ChildIdentities.cs` with table `capability_children`: `id uuid PK` (UUIDv7), `organization_id uuid`, `capability_id uuid`, `kind text CHECK (kind IN ('tool','resource','resource-template','prompt','skill','operation','webhook','channel'))`, `native_name text CHECK (length BETWEEN 1 AND 1024)`, `is_duplicate boolean NOT NULL DEFAULT false`, `ordinal integer CHECK (ordinal >= 1)` with `UNIQUE (capability_id, ordinal)`, `first_revision_id uuid`, `created_at timestamptz`; FK (`capability_id`, `organization_id`) → `capabilities` `ON DELETE RESTRICT`; FK (`first_revision_id`, `organization_id`) → `manifest_revisions`; `ak_capability_children_id_organization_id UNIQUE (id, organization_id)`; partial unique index `ix_capability_children_match UNIQUE (capability_id, kind, native_name) WHERE NOT is_duplicate` (data-model, research R4)
- [X] T004 In the same migration, table `revision_children`: `revision_id uuid`, `entry_index integer CHECK (entry_index >= 0)`, `organization_id uuid`, `child_id uuid`, `kind text`, `native_name text`, `title text NULL`; PK (`revision_id`, `entry_index`); `UNIQUE (revision_id, child_id)`; index on `child_id`; FK (`revision_id`, `organization_id`) → `manifest_revisions`; FK (`child_id`, `organization_id`) → `capability_children` (data-model)
- [X] T005 In the same migration, table `projection_generations`: `id uuid PK` (UUIDv7), `organization_id uuid`, `revision_id uuid` FK (`revision_id`, `organization_id`) → `manifest_revisions`, `generation integer CHECK (generation >= 1)` with `UNIQUE (revision_id, generation)`, `adapter_id text`, `adapter_version text`, `projection_version text`, `projection bytea NULL`, `projection_digest text NULL`, `created_at timestamptz`; `CHECK ((generation = 1) = (projection IS NULL AND projection_digest IS NULL))`; `CHECK (projection_digest IS NULL OR projection_digest = 'sha256:' || encode(sha256(projection), 'hex'))` (data-model, research R5)
- [X] T006 In the same migration, enable **and force** RLS on the three tables; grant `gozleme_app` `SELECT, INSERT` only; read policies `CASE WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END`; insert policies with the member branch of `manifest_revisions_insert` (`gozleme.account_has_role(organization_id, 'publisher')` and a `held` claim for the capability's publisher), restricted to `is_duplicate = false` on `capability_children` and `generation = 1` on `projection_generations`, or `gozleme.is_operator()` (data-model "Insert policy", FR-018)
- [X] T007 In the same migration, the row guards (`plpgsql`, `SET search_path = pg_catalog, pg_temp`, `EXECUTE` revoked from `PUBLIC`): `capability_children_insert_guard` (sets `created_at = now()`; `ordinal = max + 1` for the capability; `first_revision_id` is a revision of the same capability) and `projection_generations_insert_guard` (`generation = max + 1`; generation 1 has the revision's adapter ID, adapter version and projection version; generation > 1 only for `is_operator()` and with the same `kind`/`nativeName` multiset as the revision's stored projection; sets `created_at`)
- [X] T008 In the same migration, the statement-level `revision_children_entry_check` (`AFTER INSERT … REFERENCING NEW TABLE`): parse each affected revision's stored projection once (`convert_from(projection, 'UTF8')::jsonb -> 'entries'`) and raise `insufficient_privilege` for any row whose `entry_index`, `kind`, `native_name` or `title` differs from the entry, whose child belongs to another capability or kind, or whose non-duplicate child has a different native name (research R4)
- [X] T009 In the same migration, deferred constraint triggers (`DEFERRABLE INITIALLY DEFERRED`): `capability_children_recorded` (at commit the child has a `revision_children` row for `first_revision_id`) and `manifest_revisions_children` on `manifest_revisions` `AFTER INSERT` (at commit the revision has generation 1 and exactly as many `revision_children` rows as projection entries; fires only for revisions inserted after the migration)
- [X] T010 In the same migration, `immutable_row_guard` row triggers for `UPDATE`/`DELETE` and statement triggers for `TRUNCATE` on all three tables, for every login; and replace `manifest_revisions_select` with the same policy plus the first branch `WHEN gozleme.is_operator() THEN true` (the `publisher_signing_keys_select` pattern; maintainer-approved A2, security-review item). No new callable function, no `SECURITY DEFINER`, `DefinerHelpers` unchanged (research R13)
- [X] T011 Add entities `CapabilityChild`, `RevisionChild`, `ProjectionGeneration` (in `src/Gozleme.Api/Children/ChildEntities.cs`) to `src/Gozleme.Api/Persistence/GozlemeDbContext.cs`, write `20261122000000_ChildIdentities.Designer.cs` and update `GozlemeDbContextModelSnapshot.cs`; classify the three tables `TenantOwned` in `src/Gozleme.Api/Persistence/TableClassification.cs` and add them to `src/Gozleme.Api/Persistence/RlsCatalog.cs`
- [X] T012 Update the GOZ-0007 integration tests that insert revisions by SQL (`RevisionAtomicityTests`, `RevisionImmutabilityTests`, `RevisionIsolationTests` and any helper in `tests/Gozleme.Api.IntegrationTests/Infrastructure/`) so they also insert generation 1 and children, or assert the new deferred refusal where that is the point of the test

### Pure core (tests first)

- [X] T013 [P] Write `tests/Gozleme.Api.Tests/Children/ChildAssignerTests.cs`: match by exact (`kind`, `nativeName`); title, description and position ignored; rename → new child; case and Unicode normalization variants are different keys; a kind change is a different child; a duplicate child is never matched; install mode: first duplicate by position takes the key, each further one gets a new `is_duplicate` child; counts `declared`/`created`/`matched`/`absent` (absent = earlier non-duplicate children not declared, plus every duplicate child) and `duplicates`; ordinals continue from the next ordinal; a fixed sequence gives the same (sequence, entry index) → ordinal mapping every time (research R2, SC-003)
- [X] T014 [P] Write `tests/Gozleme.Api.Tests/Children/ChildEntriesTests.cs`: duplicate groups; one `publish.duplicate_entry` problem for every entry in a group, the first included, ordered by index, location `{part: "projection", pointer: "/entries/<i>"}`, category `invalid_value`, fixed message "Two or more projection entries have the same kind and native name.", capped at 50 with `truncated`, no name echoed; includes an `a2a` card with two skills of one `id` and an `api` derived name `"<METHOD> <path>"` equal to another `operationId` (research R3, contracts/publish-delta.md)
- [X] T015 Implement `src/Gozleme.Api/Children/ChildAssigner.cs` (pure; input: the capability's non-duplicate children by key, the next ordinal, the projection entries, mode `publish` | `install`; output: new children, revision records, counts) so T013 passes
- [X] T016 [P] Implement `src/Gozleme.Api/Children/ChildEntries.cs` (`Duplicates(entries)`) and `src/Gozleme.Api/Children/PublishRules.cs` (publish rule catalogue version `1` with the one rule; the GOZ-0006 catalogue and its 40 rules unchanged) so T014 passes; add the `projection` location part to the problem location type without changing GOZ-0006 output

### Identity, telemetry, audit

- [X] T017 [P] Add `CanonicalId.Child(origin, capabilityId, kind, childId)` rendering `gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>` in `src/Gozleme.Api/Publishers/RegistryOrigin.cs` with unit tests beside the existing `CanonicalId` tests; add the "Child IDs (GOZ-0008)" section (form, no alias, no reservation, maintainer Q1 provenance, pointer to `docs/specs/008-child-identities/contracts/child-identity.md`) to `docs/specs/005-publisher-identities/contracts/namespace.md` (research R8)
- [X] T018 [P] Create `src/Gozleme.Api/Children/ChildTelemetry.cs`: spans `gozleme.children.assign` (attributes `gozleme.manifest.family`, `gozleme.children.declared`, `.created`, `.matched`, `.absent`, `.duplicates` in install mode), `gozleme.projection.generation.record` (`gozleme.projection.generation` integer, `gozleme.projection.attested` bool), `gozleme.children.install` (revision and capability counts), `gozleme.operator.projection_regenerate` (`gozleme.operator.exit_code`); counters `gozleme.children.assignments` (`gozleme.manifest.family`, `gozleme.child.kind`, `gozleme.children.outcome` ∈ {`created`, `matched`, `duplicate`}) and `gozleme.projection.generations` (`gozleme.manifest.family`, `gozleme.projection.outcome` ∈ {`recorded`, `identical`, `refused`}); no name, title, native name, digest, document or version ever (research R11, FR-019)
- [X] T019 [P] Add audit events 1601 `projection_generation_recorded`, 1602 `projection_generation_refused` (fixed reason word) and 1603 `children_installed` (capability, revision count) to `src/Gozleme.Api/Identity/SecurityAudit.cs`, opaque IDs only; note the next free range 1701 (FR-020)

### Publication and install

- [X] T020 Create `src/Gozleme.Api/Children/ChildStore.cs` insert path under `TenantTransaction`: one query for the capability's non-duplicate children by key and the next ordinal, then add new `capability_children`, `revision_children` and generation 1 rows to the same `SaveChangesAsync`
- [X] T021 Change `src/Gozleme.Api/Revisions/RevisionPublisher.cs`: after GOZ-0006 validation accepts, run `ChildEntries.Duplicates` and refuse with `422` `publish.duplicate_entry` (GOZ-0006 `Rejected` shape, nothing stored, budget attempt counted, span outcome `rejected`) before the version check; under the capability lock, answer `503 {"error":"unavailable"}` when the capability has a revision without generation 1 (maintainer-approved A7); then assign children (`ChildAssigner`, publish mode) and insert generation 1 in the publication transaction, inside `gozleme.children.assign` and `gozleme.projection.generation.record` spans; the `201` body is unchanged (FR-021)
- [X] T022 Update the publication order in `src/Gozleme.Api/Revisions/RevisionEndpoints.cs` to … → validation → `422 publish.duplicate_entry` → `409 version_exists` → `503 unavailable` (no usable key, or rollout gap) → `201`, and classify an `insufficient_privilege` from the entry check or deferred triggers as a refusal with rollback (contracts/publish-delta.md)
- [X] T023 Create `src/Gozleme.Api/Children/ChildInstaller.cs` and call it as the last step of `src/Gozleme.Api/Persistence/MigrationRunner.cs`, after the registry origin is verified: as the operator, select revisions without generation 1 grouped by capability and ordered by `sequence`; per capability take `gozleme.revisions:<capability>`, assign each revision with `ChildAssigner` in install mode, insert generation 1, commit per capability, emit `gozleme.children.install` and audit 1603 after commit; idempotent; reads revisions and attestations only (research R6, FR-016, FR-016a)
- [X] T024 Update the migration pins: `LATEST_MIGRATION` (`/_ChildIdentities$/`), `tests/Gozleme.Api.IntegrationTests/MigrationAndRoleTests.cs` (9 migrations), `StatusEndpointTests.cs`, and the RLS catalog tests (`RlsCatalogTests.cs`) for three more tenant-owned tables (research R13)

**Checkpoint**: Foundation ready — publication records children and generation 1 with every database guard in place, `make up` installs children for existing revisions, and existing suites pass

---

## Phase 3: User Story 1 — Browse a server's independent child identities (Priority: P1) 🎯 MVP

**Goal**: A member browses a capability's children, each child's page and a revision's children, in the browser and the API

**Independent Test**: As carol, publish one MCP revision with two tools, one resource and one prompt to `manifest-demo`; the revision page lists 4 children with distinct child IDs and the right kinds; the Children view contains them; each child page shows ID, kind, parent capability and history; the API returns the same

### Tests for User Story 1

- [X] T025 [P] [US1] Write the US1 cases of `tests/Gozleme.Api.IntegrationTests/ChildPublishTests.cs` (fresh capabilities for exact counts): one MCP revision → exactly 4 children with distinct IDs and kinds `tool`, `tool`, `resource`, `prompt`; `canonicalId` equals `CanonicalId.Child`; child detail lists every revision with presence, native name and title; revision children ordered by `entry_index`; a `skill` and a `model` capability have no children; capability children paged by `ordinal` (default 50, at most 100, `after` cursor); child revision records equal the entry count (SC-002)
- [X] T026 [P] [US1] Write Vitest suites `web/src/components/ChildList.test.tsx`, `ChildDetail.test.tsx` and `RevisionChildren.test.tsx`: kind, child ID and names/titles by revision; empty state "This capability declares no children."; the fixed notice "Children are declared by the publisher in the manifest. They are not verified behaviour." on every child view; no approval, trust, safety, lifecycle or verification badge (FR-014)

### Implementation for User Story 1

- [X] T027 [US1] Create `src/Gozleme.Api/Children/ChildContracts.cs` (`ChildSummary`, `ChildDetail` with history page, `RevisionChild`, list envelopes with `items`/`next`) matching `docs/specs/008-child-identities/contracts/children-api.openapi.yaml`; child records and views carry generation 1 titles (maintainer-approved A8)
- [X] T028 [US1] Add the read queries to `src/Gozleme.Api/Children/ChildStore.cs`: capability children by `ordinal`, child history by revision `sequence` (present with native name and title, or absent), revision children by `entry_index`; all bounded by `ChildPageSize`
- [X] T029 [US1] Create `src/Gozleme.Api/Children/ChildEndpoints.cs` with `GET …/capabilities/{capabilityId}/children` (`manifest.list`), `GET …/children/{childId}` (`manifest.read`) and `GET …/revisions/{revisionId}/children` (`manifest.read`), reusing the `RevisionEndpoints` resolution for the order origin `503` → membership `404` → permission `403` → target `404` (FR-015); set `gozleme.child.id` only after membership is confirmed
- [X] T030 [P] [US1] Create `web/src/lib/children.ts` (types, notice text, kind labels) and `web/src/lib/children-server.ts` (server fetchers, as in `revisions-server.ts`)
- [X] T031 [P] [US1] Create GET-only BFF routes `web/src/app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/children/route.ts`, `…/children/[childId]/route.ts` and `…/revisions/[revisionId]/children/route.ts`
- [X] T032 [US1] Create `web/src/components/ChildList.tsx`, `ChildDetail.tsx` and `RevisionChildren.tsx` (child IDs rendered with `CanonicalId.tsx`, each linked by child ID) so T026 passes
- [X] T033 [US1] Create pages `web/src/app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/children/page.tsx` and `…/children/[childId]/page.tsx` with `not-found.tsx` for both (session required, existing "not found" page); add the **Children** section to `…/revisions/[revisionId]/page.tsx`
- [X] T034 [US1] Add **Children** links to `web/src/components/CapabilityList.tsx` and `web/src/components/RevisionList.tsx` so a member reaches any child's history in two moves (SC-001)

**Checkpoint**: User Story 1 is demonstrable in the browser and the API

---

## Phase 4: User Story 2 — Child identities persist across revisions (Priority: P1)

**Goal**: Kept and retitled entries keep their child IDs, new entries get new ones, dropped and renamed children stay and are shown absent, and duplicate keys are refused at publication

**Independent Test**: After US1, publish a second revision that keeps one tool, retitles another, drops the prompt and adds a tool; IDs stay stable for the kept and retitled tools, the new tool has a new ID, the prompt is absent from the new revision, and revision 1 still lists exactly its 4 children

### Tests for User Story 2

- [X] T035 [P] [US2] Add the US2 cases to `tests/Gozleme.Api.IntegrationTests/ChildPublishTests.cs`: keep/retitle/drop/add; reordered entries keep IDs; rename → new child and old child absent; a lower version published later matches by sequence and earlier revisions' children never change; the same key in two capabilities → two different children; an `a2a` card with duplicate skill `id`s → validate `200`, publish `422` `publish.duplicate_entry` with two `projection` locations and nothing stored (no revision, child or generation row; budget counted); the `201` body is byte-shape unchanged (FR-005, FR-005a, FR-006, FR-008, FR-021)
- [X] T036 [P] [US2] Add a determinism test to `ChildPublishTests.cs`: publish the same manifest sequence into two fresh capabilities and compare the (sequence, entry index) → ordinal mapping (SC-003)
- [X] T037 [P] [US2] Extend `web/src/components/ChildDetail.test.tsx` for the **Present**/**Absent** history and extend `web/src/components/ProblemList.test.tsx` for a `projection` location part without an echoed name

### Implementation for User Story 2

- [X] T038 [US2] Render the Present/Absent history (sequence, version, native name and title where present) in `web/src/components/ChildDetail.tsx`
- [X] T039 [US2] Render the `projection` location part in `web/src/components/ProblemList.tsx` and surface the `422` problem list from `web/src/components/PublishRevision.tsx`

**Checkpoint**: User Stories 1 and 2 work independently

---

## Phase 5: User Story 3 — Child IDs are not projection generation IDs (Priority: P1)

**Goal**: Each revision lists its projection generations; the operator command adds unattested generation N+1 that keeps every child; generation and child IDs are never interchangeable

**Independent Test**: Run `docker compose run --rm migrate projection-regenerate --revision <id>`; generation 2 exists, every child keeps its ID, the revision still verifies, generation 1 is **Attested at publication** and generation 2 **Not attested**, and swapped IDs answer the empty `404`

### Tests for User Story 3

- [X] T040 [P] [US3] Write `tests/Gozleme.Api.Tests/Children/ProjectionRegeneratorTests.cs`: the stored native bytes of each family re-project to byte-identical canonical bytes (RFC 8785, `CanonicalJson`); validation rules are not called (maintainer-approved A4); an entry key multiset change is detected
- [X] T041 [P] [US3] Write `tests/Gozleme.Api.IntegrationTests/ProjectionRegenerateTests.cs`: generation 2 recorded with the same child IDs, stdout `<revision-uuid> generation 2 <generation-uuid> digest equals generation 1: yes`, exit 0; a third run records generation 3 (byte-identical still recorded, clarify Q2); generation 1 bytes, digest and attestation unchanged and the revision verifies; unknown revision exit 2 `unknown_target`; tampered native bytes exit 3 `native_digest_mismatch`; a changed entry multiset exit 3 `entries_changed` with nothing recorded; an unregistered adapter exit 3 `adapter_unavailable`; usage exit 64; without the owner login (as `gozleme_app`) refused; a suspended publisher's revision can be re-projected; audit 1601/1602 and output hold opaque IDs only
- [X] T042 [P] [US3] Add generation cases to `tests/Gozleme.Api.IntegrationTests/ChildPublishTests.cs`: generation 1 bytes and digest equal the stored revision projection; `attested` true only for generation 1; `comparedToPrevious` `identical`/`different`/`not_comparable` and absent for generation 1; a generation ID in a child route, a child ID in the generation route and a revision ID as a child ID each answer the identical empty `404` (US3-AS3, FR-010)
- [X] T043 [P] [US3] Write `web/src/components/ProjectionGenerations.test.tsx`: generation, generation ID, adapter, digest, **Attested at publication** for 1, **Not attested** for later ones, download link

### Implementation for User Story 3

- [X] T044 [US3] Implement `src/Gozleme.Api/Children/ProjectionRegenerator.cs` (pure: native bytes + stored encoding + envelope rebuilt from `envelope_summary` + adapter → canonical projection; no validation rules) so T040 passes
- [X] T045 [US3] Implement `src/Gozleme.Api/Children/ProjectionOperatorCommands.cs` (the `SigningKeyOperatorCommands` pattern) and the `projection-regenerate --revision <uuid>` dispatch in `src/Gozleme.Api/Program.cs`: one transaction under `gozleme.projections:<revision>`; read as the operator; check native digest; select the adapter by stored ID; parse and project; canonicalize; compare the entry key multiset with generation 1; insert generation N+1 (`max + 1`) even when identical; exit codes 0/1/2/3/64 with the fixed stderr words; span, counter and after-commit audit (contracts/projection-generations.md) so T041 passes
- [X] T046 [US3] Add `ProjectionGenerationView` to `src/Gozleme.Api/Children/ChildContracts.cs` and `GET …/revisions/{revisionId}/projections` (unpaged list) and `GET …/revisions/{revisionId}/projections/{generationId}` (exact bytes, `application/json`, `Content-Disposition: attachment`, `nosniff`; generation 1 serves the stored revision projection bytes) to `src/Gozleme.Api/Children/ChildEndpoints.cs`, both on `manifest.read`; set `gozleme.projection.generation_id` only after membership is confirmed
- [X] T047 [P] [US3] Create GET-only BFF routes `web/src/app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/[revisionId]/projections/route.ts` and `…/projections/[generationId]/route.ts` (passes the attachment bytes through)
- [X] T048 [US3] Create `web/src/components/ProjectionGenerations.tsx` and add the **Projection generations** section to `…/revisions/[revisionId]/page.tsx` so T043 passes
- [X] T049 [P] [US3] Add a `projectionRegenerate(revisionId)` helper to `e2e/tests/operator.ts` returning exit code, stdout and stderr

**Checkpoint**: User Stories 1–3 work independently

---

## Phase 6: User Story 4 — Child identities are immutable and tenant-private (Priority: P1)

**Goal**: No login can change or delete child records; other tenants, signed-out visitors and malformed IDs learn nothing; no write route exists

**Independent Test**: `UPDATE`/`DELETE`/`TRUNCATE` on the three tables as `gozleme_app` and `gozleme_migrator` are refused; dave gets the identical empty `404` on all five endpoints; signed-out gets `401`/sign-in; `DELETE` on a child route gives `405`

### Tests for User Story 4

- [X] T050 [P] [US4] Write `tests/Gozleme.Api.IntegrationTests/ChildImmutabilityTests.cs`: `UPDATE`, `DELETE` and `TRUNCATE` on `capability_children`, `revision_children` and `projection_generations` refused as `gozleme_app` and as `gozleme_migrator`; `POST`/`PUT`/`PATCH`/`DELETE` on the three child routes and the two projection routes answer `405` (FR-003, FR-004, FR-017)
- [X] T051 [P] [US4] Write `tests/Gozleme.Api.IntegrationTests/ChildIntegrityTests.cs` (direct SQL as `gozleme_app` with a publisher context): a wrong child reuse, a second non-duplicate child for one key, a mismatched entry (`kind`, `native_name`, `title` or `entry_index`), a child of another capability, a member-inserted duplicate child, a member-inserted generation 2, an orphan child and a revision without generation 1 or with a missing child record are all refused at insert or commit (research R4, FR-018)
- [X] T052 [P] [US4] Write `tests/Gozleme.Api.IntegrationTests/ChildAuthorizationTests.cs`: each built-in role × the five endpoints against contracts/permissions-delta.md; non-member, foreign-organization, malformed and swapped IDs → identical empty `404`; origin not verified → `503`; signed-out → `401`; a member removed between requests → `404`; a suspended publisher's children stay readable (US4-AS5)
- [X] T053 [P] [US4] Write `tests/Gozleme.Api.IntegrationTests/ChildIsolationTests.cs` and add the three tables to the cross-tenant isolation matrix for both logins (`RlsIsolationTests` / matrix source in `tests/Gozleme.Api.IntegrationTests/Infrastructure/`)
- [X] T054 [P] [US4] Update `tests/Gozleme.Api.IntegrationTests/OrganizationPermissionContractTests.cs` (22 actions, 6 resources, **32** scoped endpoints) and `EndpointAuthorizationTests.cs` (the five new endpoints)

### Implementation for User Story 4

- [X] T055 [US4] Add the `405` handling for non-GET methods on the child and projection routes in `src/Gozleme.Api/Children/ChildEndpoints.cs`, as the GOZ-0007 revision routes do
- [X] T056 [US4] Apply contracts/permissions-delta.md to `docs/architecture/contracts/organization-permissions.md` (five endpoint rows, the database write row, the boundary text)

**Checkpoint**: User Stories 1–4 work independently

---

## Phase 7: User Story 5 — Revisions published before this feature have children (Priority: P2)

**Goal**: Existing revisions get generation 1 and children by the same rule, idempotently, without changing any revision or attestation

**Independent Test**: Start from a stack with GOZ-0007 revisions, run `migrate`; every existing revision has generation 1 and children by the continuity rule and still verifies; a second `migrate` changes nothing

### Tests for User Story 5

- [X] T057 [P] [US5] Write `tests/Gozleme.Api.IntegrationTests/ChildInstallTests.cs`: migrate to `ManifestRevisions`, publish revisions (several per capability in mixed version order, and an `a2a` card with duplicate skill `id`s), migrate to latest and run the install step; children and generation 1 equal those of the same sequence published with the feature; the first duplicate by position takes the key, the other is `is_duplicate` and absent from every later revision; every entry has exactly one child record (SC-002); a repeat run changes nothing (row counts and IDs equal); every revision verifies exactly as before; audit 1603 per capability (FR-016, FR-016a)
- [X] T058 [P] [US5] Add the rollout-gap case to `ChildInstallTests.cs`: with a capability revision that has no generation 1 (migrated, install not yet run), a new-API publication answers `503 {"error":"unavailable"}` and stores nothing (maintainer-approved A7)

### Implementation for User Story 5

- [X] T059 [US5] Fix anything `ChildInstaller` (T023) needs for T057–T058 to pass; show the "Duplicate entry" note for `is_duplicate` children in `web/src/components/ChildDetail.tsx` (with a case in `ChildDetail.test.tsx`)

**Checkpoint**: All user stories are independently functional

---

## Phase 8: Polish & Cross-Cutting Concerns (pins, telemetry, performance, demo, evidence)

**Purpose**: Deliberate pin updates, telemetry and performance proofs, the cumulative demo and its evidence

- [X] T060 [P] Write `tests/Gozleme.Api.IntegrationTests/ChildTelemetryTests.cs`: `gozleme.children.assign` under `gozleme.manifest.publish` with the four counts; `gozleme.projection.generation.record`; `gozleme.children.install`; `gozleme.operator.projection_regenerate`; counters with bounded labels; no name, title, native name, digest or document in spans or logs (FR-019)
- [X] T061 [P] Write `tests/Gozleme.Api.IntegrationTests/RevisionChildrenPerformanceTests.cs`: a 1,000-entry MCP manifest publishes within the GOZ-0007 budget (2 s) plus 1 s (SC-006, research R12)
- [X] T062 [P] Update `tests/Gozleme.Api.IntegrationTests/BootstrapBoundaryTests.cs` (table list + `capability_children`; the `(manifest|revision|projection)` set also allows `projection_generations` and `revision_children`; `POST /api/v1/publish`, `GET /api/v1/capabilities`, `/catalog`, `/search` still `404`); confirm `ManifestNoStorageTests` (validate stores nothing) and the GOZ-0006 rule catalogue test (40 rules, `rule_catalogue_version` unchanged) still pass unchanged
- [X] T063 Write `e2e/tests/children-demo.spec.ts` following quickstart "Demo walkthrough" steps 1–6 with run-unique versions and native names (research R14): browse children; continuity with Present/Absent; the `a2a` duplicate `422` while validate answers `200`; `projection-regenerate` via `operator.ts` (exit 0, identical digest; unknown revision exit 2) and the generations section; swapped-ID `404`s; `psql` refusals as both logins; dave's empty `404` on all five endpoints; signed-out redirect and `401`; `DELETE` → `405`; GOZ-0007 suite revisions have generation 1 and children and verify; Jaeger trace screenshots of `gozleme.children.assign` under `gozleme.manifest.publish`, `gozleme.projection.generation.record` and `gozleme.operator.projection_regenerate`; `gozleme_children_assignments_total` and `gozleme_projection_generations_total` present; a no-name scan of Jaeger, Loki and API logs for the run-unique native names, titles and digests (SC-007)
- [X] T064 Add project `goz-0008` (`testMatch: /children-demo\.spec\.ts/`, `dependencies: ["goz-0007"]`) to `e2e/playwright.config.ts`, add `"goz-0008": "008-child-identities"` to `specDirs` in `e2e/tests/evidence.ts`, and write `docs/specs/008-child-identities/evidence/demo-run.json` and `children-evidence.json` (child IDs, generation IDs, run ID) with sanitized screenshots under `evidence/screenshots/` (Children view, child page with Present/Absent, revision Children section, the `422` problem list, Projection generations section, Jaeger traces) and regression screenshots under `evidence/screenshots/regression-goz-NNNN/`
- [X] T065 Run `make test`, `make lint`, `make build`, `make validate` and `make demo` (all suites `goz-0000` … `goz-0008`); record results in `docs/specs/008-child-identities/progress.yaml` `evidence` and update the GOZ-0008 registry entry gates in `.gozleme/spec-registry.yaml` (FR-022)
- [X] T066 Confirm the security-review items in `progress.yaml` (plan "Post-design re-check") for `review-security` during convergence: the `is_operator()` read branch on `manifest_revisions_select`, the statement-level entry check and partial unique index as the matching guarantee, operator-only duplicate children, the rollout-gap `503`, the no-name scan coverage of spans, logs and command output, and `projection-regenerate` without validation rules

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: T001 and T002 in parallel.
- **Foundational (Phase 2)**: after Setup. T003–T010 are one migration file, in order; T011 after T010; T012 after T011. T013–T014 in parallel, then T015 and T016. T017–T019 in parallel. T020 after T011 and T015; T021 after T016, T018, T020; T022 after T021; T023 after T015, T018, T019, T020; T024 after T011.
- **User stories (Phases 3–7)**: all depend on Phase 2. In spec order for a single implementer. US2 and US5 web tasks extend US1's `ChildDetail.tsx`; US3's page task extends the revision page that US1 changes.
- **Polish (Phase 8)**: T060–T062 after Phase 2; T063–T064 after all stories; T065 then T066 last before convergence.

### User Story Dependencies

- **US1**: none beyond Foundational.
- **US2**: API tests need only Foundational; T038 extends T032.
- **US3**: T040, T041, T044, T045 are independent of US1; T046 extends T029's file; T048 extends T033's page.
- **US4**: tests need the endpoints of US1 and US3 (five routes); T055 extends `ChildEndpoints.cs`.
- **US5**: tests need only Foundational (T023); T059's UI note extends T032.

### Within Each User Story

- Tests are written first and fail before the implementation they cover.
- API before BFF route before component before page.

## Parallel Opportunities

- Setup: T001, T002.
- Foundational: T013, T014 together; T017, T018, T019 together.
- US1: T025, T026 together; T030, T031 together.
- US2: T035, T036, T037 together.
- US3: T040, T041, T042, T043 together; T047, T049 alongside T044–T046.
- US4: T050–T054 together.
- US5: T057, T058 together.
- Polish: T060, T061, T062 together.

## Parallel Example: User Story 1

```bash
Task: "ChildPublishTests US1 cases in tests/Gozleme.Api.IntegrationTests/ChildPublishTests.cs"
Task: "ChildList, ChildDetail, RevisionChildren Vitest suites in web/src/components/"
Task: "children types and notice in web/src/lib/children.ts and children-server.ts"
Task: "GET BFF routes under web/src/app/api/.../capabilities/[capabilityId]/children/"
```

## Implementation Strategy

1. **Foundation first**: T001–T024. The migration and its guards, the pure matcher and duplicate rule, publication with children and generation 1, and the install step, so the existing demo stack can publish again.
2. **MVP**: Foundational + US1 — a member browses a capability's children, a child's page and a revision's children.
3. Add US2 (continuity and the duplicate refusal) and US3 (generations and the re-projection command).
4. Add US4 (immutability, integrity, authorization and isolation proofs) and US5 (install-step proofs).
5. Polish: telemetry and performance suites, pins, the `goz-0008` demo with Jaeger evidence. Then converge (with `review-security` on the items in T066), run the post-convergence impact analysis and the roadmap sync per `drive-speckit`.

**Total**: 66 tasks (Setup 2, Foundational 22, US1 10, US2 5, US3 10, US4 7, US5 3, Polish 7).

---

## Phase 9: Convergence

**Maintainer rulings (2026-11-19, convergence)**: F2 tighten the member insert policies; F3 if confirmed, publish rule plus install-step skip; F4 keep `503 unavailable` and document it; F14 exit 3 `not_installed`. Recorded in `progress.yaml` `decisions`.

- [X] T067 Make the combined `Gozleme.Api.IntegrationTests` run pass with 0 failures (load sensitivity of the `PublisherIsolationTests` TRUNCATE case, `TelemetryTests` outage and `RegistryOriginTests` different-origin), and record the green result in `progress.yaml` `evidence` per FR-022, T065 (partial)
- [X] T068 Tighten the member branches of `capability_children_insert`, `revision_children_insert` and `projection_generations_insert` in `src/Gozleme.Api/Persistence/Migrations/20261122000000_ChildIdentities.cs` so the revision must also satisfy `published_by = (SELECT gozleme.current_account_id()) AND published_at = now()` (the revision being published in this transaction; operator branch unchanged), and add negative cases to `tests/Gozleme.Api.IntegrationTests/ChildIntegrityTests.cs`: a member with a held claim inserting generation 1 or child records for an existing pending revision (install not run) is refused per FR-018, maintainer ruling F2 (partial)
- [X] T069 Write an integration test that publishes an MCP manifest whose tool native name and title contain an escaped `\u0000`, and an install-step case with such a stored revision. If the test confirms a failure, add publish rule `publish.nul_character` (publish rule catalogue, `422`, `publish.duplicate_entry` shape, location `projection` `/entries/<i>`, no echo; validate unchanged) to `src/Gozleme.Api/Children/PublishRules.cs` and `ChildEntries.cs`, make `ChildInstaller` skip such a revision, audit it by revision ID only and continue, map SQLSTATE `22P05`/`22021` on the child tables to `unavailable` in `RevisionPublisher.Classify`, and document the rule in `contracts/publish-delta.md` and the skip in `contracts/projection-generations.md` per FR-007, FR-016, maintainer ruling F3 (partial)
- [X] T070 Document in `docs/specs/008-child-identities/contracts/publish-delta.md` that a child-table refusal at insert (`23505`/`42501`) answers `503 unavailable` with nothing stored, and classify a commit-time `23000` from `capability_children_recorded` or `manifest_revisions_children` the same way instead of an unhandled `500` (`RevisionPublisher.cs`, `TenantTransaction.cs`), with a test, per FR-005a, FR-007, T022, maintainer ruling F4 (partial)
- [X] T071 Page the revision page's Children section: `listRevisionChildren` in `web/src/lib/children-server.ts` takes an `after` cursor, and `web/src/components/RevisionChildren.tsx` renders a Next link from `list.next`, with a Vitest case, per US1/AC3, FR-013 (partial)
- [X] T072 Extend `tests/Gozleme.Api.IntegrationTests/ChildAuthorizationTests.cs` with the `approver` and `reviewer` roles across the five endpoints, a signed-out HTTP `401` request per endpoint, and a member removed between requests answering `404` per T052, US4/AC3, FR-015 (partial)
- [X] T073 Add superuser `UPDATE`, `DELETE` and `TRUNCATE` attempts on the three child tables to `tests/Gozleme.Api.IntegrationTests/ChildImmutabilityTests.cs` (as in `RevisionImmutabilityTests`) per US4/AC1, FR-017 (partial)
- [X] T074 Add API cases for `comparedToPrevious` `different` and `not_comparable` (`tests/Gozleme.Api.IntegrationTests/ProjectionRegenerateTests.cs` or `ChildPublishTests.cs`) and a Vitest case for each label in `web/src/components/ProjectionGenerations.test.tsx` per T042, FR-011 (partial)
- [X] T075 Add `ChildTelemetryTests` cases for the `gozleme.children.install` span (revision and capability counts) and the `gozleme.operator.projection_regenerate` span (`gozleme.operator.exit_code`), each with the no-name scan, per T060, FR-019 (partial)
- [X] T076 Make `gozleme.children.assign` enclose the existing-children query, `ChildAssigner.Assign` and the inserts, and `gozleme.projection.generation.record` enclose the generation insert, in `RevisionPublisher.cs`, `ChildInstaller.cs` and `ChildTelemetry.cs`, and add the publication counters only after commit, per FR-019, T018, T021 (partial)
- [X] T077 Make `projection-regenerate` answer exit 3 with the fixed stderr word `not_installed` (audit 1602) for a known revision without generation 1, write exactly `failed` (no exception type name) on stderr for exit 1 in `src/Gozleme.Api/Children/ProjectionOperatorCommands.cs`, document `not_installed` in `contracts/projection-generations.md`, and assert exit 1 `failed` (run without the owner login), `projection_failed` and `not_installed` in `ProjectionRegenerateTests.cs` per FR-011a, US3/AC6, maintainer ruling F14 (contradicts)
- [X] T078 Add to `tests/Gozleme.Api.IntegrationTests/ChildIntegrityTests.cs` a case where the record matches the entry but the reused non-duplicate child has a different native name, and add generation-ID-as-child-ID and child-ID-as-generation-ID `404` cases per T051, US3/AC3, FR-010 (partial)
- [X] T079 In `e2e/tests/children-demo.spec.ts`, run the `gozleme_migrator` `UPDATE`/`DELETE`/`TRUNCATE` attempts also without an account context (operator) so the immutability guard refusal is captured in `children-evidence.json`, and add the duplicate skill name and the second revision's projection digest to the no-name scan values per US4/AC1, SC-007, T063 (partial)
- [X] T080 Make the `ProblemList.tsx` truncation note fit publish-time problems when the list comes from `PublishRevision.tsx` (no "validate again" for `publish.*` rules), with a Vitest case, per T039 (partial)
- [X] T081 Re-run `make test`, `make lint`, `make build`, `make validate` and `make demo` (goz-0000 … goz-0008) after T067–T080, refresh the evidence and screenshots, and update `progress.yaml` per FR-022 (partial)

## Phase 10: Convergence

**Maintainer ruling (2026-11-19, convergence pass 2)**: envelope U+0000 — extend `publish.nul_character` to envelope strings (part `envelope`, exact pointer), `422`; validate unchanged. Recorded in `progress.yaml` `decisions`.

- [X] T082 Extend `publish.nul_character` to the envelope strings stored in `manifest_revisions.envelope_summary` (`title`, `summary` and any other envelope string the summary holds) at `{part: "envelope", pointer: <the string>}` in `src/Gozleme.Api/Children/ChildEntries.cs` and `src/Gozleme.Api/Revisions/RevisionEndpoints.cs` (duplicates first, then projection, then envelope U+0000 problems; no value echoed; validate unchanged), document it in `contracts/publish-delta.md`, and add a unit case to `tests/Gozleme.Api.Tests/Children/ChildEntriesTests.cs` and an integration case (validate `200`, publish `422`, nothing stored) to `tests/Gozleme.Api.IntegrationTests/ChildPublishTests.cs` per FR-021, T069, maintainer ruling F3 and envelope-nul (partial)
- [X] T083 Map a row-level `WITH CHECK` refusal of a GOZ-0008 child insert (`42501` with no table name) to `unavailable` instead of `suspended` in `RevisionPublisher.Classify` (`src/Gozleme.Api/Revisions/RevisionPublisher.cs`), keeping a refusal of the revision insert itself `suspended`, and add a `Classify` unit test (child-table `42501`, null-table `42501` from a child insert, `23000` by constraint, `22P05`/`22021`) per FR-007, maintainer ruling F4 (partial)
- [X] T084 Record `gozleme.projection.generations{outcome=refused}` for the `not_installed` exit 3 in `src/Gozleme.Api/Children/ProjectionOperatorCommands.cs`, and assert it in `tests/Gozleme.Api.IntegrationTests/ProjectionRegenerateTests.cs` or `ChildTelemetryTests.cs` per FR-019, T076, T077 (partial)
- [X] T085 Re-run `make test`, `make lint`, `make build`, `make validate` and `make demo` (goz-0000 … goz-0008) on a clean working tree at the final commit, so `demo-run.json` pins that commit with `workingTreeCleanAtStart: true`, and update `progress.yaml` per FR-022, T081 (partial)

## Phase 11: Convergence

- [X] T086 Add the GOZ-0008 publication delta to `docs/specs/008-child-identities/contracts/children-api.openapi.yaml` (additive; the merged GOZ-0006 and GOZ-0007 contracts stay unchanged): the publish `422` body with problem `location.part` `projection` and `envelope` for publish rule catalogue v1 (`publish.duplicate_entry`, `publish.nul_character`), and the publish `503` causes (rollout gap, child-table refusal, `22P05`/`22021`) per FR-005a, FR-021, maintainer rulings A7, F3, F4, envelope-nul (contradicts)
- [X] T087 Correct the last bullet of `docs/specs/008-child-identities/contracts/publish-delta.md` so the span outcome for a child-table refusal is `unavailable`, matching `RevisionEndpoints.cs` (`PublishOutcome.Unavailable`) per FR-019, maintainer ruling F4 (contradicts)
- [X] T088 Update `docs/architecture/contracts/organization-permissions.md` (publish order: `publish.nul_character`, the rollout-gap `503`, the child-refusal `503`; the child write row: `published_by = current_account_id() AND published_at = now()`), the write row of `docs/specs/008-child-identities/contracts/permissions-delta.md`, and the "Member insert branch" of `docs/specs/008-child-identities/data-model.md` to the policies in `20261122000000_ChildIdentities.cs` per FR-018, maintainer rulings F2, F3, F4, A7 (partial)
- [X] T089 Add a convergence-rulings note to `docs/specs/008-child-identities/plan.md` and `research.md` (R7 exit-3 words) covering audit 1604, `publish.nul_character` (projection and envelope), the child-refusal `503` and `not_installed`, pointing to the `progress.yaml` decisions, per plan "Scale/Scope" and "Constraints", maintainer rulings F3, F4, F14, envelope-nul (partial)
- [X] T090 Add `presentIn` to `ChildSummary.required` in `docs/specs/008-child-identities/contracts/children-api.openapi.yaml`, matching `ChildContracts.cs` per FR-013 (partial)
- [X] T091 Document in `docs/specs/008-child-identities/contracts/projection-generations.md` the exit code and stderr word of `projection-regenerate` run without the owner login (the app login sees no revision), and assert that exact code and word in `ProjectionRegenerateTests.The_app_login_cannot_re_project_or_insert_a_later_generation` per US3/AC6, FR-011a (partial)
- [X] T092 Assert the immutability guard's SQLSTATE on every `TRUNCATE` attempt in `tests/Gozleme.Api.IntegrationTests/ChildImmutabilityTests.cs` (application, owner with and without account context, superuser) instead of any `PostgresException` per US4/AC1, FR-017 (partial)
- [X] T093 Add a real upgrade-path case to `tests/Gozleme.Api.IntegrationTests/ChildInstallTests.cs`: migrate to `ManifestRevisions`, publish revisions (mixed version order, an `a2a` duplicate), migrate to latest, run the install step, and compare children, generation 1, duplicate marking and verification with the same sequence published with the feature per T057, FR-016, FR-016a, US5/AC1–AC4 (partial)
- [X] T094 In `e2e/tests/children-demo.spec.ts` step 5, page through every revision of the seeded capability (not only the first 100) for the verification check, and record in `children-evidence.json` which revisions are excluded and why (tampered, current run) per US5/AC3 (partial)
- [X] T095 Show the revisions each child appears in (`presentIn`) in `web/src/components/ChildList.tsx`, with a Vitest case in `ChildList.test.tsx`, per US1 ("the revisions it appears in") (partial)
- [ ] T096 Re-run `make test`, `make lint`, `make build`, `make validate` and `make demo` (goz-0000 … goz-0008) on a clean working tree at the final commit, refresh the evidence and screenshots, and update `progress.yaml` per FR-022, T085 (partial)
