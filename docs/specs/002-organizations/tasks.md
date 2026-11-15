---

description: "Task list for GOZ-0002 Organizations and membership"
---

# Tasks: Organizations and Membership

**Input**: Design documents from `docs/specs/002-organizations/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R14), data-model.md, contracts/organizations-api.openapi.yaml, contracts/web-routes.md, quickstart.md

**Tests**: Required. The spec and prompt demand positive and negative tests, boundary tests, telemetry no-secret checks and a cumulative browser demo (FR-025–FR-027, Constitution IV/VI).

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)

## Path Conventions

API `src/Gozleme.Api/`, API tests `tests/Gozleme.Api.IntegrationTests/` and `tests/Gozleme.Api.Tests/`, web `web/src/`, demo `e2e/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Module skeleton and configuration

- [X] T001 Create `src/Gozleme.Api/Organizations/OrganizationOptions.cs`: section `Gozleme:Organizations`, `InvitationExpiryHours` default 168 `[Range(1, 720)]`, `RedeemPerMinute` default 10 `[Range(1, 100)]`. ~~Add the defaults to `src/Gozleme.Api/appsettings.json`~~ (implementation note: defaults stay in code like `AuthOptions`; documented in `docs/engineering/local-development.md`)
- [X] T002 Create `src/Gozleme.Api/Organizations/OrganizationServiceCollectionExtensions.cs` with `AddGozlemeOrganizations(this WebApplicationBuilder)`: binds and validates options on start, and registers the `invitation-redeem` fixed-window rate-limit policy partitioned by the authenticated account ID (`ClaimTypes.NameIdentifier`), using `RedeemPerMinute` and `QueueLimit = 0` (research R7). Call it from `src/Gozleme.Api/GozlemeApp.cs` after `AddGozlemeIdentity()`
- [X] T003 Generalize the rate-limit rejection handler in `src/Gozleme.Api/Identity/IdentityServiceCollectionExtensions.cs` so that the telemetry operation label comes from endpoint metadata (a small `RateLimitOperation` metadata record) instead of the path guess, falling back to the current behaviour. Keep GOZ-0001 responses unchanged

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Entities, migration, access check, telemetry and audit used by every story

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T004 [P] Create entities in `src/Gozleme.Api/Organizations/Organization.cs`, `Membership.cs` and `Invitation.cs` per data-model.md:
  - `organizations`: `id` uuid PK, `display_name` `varchar(100)` not null, `created_at`, `created_by` uuid null → `users(id)` `ON DELETE SET NULL`, `updated_at`.
  - `memberships`: PK `(organization_id, user_id)`, `user_id` → `users(id)` `ON DELETE CASCADE`, `organization_id` → `organizations(id)` `ON DELETE CASCADE`, `joined_at`, `joined_via` `varchar(16)` (`creator` | `invitation`), `invitation_id` null `SET NULL`, `selected_at` null.
  - `invitations`: `id` uuid PK, `organization_id` `CASCADE`, `code_hash` `bytea` 32 bytes unique, `created_by`/`used_by`/`revoked_by` null `SET NULL`, `created_at`, `expires_at`, `used_at`, `revoked_at`.
  - **No role, owner or admin column**
- [X] T005 Register the entities in `src/Gozleme.Api/Persistence/GozlemeDbContext.cs`:
  - `DbSet`s and table names `organizations`, `memberships`, `invitations`.
  - Indexes `memberships(user_id, selected_at DESC)` and `invitations(organization_id, created_at)`, plus a unique index on `invitations(code_hash)`.
  - Check constraints `joined_via IN ('creator','invitation')`, `NOT (used_at IS NOT NULL AND revoked_at IS NOT NULL)` and `octet_length(code_hash) = 32`.
  - `created_at` and `joined_at` default to `now()`.
- [X] T006 Generate the additive migration `src/Gozleme.Api/Persistence/Migrations/20261116000000_Organizations.cs` (+ Designer, updated `GozlemeDbContextModelSnapshot.cs`), with a doc comment noting that it is additive, that default privileges give `gozleme_app` DML, that there is no RLS yet (GOZ-0003), and that tenant key columns are present
- [X] T007 [P] Create `src/Gozleme.Api/Organizations/OrganizationTelemetry.cs`:
  - Spans `gozleme.org.<operation>` for the operations `create`, `rename`, `select`, `invite_create`, `invite_revoke`, `invite_redeem`, `member_remove` and `member_leave`.
  - Tags `gozleme.org.outcome`, `gozleme.org.failure_category` (`none`, `validation`, `not_found`, `invalid_invitation`, `last_member`, `rate_limited`) and `gozleme.organization.id` (set only after membership is confirmed).
  - Counter `gozleme.org.operations` with labels operation, outcome and category only.
