---

description: "Task list for GOZ-0005 Publisher identities and canonical capability IDs"
---

# Tasks: Publisher Identities and Canonical Capability IDs

**Input**: Design documents from `docs/specs/005-publisher-identities/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R13), data-model.md, contracts/ (publishers-api.openapi.yaml, namespace.md, operator-commands.md, permissions-delta.md), quickstart.md

**Tests**: Required. The spec and prompt demand database-layer refusals as both logins (FR-019, FR-024, SC-002), the role × action matrix (SC-005), identical not-found and unavailable answers (SC-004, SC-006), concurrency (SC-007), origin refusal (SC-003), a telemetry no-secret scan (SC-008) and a cumulative browser demo (FR-030; Constitution IV/VI).

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently. Stories run in priority order: US1, US2, US3, US5 (all P1), then US4 (P2).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)

## Path Conventions

API `src/Gozleme.Api/`, API tests `tests/Gozleme.Api.IntegrationTests/` and `tests/Gozleme.Api.Tests/`, web `web/src/`, demo `e2e/`, deployment `compose.yaml` and `deploy/helm/gozleme/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Vocabulary and configuration shared by every story

- [x] T001 Create `src/Gozleme.Api/Publishers/NamespaceRules.cs` (research R7, contracts/namespace.md):
  - `IsValidHandle(string)` and `IsValidCapabilityName(string)` implementing `^[a-z][a-z0-9]*(-[a-z0-9]+)*$` with `char` range checks only (no `Regex`, no culture, no trimming, case-folding or normalization). Handles are "2–39 characters", capability names "2–64".
  - `ReservedHandles`, the fixed sorted set `admin`, `api`, `capabilities`, `capability`, `gozleme`, `operator`, `publisher`, `publishers`, `registry`, `root`, `security`, `support`, `system`, `www`; `IsValidHandle` returns false for each.
  - `RuleText` constants for the `400 validation_failed` messages (one for handles, one for names).
- [x] T002 [P] Create `src/Gozleme.Api/Publishers/RegistryOrigin.cs` (research R6, contracts/namespace.md):
  - `RegistryOriginOptions` bound to `Gozleme:Registry:Origin` with `ValidateOnStart`.
  - `RegistryOrigin.IsValid(string)`: dot-separated labels of 1–63 `[a-z0-9-]`, no label starting or ending with `-`, at most 253 characters, no scheme, path, port, user information or trailing dot; ordinal ASCII checks only.
  - `CanonicalId.Publisher(origin, Guid)` → `gz://<origin>/pub/<uuid>` and `CanonicalId.Capability(origin, Guid)` → `gz://<origin>/cap/<uuid>`, with the UUID lowercase, hyphenated, 36 characters.
