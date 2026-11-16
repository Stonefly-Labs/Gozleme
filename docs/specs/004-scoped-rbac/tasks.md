---

description: "Task list for GOZ-0004 Scoped RBAC permission foundation"
---

# Tasks: Scoped RBAC Permission Foundation

**Input**: Design documents from `docs/specs/004-scoped-rbac/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R13), data-model.md, contracts/permissions.md, contracts/rbac-api.openapi.yaml, quickstart.md

**Tests**: Required. The spec and prompt demand positive and negative tests, database-layer escalation tests as both logins, a boundary test for "full policy evaluator comes later", telemetry no-secret checks and a cumulative browser demo (FR-016, FR-022; Constitution IV/VI).

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)

## Path Conventions

API `src/Gozleme.Api/`, API tests `tests/Gozleme.Api.IntegrationTests/` and `tests/Gozleme.Api.Tests/`, web `web/src/`, demo `e2e/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Vocabulary shared by every story

- [X] T001 Create `src/Gozleme.Api/Organizations/OrganizationPermissions.cs`:
  - `OrganizationRole` constants `admin`, `approver`, `member`, `publisher`, `reviewer`, and `All` (sorted).
  - `OrgAction` constants (`organization.read`, `organization.select`, `organization.rename`, `membership.list`, `membership.leave`, `membership.remove`, `membership.roles.change`, `invitation.list`, `invitation.create`, `invitation.revoke`), each mapped to a resource type (`organization` | `membership` | `invitation`).
  - The role table from `contracts/permissions.md` (`publisher`/`reviewer`/`approver` equal to `member`; no role implies another).
  - `OrganizationPermissions.For(IEnumerable<string> roles) → IReadOnlySet<string>` (union of the roles' actions; unknown roles grant nothing).
  - `OrganizationPermissions.RenderTable()`, producing the fixed-width text table used by the contract.
  - `OrgActionAttribute(params string[] actions)` endpoint metadata.
- [X] T002 [P] Unit tests in `tests/Gozleme.Api.Tests/OrganizationPermissionsTests.cs`: role table per role, union semantics, unknown role grants nothing, admin does not imply publisher/reviewer/approver, every action has a resource type

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Schema, model and the decision point that every story uses

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T003 Write migration `src/Gozleme.Api/Persistence/Migrations/20261118000000_ScopedRbac.cs` (with Designer and updated `GozlemeDbContextModelSnapshot.cs`), exactly as research R1–R5 and data-model.md:
  1. `ADD COLUMN roles text[] NOT NULL DEFAULT '{admin,member}'`, then `SET DEFAULT '{member}'`.
  2. Constraint `ck_memberships_roles`: "subset of `{admin, approver, member, publisher, reviewer}`, contains `member`, no NULL element".
  3. Non-definer `gozleme.account_is_admin(uuid)` and `gozleme.lock_organization_members(uuid)` (`pg_advisory_xact_lock(hashtextextended('gozleme.memberships:' || org::text, 0))`), both `SET search_path = pg_catalog, pg_temp`, with `REVOKE EXECUTE … FROM PUBLIC` and `GRANT EXECUTE … TO gozleme_app`.
  4. Altered policies `organizations_update`, `invitations_insert`, `invitations_update` (member branch → admin; code-holder branch unchanged), `invitations_delete`, `memberships_insert` (creator branch `roles <@ '{admin,member}'`; invitation branch `roles = '{member}'`) and `memberships_update` (rows of the caller's organizations).
  5. `gozleme.memberships_guard()` in the order of research R4: cascade pass `rls_internal() AND pg_trigger_depth() > 1`; normalize; immutable columns; early return; lock, then authorize; last admin. It raises `insufficient_privilege` and has `PUBLIC` revoked. Trigger: `BEFORE INSERT OR UPDATE OR DELETE … FOR EACH ROW`.

  `Down` restores the GOZ-0003 policy text verbatim and drops the trigger, the functions, the constraint and the column. Start from the verified scratch SQL (`rbac.sql`, `guard2.sql` in the session scratchpad).
- [X] T004 Add `public string[] Roles { get; set; } = [OrganizationRole.Member];` to `src/Gozleme.Api/Organizations/Membership.cs`, and map it in `src/Gozleme.Api/Persistence/GozlemeDbContext.cs` (`roles`, `text[]`, `HasDefaultValueSql("'{member}'::text[]")`). In `OrganizationEndpoints.cs`, `CreateAsync` writes `["admin","member"]` and `RedeemAsync` writes `["member"]`
- [X] T005 Create `src/Gozleme.Api/Organizations/AuthorizationTelemetry.cs`: span `gozleme.authz.decision` with `gozleme.authz.action`, `gozleme.authz.resource_type`, `gozleme.authz.outcome` (`allow` | `deny`); counter `gozleme.authz.decisions` with the same three labels. Never roles, account ids, names, emails or codes
- [X] T006 Add audit events to `src/Gozleme.Api/Identity/SecurityAudit.cs`:
  - **1301** `AuthorizationDenied(organizationId, accountId, action)`.
  - **1302** `MemberRolesChanged(organizationId, targetId, actorId, string before, string after)`, where the role lists are comma-joined and sorted.

  Opaque ids only, category `Gozleme.Security.Audit`. Document the range 1301–1310 in the class comment
- [X] T007 Add `OrganizationAccess.AuthorizeAsync(GozlemeDbContext db, string? organizationId, ClaimsPrincipal user, string action, HttpContext http, CancellationToken ct)` in `src/Gozleme.Api/Organizations/OrganizationAccess.cs`. It returns `(Membership? membership, bool allowed)`:
  1. Run the existing membership read first; null means 404 with no span.
  2. Then `OrganizationPermissions.For(membership.Roles).Contains(action)`.
  3. Record the span and metric.
  4. On deny, defer audit 1301 with `TenantTransaction.AfterCommit`.

  Add the helper `Forbidden(Activity?, string operation)` in `OrganizationEndpoints.cs`. It answers `403 {"error":"forbidden"}` and records `OrgFailure.Forbidden`.
- [X] T008 [P] Extend `src/Gozleme.Api/Organizations/OrganizationTelemetry.cs` with `OrgOperation.MemberRoles = "member_roles"` and `OrgFailure.Forbidden = "forbidden"`, `OrgFailure.LastAdmin = "last_admin"`. Extend `OrganizationContracts.cs` with:
  - `OrganizationError.Forbidden` and `OrganizationError.LastAdmin`;
  - `MemberResponse.Roles`;
  - `OrganizationDetailResponse(Id, DisplayName, CreatedAt, Roles, Permissions)`;
  - `RolesRequest(string[]? Roles)`.
- [X] T009 Update pins: `LATEST_MIGRATION` → `/_ScopedRbac$/` in `e2e/tests/bootstrap-demo.spec.ts`; column pin and forbidden-word scan in `tests/Gozleme.Api.IntegrationTests/OrganizationBoundaryTests.cs` (allow `roles` on `memberships` only; still forbid `owner|admin|permission|delegat` as column names elsewhere); confirm `RlsCatalogTests` and `BootstrapBoundaryTests` stay green (no new table, no definer, `PUBLIC` revoked)
- [X] T010 Fixtures: give `tests/Gozleme.Api.IntegrationTests/Infrastructure/TwoTenants.cs` explicit roles (alice admin in A, carol member in A, dave admin in B). Add `SetRolesAsync(orgId, userId, roles)` to `Infrastructure/OrgClient.cs`

**Checkpoint**: The migration applies, the build is green, and the existing suites run. Some GOZ-0002 assertions are expected to fail until US1/US2 narrow them (R12).

---

## Phase 3: User Story 1 — Assign a role and watch a privileged operation go from denied to permitted (Priority: P1) 🎯 MVP

**Goal**: A member is refused a privileged operation, an admin grants `admin`, the member's next request succeeds, and revocation refuses again, with no sign-out.

**Independent Test**: Seeded alice (admin) and carol (member) in Seeded Demo Org; quickstart steps 2–6.

### Tests for User Story 1

- [X] T011 [P] [US1] `tests/Gozleme.Api.IntegrationTests/RoleAssignmentTests.cs`:
  - a member's rename is 403 and unchanged;
  - an admin grants carol `admin` (200, audit 1302);
  - carol's next rename is 200 on the same cookie;
  - the grant is revoked and the next rename is 403, with the session still valid (`GET /auth/session` 200);
  - a non-member's role change, and changes to an unknown, malformed or other-organization target, give an identical empty 404;
  - an unknown role gives 400 `validation_failed` with `fields.roles`;
  - a set without `member` gets `member` added;
  - assigning the same set changes nothing and writes no second 1302.
- [X] T012 [P] [US1] Revocation on demotion in `RoleAssignmentTests.cs`: carol made admin creates an invitation; alice removes carol's admin; the invitation is revoked (audit 1104) and its code is refused at redeem with the generic `invalid_invitation`. Also prove that a non-admin holds no open invitation (research R5 invariant) after the demotion
- [X] T013 [P] [US1] Web tests, `web/src/components/Organizations.test.tsx`:
  - members see role badges;
  - an admin sees the per-member role editor (four checkboxes, `member` fixed, no combobox);
  - a member sees no editor, rename form, invitation panel or Remove button for others;
  - a 403 shows "You no longer have permission to do that." and refreshes.

  In `web/src/app/api/organizations/routes.test.ts`: the new `PUT …/roles` route rejects a cross-origin request before forwarding.

### Implementation for User Story 1

- [X] T014 [US1] Move every organization-scoped handler in `src/Gozleme.Api/Organizations/OrganizationEndpoints.cs` to `AuthorizeAsync` with its action (contracts/permissions.md "Endpoint → action"). Handlers: Get, Select, Rename, Members, Invitations, CreateInvitation, RevokeInvitation and RemoveMember (`membership.leave` when the target is the caller, else `membership.remove`). Add `.WithMetadata(new OrgActionAttribute(...))` to each mapping. `GetAsync` returns `OrganizationDetailResponse` with the caller's sorted roles and `OrganizationPermissions.For(roles)`. `MembersAsync` includes each member's sorted roles. Update the class comment (interim role-neutral rule removed)
- [X] T015 [US1] Add `PUT /{organizationId}/members/{userId}/roles` (`SetRolesAsync`, `OrgOperation.MemberRoles`, body limit, `OrgActionAttribute(OrgAction.MembershipRolesChange)`) in `OrganizationEndpoints.cs`:
  1. Authorize.
  2. Validate (`member` added; unknown role → 400).
  3. `SELECT gozleme.lock_organization_members(@org)`, then the organization row `FOR UPDATE`.
  4. Re-read the caller's membership and re-check `admin` after the locks.
  5. Load the target (404 if absent or malformed).
  6. Return early on an unchanged set.
  7. Last-admin check (409 `last_admin`).
  8. If the target loses `admin`, revoke its open invitations `FOR UPDATE` (as `RemoveMemberAsync` does, `revoked_by` = actor, audit 1104 each).
  9. Save, then audit 1302 after commit, and return the member.
- [X] T016 [US1] Web types and route: add `roles` and `permissions` to the types in `web/src/lib/organizations.ts`. Create `web/src/app/api/organizations/[organizationId]/members/[userId]/roles/route.ts`: `PUT`, `isSameOrigin()` then `forwardToApi()`, path built with `segment()`
- [X] T017 [US1] Web UI:
  - `web/src/components/MemberList.tsx`: role badges for everyone; admins get a per-member "Edit roles" form with checkboxes for `admin`, `publisher`, `reviewer` and `approver` (`member` shown fixed) and a Save button, with messages for 403 and for 409 `last_admin` ("An organization needs at least one admin.").
  - `web/src/app/organizations/[organizationId]/page.tsx`: render `RenameOrganizationForm`, `InvitationPanel` and the Remove buttons only when `permissions` includes their action; Leave stays for everyone.
  - `web/src/components/RenameOrganizationForm.tsx`, `InvitationPanel.tsx`: handle 403 with the permission message and `router.refresh()`.
- [X] T018 [US1] Update `tests/Gozleme.Api.IntegrationTests/MembershipTests.cs`, `InvitationTests.cs` and `OrganizationTests.cs`. Where an invitee performed a now admin-only action, either grant `admin` first or assert the new 403. Keep `Removing_or_leaving_revokes_the_departing_accounts_open_invitations` (the departing account must be an admin to have created invitations). Rewrite `OrganizationBoundaryTests.Extra_privilege_fields_are_ignored_and_the_creator_has_no_special_standing` as "the creator holds admin through its role; extra privilege fields are ignored"
- [X] T019 [US1] Extend `seed` in `src/Gozleme.Api/Identity/OperatorCommands.cs` (research R11). It acts as the first seeded member holding `admin` (alice, else carol; neither → "run make reset") and never with owner rights. Every run restores Demo Org alice `{admin,member}` and carol `{member}`, and Partner Org dave `{admin,member}`. A fresh creation inserts the creator with `{admin,member}` and the others with `{member}`. Update `tests/Gozleme.Api.IntegrationTests/TenantSeedTests.cs` for the roles and idempotency, including recovery when alice was demoted and carol is admin

**Checkpoint**: User Story 1 works through the API and the UI.

---

## Phase 4: User Story 2 — Every organization operation is governed by one published permission contract (Priority: P1)

**Goal**: The role table and the endpoint → action map are published and pinned, and the full role × action matrix behaves as published.

**Independent Test**: `OrganizationPermissionContractTests` and the matrix test.

- [X] T020 [P] [US2] `tests/Gozleme.Api.IntegrationTests/OrganizationPermissionContractTests.cs`:
  - enumerate `EndpointDataSource`; every route under `/api/v1/organizations/{organizationId}` has `OrgActionAttribute` with known actions;
  - every action has a role-table entry;
  - `OrganizationPermissions.RenderTable()` equals the fenced block in `docs/specs/004-scoped-rbac/contracts/permissions.md`, read relative to the repository root.
  - Negative case: a test-only endpoint mapped without the attribute is reported.
- [X] T021 [P] [US2] Matrix test in `RoleAssignmentTests.cs`. For each single-role set `{member}`, `{member,publisher}`, `{member,reviewer}`, `{member,approver}` and `{admin,member}`, call every action endpoint as carol and compare allow/deny with `OrganizationPermissions` (100%, SC-002), restoring state between cases
- [X] T022 [P] [US2] Policy-evaluator boundary (FR-016, FR-017), with `tests/Gozleme.Api.IntegrationTests/OrganizationBoundaryTests.cs`:
  - `Delegation_routes_do_not_exist` keeps `/transfer`, `/owner`, `/admins`, `PATCH /members/{user}` and `DELETE` organization at 404/405, and replaces `PUT /members/{user}/role` with an assertion that only `/roles` exists;
  - new `No_policy_or_custom_role_surface_exists`: `/policies`, `/roles`, `/permissions`, `/grants` and `POST /members/{user}/roles` give 404/405; a role name outside the five gives 400; the database has no `policy`, `grant` or `role` table;
  - `AuthorizeAsync`'s inputs are `(roles, action)` only (reflection on the signature);
  - the endpoint-authorization pin in `EndpointAuthorizationTests.cs` includes the new endpoint (session required, not anonymous).
- [X] T023 [US2] Telemetry and audit tests in `tests/Gozleme.Api.IntegrationTests/RbacTelemetryTests.cs` (pattern `OrganizationTelemetryTests.cs`):
  - an allow and a deny produce `gozleme.authz.decision` with the three attributes;
  - `gozleme.authz.decisions` labels are only action, resource type and outcome;
  - `gozleme.org.operations` records `forbidden` and `last_admin`;
  - audit 1301 and 1302 carry opaque ids only;
  - no email, display name, code, `v1.` context or account id appears in captured spans, metric labels or logs.

**Checkpoint**: The contract is published, pinned and matched by behaviour.

---

## Phase 5: User Story 3 — An organization can never lose its last admin (Priority: P1)

**Goal**: The last-admin rule holds in the application and the database, under concurrency.

**Independent Test**: Single-admin self-demotion and leaving refused; concurrent mutual demotion leaves one admin.

- [X] T024 [P] [US3] Tests in `RoleAssignmentTests.cs`:
  - a sole admin's self-demotion → 409 `last_admin`; a sole admin leaving with other members → 409 `last_admin`; the sole member leaving → 409 `last_member` (unchanged);
  - an admin demoting self while another admin exists → 200, with effect on the next request;
  - concurrency: two admins demote each other in parallel (20 iterations) and at least one admin remains every time, with no 500.
- [X] T025 [US3] Apply the per-organization lock and the last-admin rule in `RemoveMemberAsync` (`OrganizationEndpoints.cs`):
  1. `lock_organization_members` first.
  2. The organization row lock only when the caller holds `admin`; a non-admin's `FOR UPDATE` would match nothing under the admin-only policy (R5).
  3. Re-read after the locks.
  4. `last_member` when the target is the sole member, else `last_admin` when the target is the only admin.
  5. Keep the revocation of the departing account's invitations.

  Map `DbUpdateConcurrencyException` paths so that none reaches 500 for these cases.

**Checkpoint**: The last-admin rule holds.

---

## Phase 6: User Story 4 — Existing organizations keep working after the upgrade (Priority: P2)

**Goal**: Option B: existing members keep all rights, new invitees get `member`, and creators get `admin`.

**Independent Test**: Migrate a database holding GOZ-0003-era memberships and check the roles.

- [X] T026 [P] [US4] `tests/Gozleme.Api.IntegrationTests/RbacMigrationTests.cs`:
  - migrate to `20261117000000_TenantRls`, insert organizations and memberships as a superuser, then migrate to latest; every pre-existing membership reads `{admin,member}`, and each member can still rename, invite and remove (SC-005);
  - a new invitee gets `{member}` and a new creator gets `{admin,member}`;
  - the migration's SQL contains no `UPDATE`, `INSERT` or `DELETE` on tenant tables and does not use `rls_helper` or `NO FORCE`;
  - `Down` then `Up` round-trips.
- [X] T027 [US4] Document the upgrade in `deploy/helm/gozleme/README.md` (pre-upgrade migration is DDL-only; every existing member becomes admin; tidy roles in the UI) and in the API section of `README.md` or `docs/` where GOZ-0003 documented the RLS posture

**Checkpoint**: The upgrade is safe and additive.

---

## Phase 7: User Story 5 — The database refuses role escalation even if the application is bypassed (Priority: P2)

**Goal**: Q3 = B: admin-only writes are enforced by RLS and the guard, independently of the application.

**Independent Test**: `RbacIsolationTests` as `gozleme_app` and the owner login.

- [X] T028 [P] [US5] `tests/Gozleme.Api.IntegrationTests/RbacIsolationTests.cs` (fixtures `TwoTenants`; `GozlemeDatabase.InTransactionAsync` with `Establish(SignContext(...))`). Encode every row of the research R3 table for `gozleme_app`:
  - a member raising its own roles, changing another's roles, removing another member or adding another account → `42501`;
  - a member renaming → 0 rows; a member creating an invitation → refused;
  - a member leaving and updating its own `selected_at` → allowed; updating another's `selected_at` or its own `joined_via`/`joined_at`/`invitation_id` (non-null) → refused;
  - an admin renaming, inviting, adding, removing and changing roles → allowed; an admin moving a membership → refused; cross-tenant attempts → 0 rows;
  - last admin by demotion or deletion → refused; unknown role or a set without `member` → check violation;
  - redemption as `{admin,member}` → refused; creator insert with `approver` → refused.
- [X] T029 [P] [US5] Owner and superuser rows in `RbacIsolationTests.cs`:
  - the owner login without a context changes roles → 0 rows;
  - the owner login with `gozleme.rls_helper = on` changes roles or deletes a membership directly → refused (the guard binds);
  - a superuser without a context changes roles → refused;
  - account deletion as `gozleme_app` still cascades (keep `RlsIsolationTests.Foreign_key_cascades_still_run_under_forced_rls` green);
  - invitation deletion nulls `invitation_id`.
- [X] T030 [P] [US5] Concurrency at the database in `RbacIsolationTests.cs`. Two connections: admin1 demotes admin2 while admin2 demotes admin1. Exactly one commits, the other gets `42501`, and an admin remains. Also cover the time-of-check case: admin2's concurrent action after its own demotion is refused (research R4 step 5)

**Checkpoint**: The database layer is independently proven.

---

## Phase 8: Polish & Cross-Cutting Concerns (demo, evidence, docs)

- [X] T031 Add the `goz-0004` Playwright project to `e2e/playwright.config.ts` (depends on `goz-0003`) and the `specDirs` entry `docs/specs/004-scoped-rbac` in `e2e/tests/evidence.ts`
- [X] T032 Narrow the GOZ-0002 e2e boundary in `e2e/tests/organizations-demo.spec.ts` (R12). Allow the role badges and checkboxes. Still forbid `owner`, `transfer` and `administrator` wording and any second combobox. Keep the route checks, except that `PUT /members/{id}/role` → `/roles` is now the only role route. Adjust the steps where bob (an invitee, now `member`) performed admin-only actions, so that the suite's original intent still passes
- [X] T033 Write `e2e/tests/rbac-demo.spec.ts`. Fresh sign-in as alice and as carol on `DEMO_ORG`, then quickstart steps 2–8:
  - carol's page shows no admin controls (screenshot);
  - carol's API rename → 403, name unchanged;
  - alice grants admin in the UI (screenshot);
  - carol renames successfully (screenshot), then restores the name;
  - alice revokes; carol's next rename → 403 and the UI message (screenshot);
  - alice's last-admin self-demotion → 409 and the UI message (screenshot);
  - demotion revokes carol's invitation, and the code is refused;
  - a non-member (dave) → identical 404 for the roles endpoint.

  Restore the seeded roles at the end.
- [X] T034 In `rbac-demo.spec.ts`, record the API role × action matrix and the policy-evaluator boundary routes. Record database evidence as `gozleme_app` through `compose()` with tokens signed in Node: carol raising her own role → 42501, carol renaming → `UPDATE 0`, alice changing roles → 1 row, all rolled back. Write all of it to `docs/specs/004-scoped-rbac/evidence/rbac-evidence.json` (sanitized: opaque ids, no emails or codes)
- [X] T035 In `rbac-demo.spec.ts`, check observability and privacy: `recentSpans('gozleme-api', …, 'gozleme.authz.decision')` shows allow and deny with action and resource type; poll Prometheus up to 90 s for `gozleme_authz_decisions_total`; scan captured spans, logs and screenshots for the seeded emails, display names, codes, `v1.` contexts and the dev key. Also check that audit 1301 and 1302 appear in Loki with opaque ids only
- [X] T036 Run the cumulative demo (`make demo`), with earlier suites writing to `evidence/screenshots/regression-goz-000N/`. Record `docs/specs/004-scoped-rbac/evidence/demo-run.json` via the `validate-demo` skill; all suites must pass
- [X] T037 [P] Update `docs/specs/004-scoped-rbac/progress.yaml`, `.gozleme/spec-registry.yaml` and the generated register (`scripts/generate_register.py`). Run `make lint`, `make test` and `make validate`, and record the counts

---

## Dependencies & Execution Order

- **Setup (T001–T002)** → **Foundational (T003–T010)** → user stories.
- **US1 (T011–T019)** is the MVP and the base for the others. It moves every handler to `AuthorizeAsync` and adds the roles endpoint.
- **US2 (T020–T023)** and **US3 (T024–T025)** depend on US1's handlers and can then run in parallel.
- **US4 (T026–T027)** and **US5 (T028–T030)** depend only on Foundational (the migration) and can run in parallel with US1.
- **Polish (T031–T037)** depends on all stories. T036 (demo run) runs last before T037.

## Parallel Opportunities

- T002 alongside T003–T010.
- T005, T006 and T008 together (different files).
- In US1: T011, T012 and T013 (tests) together; T016 and T017 (web) alongside T015 (API).
- US4 and US5 test suites (T026, T028–T030) alongside US1 implementation.
- T020, T021 and T022 together.

## Implementation Strategy

1. **MVP**: Setup + Foundational + US1. This is the stated outcome: denied → granted → permitted → revoked → denied, in the UI and API.
2. Add US2 (contract pinned, matrix) and US3 (last admin), which complete the P1 stories.
3. Add US4 (upgrade proof) and US5 (database-layer proof).
4. Polish: cumulative demo, evidence and records. Then converge, run the post-convergence impact analysis and the roadmap sync per `drive-speckit`.

**Total**: 37 tasks (Setup 2, Foundational 8, US1 9, US2 4, US3 2, US4 2, US5 3, Polish 7).

## Phase 9: Convergence

- [X] T038 On a 403, `RenameOrganizationForm` and `InvitationPanel` must show the permission message in a page-level notice that survives the refresh, then call `router.refresh()` so the stale controls disappear (`web/src/app/organizations/[organizationId]/page.tsx`, the two components, `Organizations.test.tsx`, and `rbac-demo.spec.ts` step 05). Per spec Edge Cases "role changed while the page is open" and FR-020 (contradicts)
- [X] T039 Add the pure decision function `OrganizationPermissions.Allows(IEnumerable<string> roles, string action, string resourceType)`, make `OrganizationAccess.AuthorizeAsync` decide only through it, and pin its parameter list in `OrganizationBoundaryTests.No_policy_or_custom_role_surface_exists`. Per FR-016 (partial)
- [X] T040 Add `RoleAssignmentTests.Removal_and_role_change_of_the_same_member_serialize`: an admin's removal of carol races another admin's role change of carol (10 rounds). Never a 500; carol is either removed (and the role change is 404) or role-changed and then removed; no membership row survives the removal. Per spec Edge Cases "member removed concurrently with a role change" (partial)