- [X] T008 [P] Add audit events 1101–1108 to `src/Gozleme.Api/Identity/SecurityAudit.cs`: `gozleme.audit.org_created`, `org_renamed`, `org_invitation_created`, `org_invitation_revoked`, `org_invitation_redeemed`, `org_invitation_refused` (category only), `org_member_removed` (actor, target, organization) and `org_member_left`. Each carries opaque UUIDs only
- [X] T009 [P] Create `src/Gozleme.Api/Organizations/InvitationCodes.cs` (research R5):
  - `Generate()`: 20 bytes from `RandomNumberGenerator`, encoded as Crockford base32 (32 chars), shown in dash-separated groups of 4.
  - `Normalize(string?)`: uppercase; strip dashes and whitespace; map O→0 and I/L→1; null if not exactly 32 valid chars or the input is longer than 64 chars.
  - `Hash(normalized)`: SHA-256, 32 bytes.
- [X] T010 [P] Create `src/Gozleme.Api/Organizations/DisplayNames.cs`: `Validate(string?)` trims the name, requires 1–100 chars and rejects any `char.IsControl`, returning the trimmed value or a field error ("Enter a name of 1–100 characters." / "The name can't contain control characters.")
- [X] T011 Create `src/Gozleme.Api/Organizations/OrganizationAccess.cs`: `FindMembershipAsync(db, string? organizationId, ClaimsPrincipal, ct)` parses the GUID and loads the membership for `(organizationId, NameIdentifier)`. A null result means not found (malformed, nonexistent or not a member). Add a helper that returns an empty `404` (research R3)
- [X] T012 [P] Create `src/Gozleme.Api/Organizations/OrganizationContracts.cs` with records matching contracts/organizations-api.openapi.yaml: `DisplayNameRequest`, `OrganizationResponse`, `OrganizationListResponse`, `MemberResponse`/`MembersResponse`, `InvitationResponse`/`InvitationsResponse`, `CreatedInvitationResponse`, `RedeemRequest`, `RedeemedResponse`, and `OrganizationError` codes (`validation_failed`, `invalid_invitation`, `last_member`)
- [X] T013 [P] Unit tests `tests/Gozleme.Api.Tests/InvitationCodeTests.cs` (format, entropy length, normalization of case, dashes, whitespace and aliases, rejecting bad chars and lengths, stable hash) and `tests/Gozleme.Api.Tests/DisplayNameTests.cs` (bounds, trimming, control characters, markup accepted as text)
- [X] T014 Create `tests/Gozleme.Api.IntegrationTests/Infrastructure/OrgClient.cs` helpers over `AuthClient`: `CreateAsync`, `ListAsync`, `InviteAsync` (returns the code), `RedeemAsync`, `RemoveAsync`, `SelectAsync` and `RenameAsync`

**Checkpoint**: Foundation ready. The migration applies, and unit tests pass.

---

## Phase 3: User Story 1 - Create organizations and switch between them (Priority: P1) 🎯 MVP

**Goal**: A signed-in account creates two organizations, sees only its own, switches the active context and renames.

**Independent Test**: Create two organizations as A, list and switch them, and check `activeOrganizationId`. B sees neither organization, and B's requests for them get the identical 404.

### Tests for User Story 1

- [X] T015 [P] [US1] `tests/Gozleme.Api.IntegrationTests/OrganizationTests.cs`:
  - create returns 201 with a UUIDv7 ID, the creator is the sole member (`joined_via = creator`), and the new organization becomes active;
  - the list holds exactly the caller's organizations, ordered by displayName then ID;
  - select switches `activeOrganizationId`;
  - renaming keeps the ID;
  - duplicate names are allowed;
  - empty, whitespace-only, 101-char and control-character names give 400 `validation_failed` with field errors and no change;
  - anonymous requests get 401 on every route.
- [X] T016 [P] [US1] `tests/Gozleme.Api.IntegrationTests/OrganizationIsolationTests.cs` (parity part): for GET, PATCH, select, members, invitations GET/POST, invitation DELETE and member DELETE, a non-member, a random UUID and `not-a-uuid` get byte-identical empty 404s with `no-store`. A non-member's rename has no effect