- [x] T003 [P] Create `src/Gozleme.Api/Publishers/PublisherOptions.cs`: `Gozleme:Publishers:RegisterPerHour`, default 10, range 1–100, validated on start (research R8)
- [x] T004 [P] Add `Gozleme__Registry__Origin: ${GOZLEME_REGISTRY_ORIGIN:-registry.demo.gozleme.test}` to the `migrate`, `api` and `seed` services in `compose.yaml`, document `GOZLEME_REGISTRY_ORIGIN` in `.env.example`, and add the required Helm value `registry.origin` (`required` fails the render when empty) in `deploy/helm/gozleme/values.yaml`, `templates/api.yaml` and `templates/migrate-job.yaml`; describe the one-off operator `Job` for `handle-*` commands in `deploy/helm/gozleme/templates/NOTES.txt`. Verify with `make helm-lint` (render passes with a value) and a negative render: `scripts/helm.sh template gozleme deploy/helm/gozleme --set registry.origin=` must fail with the `required` message. Wire the negative render into the `helm-lint` target so CI keeps it (FR-029)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Schema, model, permissions, audit and telemetry that every story uses

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T005 Write migration `src/Gozleme.Api/Persistence/Migrations/20261119000000_PublisherIdentities.cs` (with Designer and updated `GozlemeDbContextModelSnapshot.cs`), exactly as data-model.md and research R1–R5, starting from the verified scratch SQL. Additive, DDL only, no new role, no lift of the owner binding (FR-031):
  1. `gozleme.registry_origin` (`id smallint PK CHECK (id = 1)`, `origin text` with the host-name `CHECK` "≤ 253 characters", `recorded_at timestamptz DEFAULT now()`); `GRANT SELECT` only to `gozleme_app`.
  2. `gozleme.publishers` (`id uuid PK`, `organization_id uuid FK → organizations(id)`, `handle text` with the namespace and reserved-list `CHECK`s "2–39" and **not unique**, `display_name text` "1–100 characters", `created_at`, `created_by uuid FK → users(id) ON DELETE SET NULL`, `updated_at`; `UNIQUE (id, organization_id)`); RLS enabled and **forced**.
  3. `gozleme.capabilities` (`id`, `organization_id`, `publisher_id` with composite FK `(publisher_id, organization_id)` → `publishers(id, organization_id)`, `name text` namespace `CHECK` "2–64", `display_name` "1–100 characters", `created_at`, `created_by`, `updated_at`; `UNIQUE (publisher_id, name)`, `UNIQUE (id, organization_id)`); RLS enabled and **forced**.
  4. `gozleme.handle_claims` (`id`, `handle` with namespace and reserved `CHECK`s, `status` ∈ `blocked`, `earmarked`, `held`, `released`, `reclaimed`, nullable `organization_id FK`, nullable `publisher_id` with composite FK, `created_at`, nullable `held_at`, `ended_at`; status-shape `CHECK`; `ux_handle_claims_live UNIQUE (handle) WHERE status IN ('blocked','earmarked','held')`; `ux_handle_claims_publisher UNIQUE (publisher_id) WHERE publisher_id IS NOT NULL`); RLS enabled, **not forced**; `gozleme_app` `SELECT, INSERT, UPDATE`.
  5. `gozleme.alias_reservations` (`alias text PK`, `capability_id uuid UNIQUE` with composite FK `(capability_id, organization_id)` → `capabilities`, `organization_id`, `created_at`); RLS enabled, **not forced**; `gozleme_app` `INSERT` only.
  6. Non-definer `gozleme.account_has_role(org uuid, role text)` (plain `sql`, `STABLE`, `SET search_path = pg_catalog, pg_temp`), `REVOKE EXECUTE … FROM PUBLIC`, `GRANT EXECUTE … TO gozleme_app`. `account_is_admin` unchanged.
  7. Policies per data-model.md (`CASE` with the `rls_internal()` branch first on tenant tables): publishers select member / insert admin + `created_by = current_account_id()` / update admin with `WITH CHECK` requiring a `held` claim; capabilities select member / insert `publisher` + `created_by` + active publisher / update `publisher` + active publisher; handle_claims select own organizations / insert `held` by admin for a visible publisher / update `earmarked` → `held` by admin; alias_reservations insert requires `account_has_role(org,'publisher')`. No delete policy anywhere; `REVOKE DELETE, TRUNCATE` on all five tables from `gozleme_app`.
  8. Trigger functions (`plpgsql`, raise `42501`, `PUBLIC` execute revoked, no grant): `immutable_row_guard` (UPDATE/DELETE/TRUNCATE on `registry_origin` and `alias_reservations`), `publishers_guard` and `capabilities_guard` (only `display_name`, `updated_at` change; `created_by` only to `NULL`), `handle_claims_guard` (research R2/R4 state machine and who may take each step; refuses DELETE; `id`, `handle`, `organization_id`, `created_at` fixed; `publisher_id`, `held_at` unchanged on reclaim), `capabilities_reserve_alias` (`AFTER INSERT`, inserts `handle || '/' || name`), `alias_reservations_insert_guard` (`BEFORE INSERT`, refuses at `pg_trigger_depth() < 2`).

  `Down` drops everything in reverse order.
