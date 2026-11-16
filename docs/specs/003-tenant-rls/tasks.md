---

description: "Task list for GOZ-0003 Tenant RLS and visibility scaffolding"
---

# Tasks: Tenant RLS and Visibility Scaffolding

**Input**: Design documents from `docs/specs/003-tenant-rls/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R14), data-model.md, contracts/tenant-context.md, quickstart.md

**Tests**: Required. The spec and prompt demand negative cross-tenant tests, boundary tests for the stated "never trust caller-set session variables" rule, telemetry no-secret checks and a cumulative browser demo (FR-030, Constitution IV/VI).

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US6)

## Path Conventions

API `src/Gozleme.Api/`, API tests `tests/Gozleme.Api.IntegrationTests/` and `tests/Gozleme.Api.Tests/`, demo `e2e/`, deployment `deploy/`, Compose `compose.yaml`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Configuration and wiring for the context key

- [x] T001 Create `src/Gozleme.Api/Tenancy/TenancyOptions.cs` (section `Gozleme:Rls`, `ContextKey` required with at least 32 characters, validated on start, never logged or included in validation messages) and `src/Gozleme.Api/Tenancy/TenancyServiceCollectionExtensions.cs` (`AddGozlemeTenancy(this IHostApplicationBuilder)`: binds and validates options, and registers `TenantContextSigner`). Call it from `src/Gozleme.Api/GozlemeApp.cs`
- [x] T002 [P] Add `GOZLEME_CONTEXT_KEY` to `compose.yaml` for the `migrate`, `api` and `seed` services (`Gozleme__Rls__ContextKey: ${GOZLEME_CONTEXT_KEY:-local-dev-context-key-not-a-secret-0001}`) and to `.env.example` with a throwaway-key comment
- [x] T003 [P] Allow-list the throwaway keys in `.gitleaks.toml` (`local-dev-context-key-not-a-secret-[0-9]{4}`, `it-context-key-[A-Za-z0-9-]+`), and add the dev key to `forbiddenStrings` in `e2e/tests/compose.ts`
- [x] T004 [P] Helm: in `deploy/helm/gozleme/templates/api.yaml` and `deploy/helm/gozleme/templates/migrate-job.yaml`, read `Gozleme__Rls__ContextKey` from the existing `database.existingSecret` key `rls-context-key`. Document the new secret key, the `pgcrypto` requirement and key rotation in `deploy/helm/gozleme/README.md` and `values.yaml` comments

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The signer, the migration, key provisioning and the transaction filter that every story uses

- [x] T005 Create `src/Gozleme.Api/Tenancy/TenantContextSigner.cs`: `Sign(Guid account, DateTimeOffset now)` returns `v1.<uuid D lowercase>.<unix+300>.<hex HMAC-SHA256>` over the UTF-8 payload (contract §1). It uses `TimeProvider`, and the key bytes are the UTF-8 encoding of `ContextKey`
- [x] T006 [P] Unit tests in `tests/Gozleme.Api.Tests/TenantContextSignerTests.cs`: format, a known-answer MAC vector, expiry = now + 300 s, lowercase UUID, and options rejecting a missing or short key without echoing it
- [x] T007 Write migration `src/Gozleme.Api/Persistence/Migrations/20261117000000_TenantRls.cs` (inline `[DbContext]`/`[Migration]` attributes, no `.Designer.cs`; model snapshot unchanged), raw SQL per research R4–R7 and data-model:
  - create schema `gozleme_private` (no grants) and install `pgcrypto` into it;
  - create `context_keys` with its checks;
  - create the seven helpers (`SECURITY DEFINER`, `SET search_path = pg_catalog, pg_temp`, `REVOKE EXECUTE … FROM PUBLIC`, `GRANT EXECUTE … TO gozleme_app`; `rls_internal` stays plain `SQL STABLE`);
  - `ENABLE` and `FORCE ROW LEVEL SECURITY` and the per-command policies on `organizations`, `memberships` and `invitations`, every predicate in the `CASE WHEN gozleme.rls_internal() THEN true ELSE … END` form;
  - a full `Down`.
- [x] T008 Provision the key in `src/Gozleme.Api/Persistence/MigrationRunner.cs`: after `MigrateAsync`, read `Gozleme:Rls:ContextKey` (fail with exit code 1 and a fixed message if it is missing or short), then upsert `gozleme_private.context_keys` with the key as a parameter. Log only "context key provisioned" or "unchanged", never the key
- [x] T009 Update the test infrastructure: in `tests/Gozleme.Api.IntegrationTests/Infrastructure/GozlemeDatabase.cs`, pass the test key `it-context-key-0003-…` to `MigrateAsync`, and add `OwnerLoginConnectionString` (the `gozleme_migrator` login itself), `SignContext(Guid account, DateTimeOffset? expires)` and `TenantQueryAsync(connectionString, token, sql, params…)` helpers that run inside an explicit transaction. In `GozlemeApiFactory.cs`, add the same key to `Defaults`
- [x] T010 Create `src/Gozleme.Api/Tenancy/TenantTelemetry.cs`:
  - `StartTransaction()` returns the `gozleme.tenant.transaction` activity;
  - `Established(activity, result)` adds the event with `gozleme.tenant.context.result`;
  - `Cleared(activity, outcome)`;
  - counter `gozleme.tenant.denials` with label `gozleme.tenant.denial.reason` ∈ {missing, invalid, expired, refused_write}.

  Add audit events 1201 (context rejected: reason, account ID), 1202 (row-level write refused: account ID) and 1203 (startup self-check failed: reason) to `src/Gozleme.Api/Identity/SecurityAudit.cs`
- [x] T011 Create `src/Gozleme.Api/Tenancy/TenantTransaction.cs`, an endpoint filter:
  - begin a transaction on the request's `GozlemeDbContext`;
  - run `SELECT gozleme.establish_context(@token)`, signing for `ClaimTypes.NameIdentifier` only (never a header, claim other than the account ID, or query value). If the result is not `ok`, count it and audit 1201. The request continues, and the RLS rules return nothing;
  - run the handler; commit if `db.Database.CurrentTransaction` is still open, otherwise leave it (the handler rolled back);
  - map `PostgresException` 42501 (direct or inner `DbUpdateException`) to an empty 404 with `no-store`, the counter `refused_write` and audit 1202;
  - record the cleared event with the outcome.

  Let Npgsql connection failures propagate to `UseDatabaseUnavailableResponses` (GOZ-0001-IF-17)
- [x] T012 Apply `TenantTransaction` to the `/organizations` and `/invitations` groups in `src/Gozleme.Api/Organizations/OrganizationEndpoints.cs`. Make `RemoveMemberAsync` and `RedeemAsync` use the ambient transaction instead of `BeginTransactionAsync`, keeping their lock order and re-reads. In redeem:
  - set `gozleme.invitation_capability` (`set_config(..., @hex, true)`) before the lookup;
  - replace the unguarded organization lookup and the `FOR KEY SHARE` statement with `SELECT gozleme.lock_invited_organization(@hash)`;
  - keep `FOR UPDATE` on the invitation.
- [x] T013 Add a startup self-check in `src/Gozleme.Api/GozlemeApp.cs` (hosted service or post-build step): sign a probe token for `Guid.Empty`, call `establish_context` in a rolled-back transaction, and on a result other than `ok` log audit 1203 with the reason and increment the counter. Do not block startup, and treat a database outage as a non-event (logged by existing paths)

**Checkpoint**: The migration applies, the API starts, and every existing integration test passes with RLS forced (regression gate before story work).

---

## Phase 3: User Story 1 - Two tenants stay isolated in the running product (Priority: P1) 🎯 MVP

**Goal**: Two seeded tenants with disjoint members; cross-tenant pages and APIs answer "not found"; GOZ-0002 flows unchanged.

**Independent Test**: Seed both demo organizations, sign in as each member, and confirm own-only visibility and identical 404s. Re-run the GOZ-0002 suites.

### Tests for User Story 1

- [x] T014 [P] [US1] In `tests/Gozleme.Api.IntegrationTests/OrganizationIsolationTests.cs`, add a two-tenant API parity case under RLS. For every organization-scoped endpoint, a non-member's response for the other tenant is byte-identical (status, headers that matter, body) to the response for an unknown ID
- [x] T015 [P] [US1] Run the full GOZ-0002 integration suites (`OrganizationTests`, `InvitationTests`, `MembershipTests`, `OrganizationBoundaryTests`) unchanged against the RLS database. Add a concurrency regression to `InvitationTests.cs`/`MembershipTests.cs`: concurrent redemption and member removal in one organization complete without deadlock (lock order, GOZ-0002-IF-20)

### Implementation for User Story 1

- [x] T016 [US1] Fix any GOZ-0002 path broken by RLS that T015 exposes (expected: creation RETURNING and first membership, redemption capability, listing). Fix it in policies or helpers, never by weakening a test. Record each fix in `progress.yaml` attempts

**Checkpoint**: US1 works through the API; the UI is unchanged.

---

## Phase 4: User Story 2 - The database refuses cross-tenant rows even if the application check is skipped (Priority: P1)

**Goal**: Direct app-role and owner-login access is filtered by the policies.

**Independent Test**: The table × command × context matrix via `TenantQueryAsync`.

- [x] T017 [P] [US2] Create `tests/Gozleme.Api.IntegrationTests/RlsIsolationTests.cs`. It uses its own `GozlemeDatabase`, with two organizations and disjoint members seeded as the superuser. Through `AppConnectionString`, for each of `organizations`, `memberships` and `invitations`, test SELECT, INSERT, UPDATE and DELETE:
  - with no context: 0 rows, writes rejected with 42501 or 0 rows affected;
  - with a valid context for A: only A's rows, plus A's own membership rows;
  - with an explicit filter for B: 0 rows;
  - writes into B, and moving A's rows to B: rejected.

  Ground truth is compared through the superuser
- [x] T018 [P] [US2] In the same file: the owner login (`OwnerLoginConnectionString`) without context sees 0 rows; the app role cannot `SELECT` from `gozleme_private.context_keys`, cannot `CREATE OR REPLACE` a helper, and cannot `ALTER TABLE … DISABLE/NO FORCE ROW LEVEL SECURITY`
- [x] T019 [P] [US2] Cascades under RLS (GOZ-0002-IF-12): deleting a user (as the app role, which has no RLS on `users`) cascades its memberships and nulls `created_by`/`used_by`; deleting an organization as the superuser cascades. The unique `code_hash` index is still used: `EXPLAIN` for the capability lookup shows an index scan

**Checkpoint**: Invariant 1 is demonstrated at the database layer.

---

## Phase 5: User Story 3 - A caller-set or stale context cannot widen access (Priority: P1)

**Goal**: Prove the stated boundary.

**Independent Test**: The forged-variant matrix and pooled-connection reuse.

- [x] T020 [P] [US3] Create `tests/Gozleme.Api.IntegrationTests/TenantContextBoundaryTests.cs`. Through the app role:
  - a plain `set_config('gozleme.account_context', '<other uuid>')` gives 0 rows;
  - a token with its account swapped (MAC unchanged) gives 0 rows and `establish_context` returns `invalid`;
  - an expired token gives 0 rows and `expired`;
  - a token dated more than 10 minutes in the future gives 0 rows;
  - a token signed with the wrong key gives 0 rows;
  - a valid context for X plus a stray `set_config('gozleme.tenant_id', B)` or `('gozleme.organization_id', B)` gives no B rows;
  - a direct call to `gozleme.lock_invited_organization` with a random hash returns NULL;
  - `gozleme.invitation_capability` set to another organization's real hash (obtained as the superuser) shows exactly that one invitation and nothing else of the organization. This documents the bearer-capability semantics
- [x] T021 [P] [US3] Stale-connection test (single-connection pool `Maximum Pool Size=1`): run a transaction with A's context, then on the reused connection check that `current_setting('gozleme.account_context', true)` is empty or NULL and tenant reads give 0 rows. Through the API with the same pool size, sign in as B after A and see only B's data. Session lookup and sign-in still work (GOZ-0001-IF-03)
- [x] T022 [P] [US3] Removed member with a still-valid token: sign a token for X, remove X's membership as the superuser, and get 0 rows for that organization on the next statement in the same transaction and in a new one. Through the API, the next request gets 404 (spec AS 3.6)
- [x] T023 [P] [US3] Through the API, an `x-gozleme-organization`/`x-gozleme-account` header or a forged cookie claim does not change whose data is returned (FR-008)

**Checkpoint**: The boundary is proven by automated tests.

---

## Phase 6: User Story 4 - Every table is classified, and new tenant tables cannot ship unprotected (Priority: P2)

**Goal**: Visibility scaffolding: classification plus catalog guard.

**Independent Test**: A clean pass on the migrated database, and failure on each seeded violation.

- [x] T024 [US4] Create `src/Gozleme.Api/Persistence/TableClassification.cs` (`TenantOwned`, `GlobalIdentity`, `SharedPublic` = empty, `DefinerHelpers` = the seven pinned names minus `rls_internal`) and `src/Gozleme.Api/Persistence/RlsCatalog.cs`. `ViolationsAsync(NpgsqlConnection)` returns human-readable violations per research R11
- [x] T025 [P] [US4] Create `tests/Gozleme.Api.IntegrationTests/RlsCatalogTests.cs`. The migrated database has no violations. Negative cases, as the migrator inside a transaction that is rolled back, each produce the named violation:
  - an unclassified table;
  - a table in `TenantOwned` with RLS off (simulated by temporarily classifying a scratch table);
  - FORCE missing;
  - a table placed in `SharedPublic`;
  - an extra `SECURITY DEFINER` function;
  - `EXECUTE` granted to `PUBLIC`.
- [x] T026 [US4] Narrow the GOZ-0000 assertions that assumed no RLS: in `tests/Gozleme.Api.IntegrationTests/MigrationAndRoleTests.cs`, keep the role facts and add the `gozleme_private` no-usage check and the migrator NOBYPASSRLS fact. Update `BootstrapBoundaryTests.cs` if it pins the table or function inventory
- [x] T027 [P] [US4] Document the future-table rule (contract §5) in `docs/engineering/local-development.md` (a "Tenant-owned tables" section) and link it from the `GozlemeDbContext.cs` comment on tenant-owned entities

**Checkpoint**: The guard is in CI via `make test`.

---

## Phase 7: User Story 5 - Operators can see tenant context and denials without seeing tenant data (Priority: P2)

**Goal**: Spans, counter and audit with no secrets.

**Independent Test**: Captured telemetry assertions plus a no-secret search.

- [x] T028 [P] [US5] Create `tests/Gozleme.Api.IntegrationTests/TenantTelemetryTests.cs`:
  - an organization request yields `gozleme.tenant.transaction` with the established event (`ok`) and the cleared event (`commit`), and the `gozleme.org.*` span is its child. A request whose token fails (wrong key factory) yields `invalid` and the counter `{reason=invalid}`;
  - a refused write yields 404 and the counter `refused_write`. The test factory adds an `IStartupFilter` that maps a test-only route group with the `TenantTransaction` filter, whose handler inserts into another tenant. No test seam is added to production code;
  - no span attribute, event, log line or metric label contains `v1.`, the key, a code, a code hash (hex), an email or a display name;
  - `gozleme.organization.id` appears only on `gozleme.org.*` spans after membership is confirmed.
- [x] T029 [P] [US5] Audit: 1201 and 1202 are emitted under `Gozleme.Security.Audit` with an opaque account ID only. 1203 is emitted when the factory starts with a key that differs from the provisioned one. Extend `IdentityTelemetryTests`-style assertions in the same file
- [x] T030 [US5] Implement any telemetry gaps T028–T029 reveal in `TenantTelemetry.cs`/`TenantTransaction.cs`

**Checkpoint**: The observability requirements FR-026–FR-029 hold.

---

## Phase 8: User Story 6 - Operator and no-request paths keep working under explicit contexts (Priority: P3)

**Goal**: `seed` acts as a seeded member; the second tenant exists.

**Independent Test**: Seed twice, fresh and after mutation.

- [x] T031 [US6] Update `src/Gozleme.Api/Identity/OperatorCommands.cs`:
  - add `dave@demo.gozleme.test` to `DemoAccounts`;
  - add `PartnerOrganizationId = 0193a000-0000-7000-8000-000000000003` and `PartnerOrganizationName = "Seeded Partner Org"`;
  - rewrite the organization reset per research R10. In one transaction per organization, establish the signed context of the first seeded member who is currently a member (or the creator when the organization is new). Create it through the creator path, then restore the name, delete invitations, delete non-seeded memberships and add missing seeded members;
  - if the organization exists with no seeded member, fail with exit code 1 and the fixed message.

  `reset-password` and `unlock` stay without tenant context. `seed` reads the key through `AddGozlemeTenancy`
- [x] T032 [P] [US6] Seed tests in `tests/Gozleme.Api.IntegrationTests/TenantSeedTests.cs` (own database, since they break the fixed demo organizations; originally planned for `OperatorCommandTests.cs`):
  - a fresh run creates both organizations with exact members and no invitations;
  - a run after mutation restores them (rename, extra member, open invitations, a seeded member removed);
  - the no-seeded-member state fails with exit code 1;
  - the run is idempotent;
  - `reset-password` and `unlock` still work;
  - the seed never gains rows outside the organizations its member can see (assert through a third, unrelated organization that stays untouched).
- [x] T033 [P] [US6] Update `e2e/tests/evidence.ts`: add `DEMO.dave`, `PARTNER_ORG` (ID and name) and the `specDirs` entry for `goz-0003`

**Checkpoint**: The demo fixtures are ready.

---

## Phase 9: Cumulative demo (US1, US2, US3, US5 evidence)

- [x] T034 Add the Playwright project `goz-0003` (depends on `goz-0002`, `testMatch: /tenant-rls-demo\.spec\.ts/`) to `e2e/playwright.config.ts`
- [x] T035 Create `e2e/tests/tenant-rls-demo.spec.ts`. With fresh sign-ins of alice and dave, and screenshots in `evidence/screenshots/`:
  - (01) alice's organization list shows Seeded Demo Org and not Seeded Partner Org;
  - (02) dave sees only Seeded Partner Org;
  - (03) alice opening the Partner Org URL gets "Organization not found";
  - (04) dave opening the Demo Org URL gets "Organization not found";
  - the API (`request.get`) for the other tenant's organization, members and invitations returns 404 with a body equal to the unknown-ID response;
  - axe checks on each page;
  - (05) Jaeger: `recentSpans('gozleme-api', …, 'gozleme.tenant.transaction')` finds spans with the established and cleared events; screenshot of the trace;
  - the metrics scrape is polled up to 90 s for `gozleme_tenant_denials` after a forged-context API probe, if one can be triggered from outside. Otherwise it is recorded from the database evidence step;
  - the no-secret scan of captured telemetry includes the dev key and the `v1.` token prefix.
- [x] T036 In the same spec, add a database evidence step. Use `compose('exec', '-T', 'postgres', 'psql', '-U', 'gozleme_app', …)` and the owner login to run:
  - no context;
  - a forged plain setting;
  - a tampered token;
  - an expired token, all signed in Node with the dev key;
  - a valid token for dave;
  - an owner login without context.

  Write `docs/specs/003-tenant-rls/evidence/rls-evidence.json` with counts and the `establish_context` results only (no names, emails or tokens), and assert the expected values
- [ ] T037 Run `make demo SPEC=GOZ-0003` on a fresh Compose project. Confirm GOZ-0000…0002 replay green and the evidence scan passes. Record `evidence/demo-run.json` and update `progress.yaml` (demo stage, commit, result). Investigate failures with a maximum of three meaningful repair attempts per distinct failure

---

## Phase 10: Polish & Cross-Cutting Concerns

- [x] T038 [P] `deploy/helm/gozleme/README.md`: replace "do not cut a release before then" in the Organizations bullet with the RLS statement (research R13), and add an upgrade note (add `rls-context-key` before upgrading; `pgcrypto` required). Run `make helm-lint`
- [x] T039 [P] In `docs/engineering/local-development.md`, document `GOZLEME_CONTEXT_KEY`, key rotation, the non-guarantees (contract §4) and that existing volumes need no reset
- [x] T040 [P] Update the doc comments on `OrganizationAccess.cs` and `OrganizationEndpoints.cs` ("RLS is the second layer since GOZ-0003"), and on `GozlemeDbContext.cs` (tenant-owned tables now RLS-forced)
- [ ] T041 Run `make lint`, `make test`, `make validate`, `make helm-lint` and `gitleaks git` (as in GOZ-0002). Record counts in `progress.yaml` gates
- [ ] T042 Run the quickstart validation (`docs/specs/003-tenant-rls/quickstart.md`) end to end, including the manual spot check and the key-rotation steps on the local stack

---

## Dependencies & Execution Order

- **Setup (T001–T004)**: no dependencies; T002–T004 run in parallel.
- **Foundational (T005–T013)**: depends on Setup. T005 → T006; T007 → T008 → T009; T010 → T011 → T012 → T013. It blocks all stories.
- **US1 (T014–T016)**: after Foundational. This is the MVP and closes the GOZ-0002-IF-01 exception in the product.
- **US2 (T017–T019) and US3 (T020–T023)**: after Foundational; they run in parallel with US1 (separate test files).
- **US4 (T024–T027)**: after T007; independent of the other stories.
- **US5 (T028–T030)**: after T011.
- **US6 (T031–T033)**: after T011 (it needs the signer and helpers).
- **Demo (T034–T037)**: after US1, US5 and US6.
- **Polish (T038–T042)**: last.

## Parallel Example: after Foundational

```text
T017 RlsIsolationTests.cs        T020 TenantContextBoundaryTests.cs
T025 RlsCatalogTests.cs          T028 TenantTelemetryTests.cs
T032 OperatorCommandTests (seed) T033 e2e evidence.ts
```

## Implementation Strategy

1. **MVP**: Setup, Foundational, then US1. The product is isolated by both layers, and the GOZ-0002 flows pass.
2. **Proof**: US2 and US3 provide the evidence for invariant 1 and the stated boundary.
3. **Guard**: US4 protects every future table.
4. **Operability**: US5 and US6, then the demo and polish.