### Implementation for User Story 1

- [X] T017 [US1] Create `src/Gozleme.Api/Organizations/OrganizationEndpoints.cs` with `MapOrganizationEndpoints(this RouteGroupBuilder api)` and a `no-store` filter:
  - `GET /organizations` returns the list plus the active organization (research R4: max `selected_at`, else max `joined_at`, else null).
  - `POST /organizations` inserts the organization and the creator membership with `selected_at = now` in one transaction.
  - `GET /organizations/{organizationId}`.
  - `PATCH /organizations/{organizationId}` renames and sets `updated_at`.
  - `POST /organizations/{organizationId}/select` sets `selected_at = now` and returns 204.
  - Every write records spans, metrics and audit events (T007/T008). Body size limit 4 KiB.
  - Map it from `src/Gozleme.Api/GozlemeApp.cs` on the `api` group.
- [X] T018 [P] [US1] Web data helpers in `web/src/lib/organizations.ts`: server-side `listOrganizations()`, `getOrganization(id)`, `listMembers(id)` and `listInvitations(id)` forwarding the session via `apiHeaders`. Each returns a typed `{state: "ok"|"not_found"|"anonymous"|"unavailable"}` and encodes path segments with `encodeURIComponent`. Add tests in `web/src/lib/organizations.test.ts`
- [X] T019 [P] [US1] Route handlers `web/src/app/api/organizations/route.ts` (GET, POST), `web/src/app/api/organizations/[organizationId]/route.ts` (GET, PATCH) and `web/src/app/api/organizations/[organizationId]/select/route.ts` (POST). Every non-GET handler calls `isSameOrigin()` before `forwardToApi()`. Add tests in `web/src/app/api/organizations/routes.test.ts` (cross-site 403, forwarding, path encoding)
- [X] T020 [P] [US1] Extend `traced()` flows in `web/src/telemetry/ui.ts` with `org_create`, `org_rename`, `org_select`, `invite_create`, `invite_revoke`, `invite_redeem`, `member_remove` and `member_leave`, and update `web/src/telemetry/ui.test.ts`
- [X] T021 [US1] Components `web/src/components/CreateOrganizationForm.tsx` (accessible field error, 100 max), `RenameOrganizationForm.tsx` and `OrganizationSwitcher.tsx` (labelled select of the caller's organizations, posts select, navigates to `/organizations/{id}`, shows the active one), and `SignedInHeader.tsx` (switcher + links "Organizations" and "Join" + `AccountBar`). Add tests in `web/src/components/Organizations.test.tsx`
- [X] T022 [US1] Pages `web/src/app/organizations/page.tsx` (list with the active one marked, create form, empty state offering Create and "Join with a code") and `web/src/app/organizations/[organizationId]/page.tsx` (name and rename; a not-found page with HTTP 404 via `notFound()` for non-members). Each is gated with `getSession()` and redirects to `/login?returnTo=…`, and shows the degraded view on `unavailable`. Add `<title>`s matching `… — Gözleme`
- [X] T023 [US1] Add `SignedInHeader` to the dashboard in `web/src/app/page.tsx` (status dashboard unchanged otherwise)

**Checkpoint**: US1 is fully functional and testable on its own.

---

## Phase 4: User Story 2 - Invite someone with a single-use code (Priority: P1)

**Goal**: Members create, list and revoke invitations. A signed-in account redeems a code once.

**Independent Test**: A invites, B redeems and joins, C's reuse is refused, a revoked code is refused, an expired code is refused (using a fake time provider), and an existing member's redemption doesn't consume the code.

### Tests for User Story 2

- [X] T024 [P] [US2] `tests/Gozleme.Api.IntegrationTests/InvitationTests.cs`:
  - create returns 201 with a 39-char grouped code, and the list never contains `code`;
  - redemption by a non-member returns 200 (`alreadyMember` false) and the organization is listed;
  - reuse, revoked, expired (time advanced through `TimeProvider` override) and random or malformed codes all give a byte-identical 400 `invalid_invitation`;
  - an existing member's redemption returns 200 (`alreadyMember` true) and the code still works for another account;
  - lower case, dashes and spaces are accepted;
  - two concurrent redemptions give exactly one 200;
  - redemption without a session gives 401;
  - the 11th redemption within a minute gives 429 with `Retry-After`;
  - the DB row stores only a 32-byte hash, and the code never appears in any column.

### Implementation for User Story 2

- [X] T025 [US2] Add to `src/Gozleme.Api/Organizations/OrganizationEndpoints.cs`:
  - `GET /organizations/{organizationId}/invitations` lists open invitations only.
  - `POST /organizations/{organizationId}/invitations` creates one with expiry from options, returning the code once.
  - `DELETE /organizations/{organizationId}/invitations/{invitationId}` revokes an open invitation of that organization, and answers 404 otherwise.
  - `POST /invitations/redeem` (rate-limit policy `invitation-redeem`, 4 KiB body) runs one transaction: `SELECT … FOR UPDATE` by hash, check usable, check existing membership (no consumption), insert the `invitation` membership with `selected_at = now`, set `used_at`/`used_by`. A `DbUpdateException` on the unique key counts as already a member.
  - All of these record telemetry and audit events (refusals carry the category only).
- [X] T026 [P] [US2] Route handlers `web/src/app/api/organizations/[organizationId]/invitations/route.ts` (GET, POST), `web/src/app/api/organizations/[organizationId]/invitations/[invitationId]/route.ts` (DELETE) and `web/src/app/api/invitations/redeem/route.ts` (POST), with same-origin checks on non-GET. Extend `web/src/app/api/organizations/routes.test.ts`
- [X] T027 [US2] Components `web/src/components/InvitationPanel.tsx` (create; show the code once in a copyable, labelled read-only field with a "shown once" notice; list open invitations with expiry; revoke) and `web/src/components/RedeemInvitationForm.tsx` (generic error "This invitation is invalid or has expired.", 429 text, navigates to the organization on success). Extend `web/src/components/Organizations.test.tsx`
- [X] T028 [US2] Page `web/src/app/join/page.tsx` (gated, `returnTo=%2Fjoin`), and add `InvitationPanel` to `web/src/app/organizations/[organizationId]/page.tsx`

**Checkpoint**: US1 and US2 work independently.

---

## Phase 5: User Story 3 - See and manage the member list (Priority: P2)

**Goal**: Members list members, remove any other member, or leave. The last member is protected. Removal takes effect on the next request.

**Independent Test**: A removes B. B's already-open session gets 404 on its next request, B is still signed in, and B's other organizations still work. The last member's leave or removal gives 409. Concurrent mutual removal leaves at least one member.

### Tests for User Story 3

- [X] T029 [P] [US3] `tests/Gozleme.Api.IntegrationTests/MembershipTests.cs`:
  - the member list shows `userId`, `email`, `joinedAt`, `joinedVia` and `isSelf` only;
  - removing another member returns 204, the removed session's next request gets 404, `GET /auth/session` is still 200, and its other organization still gets 200;
  - leaving moves `activeOrganizationId` to another organization or null;
  - removing the last member (self) gives 409 `last_member`, and the membership remains;
  - with two members removing each other concurrently, exactly one succeeds and one member remains;
  - a removed member can rejoin with a new code;
  - removing an unknown or malformed `userId`, or a user in another organization, gives the identical 404;
  - the security stamp is unchanged after removal.

### Implementation for User Story 3

- [X] T030 [US3] Add to `src/Gozleme.Api/Organizations/OrganizationEndpoints.cs`:
  - `GET /organizations/{organizationId}/members` returns the members joined to `users` for email, ordered by `joined_at`.
  - `DELETE /organizations/{organizationId}/members/{userId}` runs one transaction: `SELECT … FROM organizations WHERE id = @id FOR UPDATE`, count the members, return 409 `last_member` if the count is 1, otherwise delete. It records the operation as `member_leave` when the target is the caller and `member_remove` otherwise, with telemetry and audit.
- [X] T031 [P] [US3] Route handlers `web/src/app/api/organizations/[organizationId]/members/route.ts` (GET) and `web/src/app/api/organizations/[organizationId]/members/[userId]/route.ts` (DELETE, same-origin). Extend `routes.test.ts`
- [X] T032 [US3] Component `web/src/components/MemberList.tsx` (a table of email and joined date; "Remove" for others and "Leave" for self, each with a confirm step; the 409 message "An organization must keep at least one member."; after leaving, navigates to `/organizations`). Add it to `web/src/app/organizations/[organizationId]/page.tsx`, and extend the component tests

**Checkpoint**: US1–US3 work.

---

## Phase 6: User Story 4 - Context is a preference, never authority (Priority: P2)

**Goal**: Tampered, stale or removed context never grants access. Cross-site writes are refused. The empty state works. The outage fails closed.

**Independent Test**: B selects or acts on X while not a member and gets 404 every time. A removed member's stored selection falls back. Writes without the web header get 403. With the database stopped, an organization endpoint gives 503.

### Tests for User Story 4

- [X] T033 [P] [US4] Extend `tests/Gozleme.Api.IntegrationTests/OrganizationIsolationTests.cs`:
  - a non-member's select of X gives 404, and its `activeOrganizationId` never equals X;
  - after removal, the removed member's `activeOrganizationId` falls back;
  - an account with no organizations gets an empty list and `activeOrganizationId` null;
  - every unsafe organization route without `x-gozleme-client: web` gives 403, and nothing changes.
- [X] T034 [P] [US4] Extend `tests/Gozleme.Api.IntegrationTests/DatabaseOutageTests.cs` so that `GET /api/v1/organizations` and `POST /api/v1/invitations/redeem` answer 503 `{"error":"unavailable"}` with the database stopped
- [X] T035 [US4] Verify `web/src/app/organizations/page.tsx`, the `[organizationId]` page and `/join` render the degraded view (no organization data) on `unavailable`, and the empty state on zero organizations. Add component or page tests as needed in `web/src/components/Organizations.test.tsx`

**Checkpoint**: Isolation claims are covered by negative tests.

---

## Phase 7: User Story 5 - Cumulative demo and the no-delegation boundary (Priority: P3)

**Goal**: Boundary tests, narrowed earlier assertions, seed fixtures, telemetry checks and the cumulative Playwright demo.

**Independent Test**: `make demo` on a fresh stack passes goz-0000 → goz-0001 → goz-0002 and saves screenshots and `demo-run.json`.

### Tests for User Story 5

- [X] T036 [P] [US5] `tests/Gozleme.Api.IntegrationTests/OrganizationBoundaryTests.cs` (research R8):
  - `information_schema.columns` for `organizations`, `memberships` and `invitations` holds no column matching `role|owner|admin|permission`;
  - `PATCH`/`PUT /organizations/{id}/members/{userId}`, `…/members/{userId}/role`, `…/transfer` and `…/admins` give 404/405;
  - create and redeem with an extra `role: "admin"` field succeed, but the member list is unchanged in shape;
  - a removed creator can no longer access the organization, and the remaining member can still rename it;
  - `/api/v1/users` still gives 404.
- [X] T037 [P] [US5] `tests/Gozleme.Api.IntegrationTests/OrganizationTelemetryTests.cs` (pattern `IdentityTelemetryTests.cs`):
  - every `gozleme.org.*` span exists with a bounded category;
  - the `gozleme.org.operations` counter has only operation, outcome and category labels;
  - no span tag, event, metric label or captured log contains the email, display name or invitation code used;
  - audit events 1101–1108 carry UUIDs only.
- [X] T038 [US5] Narrow `tests/Gozleme.Api.IntegrationTests/BootstrapBoundaryTests.cs`: remove only the `GET /api/v1/organizations` case and keep `/api/v1/users`. Rename the table test to `Gozleme_schema_holds_exactly_the_identity_and_organization_tables` and expect `auth_sessions,data_protection_keys,invitations,memberships,organizations,user_claims,user_logins,user_tokens,users`. Update the class doc comment (GOZ-0001-IF-10). Confirm `EndpointAuthorizationTests.cs` still passes with the unchanged anonymous list

### Implementation for User Story 5

- [X] T039 [US5] Extend `seed` in `src/Gozleme.Api/Identity/OperatorCommands.cs`: add `carol@demo.gozleme.test` to `DemoAccounts`, then idempotently upsert the organization with fixed ID `0193a000-0000-7000-8000-000000000002` and display name "Seeded Demo Org". Reset its members to exactly alice (`creator`) and carol (`invitation`), and delete its invitations. Print opaque IDs only. Extend `tests/Gozleme.Api.IntegrationTests/OperatorCommandTests.cs` (run twice, same state)
- [X] T040 [US5] Narrow `e2e/tests/bootstrap-demo.spec.ts` boundary test: `/organizations` now exists and is protected, so catalog and publish stay absent, and the dashboard links are exactly "Organizations" and "Join" (plus account controls). Update `e2e/tests/evidence.ts`: `specDirs` gets `"goz-0002": "002-organizations"`, and `DEMO` gets `carol`
- [X] T041 [US5] Add a Playwright project `goz-0002` (testMatch `organizations-demo.spec.ts`, depends on `goz-0001`) in `e2e/playwright.config.ts`, and check that `scripts/demo/run-demo.sh` picks it up as the latest spec and routes earlier suites' screenshots to `regression-goz-NNNN/`
- [X] T042 [US5] Write `e2e/tests/organizations-demo.spec.ts` (serial, axe audits, screenshots to `shotsDir("goz-0002")`):
  1. anonymous `/organizations` and `/join` redirect to login, and the API gives 401;
  2. alice signs in afresh, sees Seeded Demo Org, and creates "Demo Org Alpha <run>" and "Demo Org Beta <run>";
  3. switches with the switcher, and active is shown in the header and survives a reload;
  4. renames Beta;
  5. invites on Alpha, the code is shown once, and the reload list has no code;
  6. bob redeems in a second context and lands on Alpha;
  7. carol's reuse shows the generic error;
  8. a revoked code is refused;
  9. alice removes bob, bob's open page gets "Organization not found" on navigation, and bob is still signed in;
  10. last-member leave is refused on a fresh single-member organization;
  11. bob's API requests for Alpha's ID, a random UUID and `not-a-uuid` are byte-identical 404s, and a cross-site POST gets 403;
  12. boundary: no role, admin or transfer control in the DOM, and the delegation routes give 404/405;
  13. observability: one trace browser → web → API → PostgreSQL containing `gozleme.org.create`/`select`/`invite_redeem`/`member_remove`, with no email, display name or code in Jaeger spans or Loki logs.

  Screenshots use synthetic data only.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [X] T043 [P] Update `docs/engineering/local-development.md` (organizations walkthrough, seeded fixtures, configuration keys) and `README.md` feature list if it enumerates features
- [X] T044 [P] (Resolved as not needed: Compose and Helm wire only the registration and cookie switches for `Gozleme:Auth`, not its numeric bounds, so organization settings follow suit and are overridable through `Gozleme__Organizations__*`.) Add `Gozleme__Organizations__*` defaults to `.env.example`/`compose.yaml` only if overrides are wired there for other options, and to the Helm `deploy/helm/gozleme/values.yaml` config map in the same way as `Gozleme:Auth`
- [ ] T045 Run `make lint`, `make test`, `make validate`, `helm lint` (via `make helm-lint`) and gitleaks, and fix findings without weakening checks
- [ ] T046 Run `make demo` (cumulative) via `validate-demo`. Record `docs/specs/002-organizations/evidence/demo-run.json` and screenshots, and update `progress.yaml` evidence

---

## Dependencies & Execution Order

- **Setup (T001–T003)** → **Foundational (T004–T014)** → user stories.
- **US1 (T015–T023)** is the MVP and must come before US2–US4 at runtime, because invitations, members and context all need organizations.
- **US2 (T024–T028)** depends on US1 endpoints. **US3 (T029–T032)** depends on US1. It also uses US2 redemption to get a second member in tests (tests may insert memberships through `OrgClient` invitation helpers).
- **US4 (T033–T035)** depends on US1–US3 endpoints.
- **US5 (T036–T042)** depends on all of them. T038–T040 can start once T006 exists.
- **Polish (T043–T046)** comes last. T046 is the demo gate.

### Parallel Opportunities

- Foundational: T004, T007, T008, T009, T010, T012 and T013 touch different files.
- US1: T015, T016, T018, T019 and T020 can run in parallel. T017 comes before T021–T023 in runtime terms.
- US2: T024 and T026 can run in parallel with T025.
- US5: T036 and T037 can run in parallel. T039 and T040 touch different files.

## Parallel Example: User Story 1

```text
T015 OrganizationTests.cs      T016 OrganizationIsolationTests.cs
T018 web/src/lib/organizations.ts   T019 web/src/app/api/organizations/**   T020 web/src/telemetry/ui.ts
```

## Implementation Strategy

1. Setup and Foundational: the migration applies and the unit tests are green.
2. US1, then a checkpoint: create, list, switch and rename work end to end in the UI (MVP).
3. US2: invitations. US3: members. US4: isolation hardening. Each one's tests must be green before moving on.
4. US5: boundary, seed and cumulative demo. Then polish, gates and the demo evidence.