- [x] T006 [P] Create EF entities `src/Gozleme.Api/Publishers/Publisher.cs` and `src/Gozleme.Api/Publishers/Capability.cs` and map them in `src/Gozleme.Api/Persistence/GozlemeDbContext.cs` (snake_case columns, no navigation into claim tables; `handle`, `name`, ids and `organization_id` read-only after insert)
- [x] T007 [P] Extend `src/Gozleme.Api/Persistence/TableClassification.cs`: `publishers`, `capabilities` → `TenantOwned`; new classes `InstanceReference` (`registry_origin`) and `InstanceClaims` (`handle_claims`, `alias_reservations`) with per-table pinned privilege sets; `DefinerHelpers` unchanged. Add the two class rules to `src/Gozleme.Api/Persistence/RlsCatalog.cs` (research R1): `InstanceReference` = no `organization_id`, `gozleme_app` holds exactly `SELECT` and no column-level privilege, and no view depends on the table (`pg_depend`). Together with the source-scan pin in T011, this keeps one read path (FR-002); `InstanceClaims` = RLS enabled and not forced, ≥ 1 policy, `gozleme_app` holds exactly its pinned set, no `DELETE`/`TRUNCATE`
- [x] T008 [P] Extend `src/Gozleme.Api/Organizations/OrganizationPermissions.cs` (research R9, contracts/permissions-delta.md): resources `publisher`, `capability`; actions `publisher.list`, `publisher.read`, `publisher.register`, `publisher.rename`, `capability.list`, `capability.read`, `capability.create`, `capability.rename`; `admin` becomes an explicit list (no `OrgAction.All`) holding the publisher writes but not `capability.create`/`capability.rename`; `publisher` gets the capability writes; every role gets the four reads. Apply the delta (role rows, endpoint → action rows, database-enforcement rows) to `docs/architecture/contracts/organization-permissions.md`
- [x] T009 [P] Add audit events 1401–1408 to `src/Gozleme.Api/Identity/SecurityAudit.cs` (research R10): `PublisherRegistered(publisher, organization, user, claim)`, `PublisherRenamed`, `CapabilityCreated(capability, publisher, organization, user)`, `CapabilityRenamed`, `PublisherRegistrationRefused(category, organization, user)`, `HandleReserved(claim, kind, earmarkedOrganization?)`, `HandleReservationReleased(claim)`, `HandleReclaimed(claim, publisher, organization, earmark?)`. Opaque ids only; never a handle, name, alias or display name
- [x] T010 [P] Create `src/Gozleme.Api/Publishers/PublisherTelemetry.cs`: spans `gozleme.publisher.register`, `gozleme.publisher.rename`, `gozleme.capability.create`, `gozleme.capability.rename` (attributes `gozleme.organization.id`, `gozleme.publisher.id`, `gozleme.capability.id`, `gozleme.publisher.outcome`, set only after membership); counter `gozleme.publisher.operations` with labels `gozleme.publisher.operation` ∈ {`register`, `rename`, `capability_create`, `capability_rename`} and `gozleme.publisher.outcome` ∈ {`success`, `unchanged`, `validation_failed`, `handle_unavailable`, `conflict`, `forbidden`, `suspended`, `not_found`, `rate_limited`}; operator spans `gozleme.operator.handle_reserve|handle_release|handle_reclaim` with `gozleme.operator.exit_code` only
- [x] T011 Add `RegistryOriginReader` to `src/Gozleme.Api/Publishers/RegistryOrigin.cs`: reads `registry_origin` as `gozleme_app` through one parameterless `SELECT origin FROM gozleme.registry_origin`, caches the verified value. It is the only code that names `registry_origin` outside migrations and `MigrationRunner`, pinned by a source-scan unit test in `tests/Gozleme.Api.Tests/RegistryOriginReadPathTests.cs` (FR-002); exposes `GetVerifiedOriginAsync()` that answers unavailable (`503`) while unverified and stops the application on mismatch (research R6)
- [x] T012 Update test infrastructure: `tests/Gozleme.Api.IntegrationTests/Infrastructure/GozlemeApiFactory.cs` and `GozlemeDatabase.cs` set `Gozleme:Registry:Origin` (`registry.test.gozleme.test`) for migrate and API, and add an owner-login runner with no context; `TwoTenants.cs` exposes a publisher-member account per tenant; `OrgClient.cs` gains publisher and capability helpers for the eight endpoints
- [x] T013 Update `MigrationRunner` minimal path so tests can migrate: `src/Gozleme.Api/Persistence/MigrationRunner.cs` refuses a missing or invalid origin before touching the database, then after `MigrateAsync` runs `INSERT … ON CONFLICT (id) DO NOTHING` and re-reads (mismatch handling is completed in US2, T027)

**Checkpoint**: Foundation ready — the schema migrates, the catalog passes, and user story work can begin

---

## Phase 3: User Story 1 — Register a publisher and create a capability whose ID survives renaming (Priority: P1) 🎯 MVP

**Goal**: An admin registers a publisher; a `publisher` member creates a capability with a canonical ID and alias; renaming changes only the display name.

**Independent Test**: As the seeded admin register a publisher, as the seeded publisher member create a capability, rename it twice, and confirm the canonical ID and alias are byte-identical in the UI and the API (spec US1 Independent Test).

### Tests for User Story 1

- [x] T014 [P] [US1] `tests/Gozleme.Api.IntegrationTests/PublisherEndpointTests.cs` happy path: register (201 body has handle, organization opaque id, `gz://…/pub/<uuid>`), list and read as a plain member, create capability (`gz://…/cap/<uuid>`, alias `<handle>/<name>`), rename twice (ids and alias byte-identical), same-value rename returns success with no second audit event (spec Edge Cases), display name empty/whitespace/over 100 → `400`. Rate limit (FR-008, decision U1), with `RegisterPerHour` set low in the factory:
  - the attempt after the limit → `429 rate_limited` with `Retry-After`, audit 1405, counter outcome `rate_limited`, and no sign-in or account-registration audit or metric;
  - a non-member or plain member sending the same number of requests gets `404` or `403` every time, never `429`, and does not use up the admin's budget;
  - another account's budget is unaffected.
- [x] T015 [P] [US1] `tests/Gozleme.Api.Tests/CanonicalIdTests.cs`: rendering for both kinds, lowercase 36-character UUID, never contains organization id, handle, name or display name
- [x] T016 [P] [US1] Web Vitest `web/src/components/Publishers.test.tsx`: `PublisherList`, `RegisterPublisherForm`, `CapabilityList`, `CreateCapabilityForm`, `RenameDisplayNameForm`, `CanonicalId` (monospace + copy button), registration confirmation shows handle and organization opaque id (FR-009); `RegisterPublisherForm` renders the handle field empty with no placeholder, suggestion or autocomplete taken from the organization display name, the account email or its domain (`autoComplete="off"`, FR-010); route handlers in `web/src/lib/publishers.test.ts` refuse a cross-origin request (`isSameOrigin`)

### Implementation for User Story 1

- [x] T017 [US1] Create `src/Gozleme.Api/Publishers/PublisherContracts.cs` from `contracts/publishers-api.openapi.yaml`: register/rename/create requests, publisher and capability responses (including `publisherId`/`canonicalId`, `alias`, `origin`, `suspended`), error bodies `handle_unavailable`, `conflict`, `suspended`, `validation_failed`
- [x] T018 [US1] Create `src/Gozleme.Api/Publishers/PublisherStore.cs`: register = insert publisher (UUIDv7) then consume the caller's earmark (`UPDATE … WHERE handle = $1 AND status = 'earmarked' AND organization_id = $2`) or insert a `held` claim, in the request transaction; create capability = one insert (alias reserved by trigger); rename = update display name (no-op when unchanged); list/read with derived `suspended` (`NOT EXISTS held claim`); SQLSTATE mapping per research R8 (`23505 ux_handle_claims_live` → `handle_unavailable`; `23505` on `(publisher_id, name)` or `alias_reservations_pkey` → `conflict`; any other `23505`, `23503`, `42501` → empty `404`); parameters only, no lock on `organizations`
- [x] T019 [US1] Create `src/Gozleme.Api/Publishers/PublisherEndpoints.cs`: the eight endpoints on the organizations route group, each with `OrgActionAttribute`; order membership `404` → permission `403` → validation `400` (`NamespaceRules`, `DisplayNames`) → target `404` → suspension `409` → write; audit 1401–1404 and telemetry after success. The registration rate limit (`PartitionedRateLimiter` partitioned by account id, fixed window `RegisterPerHour`) is acquired **in the handler after `AuthorizeAsync` passes** and before validation, not with `RequireRateLimiting` middleware (research R8, decision U1). A rejection answers `429 {"error":"rate_limited"}` with `Retry-After` and `Cache-Control: no-store`, records operation `register` with outcome `rate_limited`, and writes audit 1405. A non-member never gets `429`
- [x] T020 [US1] Create `src/Gozleme.Api/Publishers/PublisherServiceCollectionExtensions.cs` and register the module, options and the rate-limit policy in `src/Gozleme.Api/GozlemeApp.cs`
- [x] T021 [US1] Seed: in `src/Gozleme.Api/Identity/OperatorCommands.cs`, acting as alice, grant carol `{member,publisher}` in Seeded Demo Org in the restore path (idempotent, FR-029); no publisher is seeded (research R11)
- [x] T022 [P] [US1] Create `web/src/lib/publishers.ts` (types, API calls, error messages "That handle isn't available.", "That name is already used under this publisher.") and the four route handlers under `web/src/app/api/organizations/[organizationId]/publishers/` (`route.ts` POST, `[publisherId]/route.ts` PATCH, `[publisherId]/capabilities/route.ts` POST, `[publisherId]/capabilities/[capabilityId]/route.ts` PATCH), each with `isSameOrigin()`; browser spans `gozleme.web.publisher.register`, `gozleme.web.capability.create`
- [x] T023 [US1] Create pages `web/src/app/organizations/[organizationId]/publishers/page.tsx` and `.../publishers/[publisherId]/page.tsx` (gate with `getSession()`, "Organization not found" on `404`, controls only when the caller's permissions include the action) and components `web/src/components/{PublisherList,RegisterPublisherForm,CapabilityList,CreateCapabilityForm,RenameDisplayNameForm,CanonicalId}.tsx`; link *Publishers* from `web/src/app/organizations/[organizationId]/page.tsx`

**Checkpoint**: US1 works end to end in the browser and API

---

## Phase 4: User Story 2 — The origin and every alias stay permanently reserved (Priority: P1)

**Goal**: Mismatched origin refuses migrate and start; taken handles and duplicate names are refused; no login can change an immutable field.

**Independent Test**: Restart with a different origin and observe the refusal; restore and see identical IDs; attempt duplicate handle, duplicate name and every immutable-field change through the API and as both database logins (spec US2 Independent Test).

### Tests for User Story 2

- [x] T024 [P] [US2] `tests/Gozleme.Api.IntegrationTests/RegistryOriginTests.cs`: first migrate records the origin; re-migrate with the same value is a no-op; missing, invalid or different origin → migrator exits non-zero with "configured origin differs from the recorded origin" (or the invalid message) and applies no migration; API start refused on mismatch; API starts with the database down and publisher endpoints answer `503` until verified (keep `DatabaseOutageTests` green); owner `UPDATE`/`DELETE` on `registry_origin` refused
- [x] T025 [P] [US2] `tests/Gozleme.Api.IntegrationTests/PublisherIsolationTests.cs` immutability and uniqueness matrix (research R2, R5), each row as `gozleme_app` and as the owner: change `handle`, `name`, `id`, `organization_id`, `publisher_id`, `created_at` → refused; delete/truncate publisher, capability, claim, alias → refused; direct `alias_reservations` insert → refused; `SELECT` on `alias_reservations` as app → permission denied; rows unchanged after each attempt
- [x] T026 [P] [US2] In `PublisherEndpointTests.cs`: `409 {"error":"handle_unavailable"}` byte-identical for another organization's admin, the owner's own retry and a blocked handle (SC-004); duplicate capability name → `409 conflict`; no route exists to change a handle, name or organization (PATCH with extra fields ignores or rejects them and nothing changes); concurrency: 10 rounds of two organizations registering the same handle and two members creating the same name → exactly one success, never a `500` (SC-007)

### Implementation for User Story 2

- [x] T027 [US2] Complete origin handling in `src/Gozleme.Api/Persistence/MigrationRunner.cs`: validate → compare with the existing recorded value **before** `MigrateAsync` → migrate → record → verify; clear operator messages; never overwrite
- [x] T028 [US2] Add the blocking `RegistryOriginCheck : IHostedService` in `src/Gozleme.Api/Publishers/RegistryOrigin.cs`, registered in `GozlemeApp.cs`: throws on mismatch so the host does not start; tolerates an unreachable database (deferred verification via `RegistryOriginReader`); `Program.cs` wiring if needed
- [x] T029 [US2] Verify and, if needed, correct the `PublisherStore` SQLSTATE mapping and the `TenantTransaction` aborted-transaction path so a unique violation never yields a `500` (research R8 "lost redemption race")

**Checkpoint**: US1 and US2 pass together

---

## Phase 5: User Story 3 — Only the right roles can register publishers and create capabilities (Priority: P1)

**Goal**: The role table governs the new actions in the API and the database; non-members and cross-organization IDs get identical not-found; work survives role loss.

**Independent Test**: For each built-in role × new action compare with the contract table; remove `publisher` from the creator and confirm nothing changes (spec US3 Independent Test).

### Tests for User Story 3

- [x] T030 [P] [US3] In `PublisherEndpointTests.cs`: the full built-in role × eight actions matrix read from `OrganizationPermissions` against the API (SC-005); admin without `publisher` → `403` on create/rename capability; non-member and unknown organization → identical empty `404` for all eight endpoints, including with a valid foreign publisher or capability id; organization B's ids under organization A's path → identical `404`; capability id under the wrong publisher → `404`; malformed ids → `404` (SC-006)
- [x] T031 [P] [US3] In `PublisherIsolationTests.cs` database role rows (research R5), as `gozleme_app` and as the owner with a member context: non-admin inserts publisher → refused; admin without `publisher` inserts capability → refused; member renames capability / publisher member renames publisher → `UPDATE 0`; allowed writes succeed; reads are member-level; a non-member context sees 0 rows (FR-024). Role lost during a request (spec Edge Cases): on one connection open a member transaction for carol, revoke carol's `publisher` from a second connection and commit, then insert a capability in carol's still-open transaction. `TenantTransaction` uses the default `READ COMMITTED`, so the insert statement sees the committed revoke and is refused (`42501`, which the API maps to `404`). Also insert and commit a capability in carol's transaction before a revoke, and assert it is unchanged afterwards. Together these pin "decided by the role the request's own statement reads; nothing created before is affected"
- [x] T032 [P] [US3] Role loss in `PublisherEndpointTests.cs`: remove carol's `publisher`, then remove carol from the organization; publishers, capabilities, canonical IDs and aliases unchanged and still listed (FR-021); account deletion sets `created_by` to `NULL` only
- [x] T033 [P] [US3] Update `tests/Gozleme.Api.Tests/OrganizationPermissionsTests.cs` (admin lacks `capability.create`, publisher holds capability writes, reads for every role) and `tests/Gozleme.Api.IntegrationTests/OrganizationPermissionContractTests.cs` (17 scoped endpoints; resources + `publisher`, `capability`; rendered table equals the living contract)
- [x] T034 [P] [US3] Web Vitest in `web/src/components/Publishers.test.tsx`: register form hidden without `publisher.register`, create/rename capability forms hidden without `capability.create`/`capability.rename`, a `403` shows the permission notice and refreshes

### Implementation for User Story 3

- [x] T035 [US3] Route `403` in the new forms to `forbiddenNoticePath` and `router.refresh()` (mirroring GOZ-0004 T038) in `web/src/components/RegisterPublisherForm.tsx`, `CreateCapabilityForm.tsx`, `RenameDisplayNameForm.tsx`; fix any API ordering defects the matrix reveals in `PublisherEndpoints.cs`

**Checkpoint**: All P1 stories except operator control pass

---

## Phase 6: User Story 5 — An operator can prevent and undo handle squatting (Priority: P1)

**Goal**: Operator command-line reserve, release and reclaim on the claim table only; reclaim suspends the holder; retired aliases stay reserved forever.

**Independent Test**: Reserve one handle for an organization and block another; confirm "unavailable" for others and success for the earmarked organization; reclaim a squatted handle and confirm suspension, unchanged IDs and aliases, re-registration, and refusal of the retired name (spec US5 Independent Test).

### Tests for User Story 5

- [x] T036 [P] [US5] `tests/Gozleme.Api.IntegrationTests/HandleClaimTests.cs` (research R2–R4 matrices): earmark consumed by the earmarked admin only, other admin `UPDATE 0`; claims of other organizations invisible (0 rows), blocked rows invisible to every account; app cannot insert `blocked`/`earmarked` or move `held` → `reclaimed`; owner cannot insert `held`, un-reclaim, release a held handle or change a handle; owner without context sees 0 publishers/capabilities; suspended publisher: create capability and rename publisher/capability refused in the database; new holder of a reclaimed handle: retired name `23505 alias_reservations_pkey`, new name ok
- [x] T037 [P] [US5] `tests/Gozleme.Api.IntegrationTests/OperatorHandleCommandTests.cs`: exit codes 0/1/2/3/4/64 and stdout/stderr text per contracts/operator-commands.md; earmark for unknown organization → 2; reserved or invalid handle → 3; handle not free → 4; reclaim with earmark in one transaction; audit 1406–1408 with opaque ids only and no handle in any field, log or span; no HTTP route matches `/handles`, `/reclaim`, `/reservations`, `/unsuspend` or `/transfer` (US5.6)
- [x] T038 [P] [US5] In `PublisherEndpointTests.cs`: blocked and earmarked-for-another handles answer the same `handle_unavailable` body as a taken one; after reclaim the old publisher reads with `suspended: true` and unchanged ids/aliases, and create/rename answer `409 {"error":"suspended"}`

### Implementation for User Story 5

- [x] T039 [US5] Create `src/Gozleme.Api/Identity/HandleOperatorCommands.cs`: `handle-reserve --handle <h> [--organization <uuid>]`, `handle-release --handle <h>`, `handle-reclaim --handle <h> [--organization <uuid>]`, connecting with `ConnectionStrings:GozlemeMigrator` and no account context; validate with `NamespaceRules`; map `23503` → 2, `23505` → 4, guard refusal → 2/4 as specified; write only `handle_claims`; audit after commit; operator spans; dispatch in `src/Gozleme.Api/Program.cs` (no origin or context key required)
- [x] T040 [US5] Suspension in the product: `PublisherStore` returns `suspended` from the derived state and the endpoints answer `409 suspended` before writing; web shows a "Suspended" badge on publisher and capabilities and hides forms (`web/src/components/PublisherList.tsx`, `CapabilityList.tsx`, publisher detail page)

**Checkpoint**: All P1 stories pass

---

## Phase 7: User Story 4 — Handles and names follow one predictable namespace rule (Priority: P2)

**Goal**: Valid input is stored exactly as typed; invalid input is rejected with the rule, never normalized.

**Independent Test**: Submit a table of valid and invalid handles and names through the API and the form (spec US4 Independent Test).

### Tests for User Story 4

- [x] T041 [P] [US4] `tests/Gozleme.Api.Tests/NamespaceRulesTests.cs`, run under `InvariantGlobalization=true`: valid table (`ab`, `a1`, `acme-tools`, 39/64-character maxima) and invalid table (uppercase, `İ`/`ı`, Cyrillic look-alikes, full-width letters, whitespace, tab, leading/trailing/double hyphen, leading digit, `_`, `.`, 1 character, 40/65 characters, every reserved handle); origin valid/invalid table (exactly 253 characters accepted and 254 rejected; label of 63 accepted and 64 rejected; scheme, port, path, user info, trailing dot, uppercase rejected)
- [x] T042 [P] [US4] In `PublisherEndpointTests.cs`: the same table through the API → valid stored byte-identical, invalid → `400 validation_failed` naming the field and rule, nothing created; the database `CHECK`s refuse invalid handles and names inserted directly as both logins

### Implementation for User Story 4

- [x] T043 [US4] Show the rule text and keep the typed value unchanged on validation errors in `RegisterPublisherForm.tsx` and `CreateCapabilityForm.tsx` (no client-side lowercasing or trimming); add the form-level case to `Publishers.test.tsx`

**Checkpoint**: All user stories independently functional

---

## Phase 8: Polish & Cross-Cutting Concerns (pins, telemetry, demo, evidence, docs)

- [x] T044 Deliberate pin updates (research R12): `e2e/tests/bootstrap-demo.spec.ts` `LATEST_MIGRATION` → `/_PublisherIdentities$/`; `MigrationAndRoleTests.cs` 6 migrations, latest `20261119000000_PublisherIdentities`; `StatusEndpointTests.cs` new `schemaVersion`; `BootstrapBoundaryTests.cs` schema table list + the five tables, and `GET /api/v1/capabilities` / `POST /api/v1/publish` still `404` (FR-025); `RlsIsolationTests.cs` + `publishers`, `capabilities` for app and owner; `TenantSeedTests.cs` carol holds `{member,publisher}`; `OrganizationBoundaryTests.cs` unchanged and passing
- [x] T045 [P] `tests/Gozleme.Api.IntegrationTests/RlsCatalogTests.cs`: the catalog passes with the new classes, plus negative tests: an `InstanceReference` table with an `organization_id` column, an extra app privilege, a column-level grant or a dependent view fails; an `InstanceClaims` table with forced RLS, no policy, a `DELETE`/`TRUNCATE` grant or a privilege outside its pinned set fails; `PUBLIC` execute on any new function fails; no new definer function
- [x] T046 [P] `tests/Gozleme.Api.IntegrationTests/PublisherTelemetryTests.cs`: spans and counter labels per T010 for each outcome; audit 1401–1408 fields; captured spans, logs and metrics contain no handle, capability name, alias, display name, email, `v1.` context or key (SC-008); counter labels bounded
- [x] T047 Add the `goz-0005` Playwright project to `e2e/playwright.config.ts` (depends on `goz-0004`) and the `specDirs` entry `docs/specs/005-publisher-identities` in `e2e/tests/evidence.ts`
- [x] T048 Write `e2e/tests/publisher-demo.spec.ts`, quickstart steps 1–5 in the browser with screenshots: alice registers `demo-NNNNNN` (confirmation with handle, organization opaque id, publisher ID; no create form for alice); carol creates `weather-lookup`; two renames with byte-identical canonical ID and alias in the UI and API (SC-001). Measure the wall-clock time from alice's first navigation to the Publishers page through carol's second rename, assert under 120 s, and record `us1ElapsedSeconds` in `publisher-evidence.json`; dave gets `handle_unavailable` byte-identical to alice's retry; carol's duplicate → `409 conflict` and the UI message
- [x] T049 In `publisher-demo.spec.ts`, quickstart steps 6–8: API role × action matrix and non-member `404`; database refusals as `gozleme_app` (`asApp`) and as the owner for every immutable field and delete, rows unchanged, rolled back; origin mismatch — recreate the API with `GOZLEME_REGISTRY_ORIGIN=other.demo.gozleme.test`, assert non-zero exit and message, restore with `composeWithEnv`, IDs unchanged (SC-003). Write `docs/specs/005-publisher-identities/evidence/publisher-evidence.json` (sanitized: opaque ids and synthetic handles only)
- [x] T050 In `publisher-demo.spec.ts`, quickstart steps 9–11: reserve and block via `docker compose run --rm migrate handle-reserve …`; dave unavailable for both; alice registers the earmarked handle; dave squats `squat-NNNNNN` and creates `forecast`; `handle-reclaim … --organization <Seeded Demo Org>`; dave's page shows **Suspended** with the same IDs and no forms (screenshot); alice registers the handle; carol's `forecast` → `409`, `radar` ok (SC-010); boundary routes still `404`; Jaeger spans, Prometheus `gozleme_publisher_operations_total` (poll ≤ 90 s), Loki audit 1401–1408 with opaque ids, and the no-secret scan of spans, logs and screenshots
- [x] T051 Run the cumulative demo (`make demo`) with earlier suites writing to `evidence/screenshots/regression-goz-000N/`, and record `docs/specs/005-publisher-identities/evidence/demo-run.json` via the `validate-demo` skill; all suites `goz-0000`…`goz-0005` must pass (SC-009)
- [x] T052 [P] Update `docs/specs/005-publisher-identities/progress.yaml`, `.gozleme/spec-registry.yaml` and the generated register (`scripts/generate_register.py`); run `make lint`, `make test` and `make validate` and record the counts; flag the Q11 owner-login design for `review-security` at convergence

---

## Dependencies & Execution Order

- **Setup (T001–T004)** → **Foundational (T005–T013)** → user stories. T005 (migration) blocks T006, T007, T011–T013 for testing; T008–T010 can proceed alongside it.
- **US1 (T014–T023)** is the MVP and supplies the store, endpoints and pages every other story extends.
- **US2 (T024–T029)** depends on Foundational and US1's store/endpoints (for the API cases); its database tests (T024, T025) depend only on Foundational.
- **US3 (T030–T035)** depends on US1's endpoints; database tests (T031) depend only on Foundational.
- **US5 (T036–T040)** depends on US1's store (registration and suspension) and Foundational; T036 and T037 can start after Foundational.
- **US4 (T041–T043)** — T041 depends only on T001/T002; T042–T043 depend on US1.
- **Polish (T044–T052)** depends on all stories. T051 (demo run) runs last before T052.

## Parallel Opportunities

- T002, T003, T004 together after T001.
- T006–T010 together alongside T005 (different files).
- US1 tests T014, T015, T016 together; web T022/T023 alongside API T017–T020.
- Database suites T025, T031, T036 and unit T041 can all start right after Foundational.
- T030–T034 together; T036–T038 together; T045 and T046 together.

## Parallel Example: User Story 1

```bash
Task: "PublisherEndpointTests happy path in tests/Gozleme.Api.IntegrationTests/PublisherEndpointTests.cs"
Task: "CanonicalIdTests in tests/Gozleme.Api.Tests/CanonicalIdTests.cs"
Task: "Publishers Vitest in web/src/components/Publishers.test.tsx"
```

## Implementation Strategy

1. **MVP**: Setup + Foundational + US1 — register, create, rename with stable IDs, in the UI and API.
2. Add US2 (origin permanence, uniqueness, immutability for every login) and US3 (role matrix, database role enforcement), completing the decided boundaries.
3. Add US5 (operator reserve and reclaim), the last P1 story.
4. Add US4 (namespace table end to end).
5. Polish: pins, catalog negatives, telemetry, cumulative demo and evidence. Then converge (with `review-security` on Q11), run the post-convergence impact analysis and the roadmap sync per `drive-speckit`.

**Total**: 52 tasks (Setup 4, Foundational 9, US1 10, US2 6, US3 6, US5 5, US4 3, Polish 9).

## Phase 9: Convergence

- [x] T053 Bind a held handle claim to its publisher's handle in the database: in `20261119000000_PublisherIdentities.cs` require `p.handle = handle_claims.handle` in the `handle_claims_insert` and `handle_claims_update` `WITH CHECK` sub-selects (and the equivalent check in `handle_claims_guard`), so a publisher row cannot carry a handle other than its claim's; add negative tests in `HandleClaimTests.cs` (mismatched handle on held insert and on earmark consume, as `gozleme_app` and as the owner) per FR-007, FR-014, FR-037, plan: forged or missing alias impossible (partial)
- [x] T054 Add `publishers` and `capabilities` to `tests/Gozleme.Api.IntegrationTests/RlsIsolationTests.cs` `Tables`, for the app and the owner login with no context, as T044 requires per research R12 (partial)
- [x] T055 Add API-host tests in `RegistryOriginTests.cs` for a missing, empty or invalid configured origin (start refused) and for a mismatch first seen after a deferred start (application stops) per US2/AC2, FR-003 (missing)
- [x] T056 Assert audit events 1406, 1407 and 1408 in `OperatorHandleCommandTests.cs` with opaque ids only and no handle in any field per US5/AC5, FR-036 (missing)
- [x] T057 Make `handle_claims_guard` set `created_at`, `held_at` and `ended_at` from `now()` instead of accepting caller values per research R2 audit chronology (partial)
- [x] T058 Update living docs: InstanceReference/InstanceClaims rules in `docs/engineering/local-development.md`; in `contracts/namespace.md` state that a handle resolves only through its held claim and that alias resolution is not offered in v1; add the `503` (origin unverified) and register `429` steps to the ordering in `docs/architecture/contracts/organization-permissions.md`; correct the `TableClassification.GlobalIdentity` summary for the registration counter per plan: living contracts (partial)
- [x] T059 Rename the Publisher response field `publisherId` to `canonicalId` (`gz://<origin>/pub/<uuid>`) in `contracts/publishers-api.openapi.yaml`, `PublisherContracts.cs`, `PublisherEndpoints.cs`, `web/src/lib/publishers.ts`, the web components, integration/Vitest tests and `e2e/tests/publisher-demo.spec.ts`; `Capability.publisherId` stays the owning publisher's UUID per maintainer decision 2026-11-17 (converge-arch-M1) (contradicts)
- [x] T060 Amend `docs/architecture/security-invariants.md`: the owner login is the installation authority over `handle_claims`/`alias_reservations`, bound only by guard triggers to one-way transitions; the owner credential must be restricted and its use audited per maintainer decision 2026-11-17 (Q11 conditions) (missing)
