# Research: Organizations and Membership (GOZ-0002)

Decisions that resolve the plan's technical unknowns. Each one cites the spec requirement or
prompt finding that drives it.

## R1 — Module placement and persistence

- **Decision**: Add a new `src/Gozleme.Api/Organizations/` module next to `Identity/`. Its entities are added to `GozlemeDbContext`, which already applies snake_case naming. Its endpoints use the DbContext directly (via `IDbContextFactory`/scoped context), like `AuthEndpoints`. One additive EF migration, `20261116000000_Organizations`. `gozleme_app` gets DML on the new tables automatically through the Foundation default privileges (GOZ-0000-IF-03).
- **Rationale**: Follows the modular-monolith baseline and the existing Identity module. A repository or service layer would be a speculative abstraction (CR-13).
- **Alternatives**: A separate DbContext was rejected because there would then be two migration histories for one schema. Raw SQL was rejected because it duplicates the EF conventions that GOZ-0003 builds on.

## R2 — Organization identifier

- **Decision**: `uuid`, generated as UUIDv7 (`Guid.CreateVersion7`), the same as accounts. It is shown in URLs (`/organizations/{id}`) and is the only organization reference in telemetry and audit events.
- **Rationale**: Consistent with GOZ-0001-IF-03/IF-18. UUIDv7 keeps index locality, and its 74 random bits make it infeasible to guess. Membership is checked on every request anyway, so guessing an ID alone gains nothing (FR-006).
- **Alternatives**: UUIDv4 was rejected: it is slightly more random, but it differs from accounts, and authorization, not secrecy, protects the organization. Slugs or handles are deferred to GOZ-0005 by maintainer decision.

## R3 — Non-member, nonexistent and malformed identifiers look identical

- **Decision**: Routes take `{organizationId}` as a string with no route constraint. One helper, `OrganizationAccess.FindMembershipAsync`, parses the ID and looks up the membership row for `(organizationId, currentUserId)`. On any miss (unparseable, no such organization, not a member) it returns `404` with an empty body and `cache-control: no-store`. The same applies to member and invitation sub-resource IDs.
- **Rationale**: FR-006 and SC-003. A `{id:guid}` route constraint would give malformed IDs a routing 404 that skips the group's filters, which could make its headers differ.
- **Alternatives**: `403` for non-members was rejected because it reveals that the organization exists.

## R4 — Active context: a preference stored on membership rows

- **Decision**: `memberships.selected_at` (nullable `timestamptz`). Switching sets it to `now()` on the caller's membership in that organization. The active organization is the caller's membership with the greatest `selected_at`. If none has ever been selected, it is the most recently joined membership, and with no memberships it is "none". `GET /api/v1/organizations` returns `activeOrganizationId` computed by this rule. Organization-scoped endpoints always take the organization ID explicitly in the path and re-check membership. They never read the "active" value as authority.
- **Rationale**: FR-005 and GOZ-0001-IF-04. Nothing is stored in session claims. Removing a membership deletes its row, so the fallback is automatic and correct (US4-1, US3-3). A tampered client selection can only name an ID, and that ID is checked like any other.
- **Alternatives**: A session claim was rejected because IF-04 forbids it. A web-tier cookie was rejected: it is per browser, the API cannot see it, and it is tamperable (it would still be re-checked, but it adds a second source of truth). A `users.active_organization_id` column was rejected: it has no tenant key and needs cleanup when membership is removed.

## R5 — Invitation codes

- **Decision**: 160 random bits from `RandomNumberGenerator`, encoded as 32 Crockford base32 characters and shown in groups of 4 with dashes (`ABCD-EFGH-…`). On input, codes are normalized: uppercase, dashes and whitespace removed, Crockford aliases mapped (O→0, I/L→1). The database stores `code_hash = SHA-256(normalized code)` (32 bytes) with a unique index. The plaintext code is returned only in the creation response. Default expiry is 7 days (`Gozleme:Organizations:InvitationExpiryHours`, range 1–720).
- **Rationale**: FR-007 and FR-008. With high entropy, a fast hash is enough: there is no low-entropy secret to protect against offline guessing, unlike passwords. The unique hash index makes lookup a single indexed query. Grouped, case-insensitive codes are easy to copy by hand.
- **Alternatives**: PBKDF2 or Argon2 were rejected as unnecessary for 160-bit random secrets, and they would need a scan or a separate lookup ID. Data Protection-sealed tokens were rejected: they are long, and they cannot be revoked without a database row anyway.

## R6 — Atomic redemption, and the last-member guard under concurrency

- **Decision**: Each mutating operation runs in one transaction. *Redeem*: `SELECT … FROM invitations WHERE code_hash = @h FOR UPDATE`. Then check that the invitation is usable (not used, not revoked, `expires_at > now()`). Then check existing membership: if the caller is already a member, return `200 {alreadyMember: true}` without consuming the code. Otherwise insert the membership and set `used_at`/`used_by`. *Remove/leave*: lock the organization row with `SELECT … FROM organizations WHERE id = @id FOR UPDATE`, count the members, refuse with `409 last_member` when the count is 1, otherwise delete the membership. All membership changes in one organization are serialized by that row lock. A unique index on `(organization_id, user_id)` is the backstop against duplicate joins.
- **Rationale**: These are the spec's concurrency edge cases (two redeemers, mutual removal). Default Read Committed isolation plus explicit row locks is enough and deterministic.
- **Alternatives**: Serializable isolation with retries was rejected as more complex, and its outcome order is nondeterministic for tests.

## R7 — Generic redemption failure and rate limiting

- **Decision**: Every unusable code answers `400 {"error":"invalid_invitation"}` with an identical body, whether it is unknown, used, revoked, expired or malformed. A separate rate-limit policy, `invitation-redeem`, uses a fixed window partitioned by the authenticated account ID: 10 per minute (`Gozleme:Organizations:RedeemPerMinute`, range 1–100), answered with `429` and `Retry-After`. The rejection handler is generalized so that the operation label comes from the endpoint's metadata instead of the path guess in `IdentityServiceCollectionExtensions`.
- **Rationale**: FR-010 and FR-011. 10 guesses per minute against a 160-bit space is not a feasible attack. Partitioning by account rather than client address works because redemption requires a session.
- **Alternatives**: Locking out after N failures was rejected because a confused user could block themselves for no security gain at this entropy.

## R8 — Interim role-neutral authority and the boundary test

- **Decision**: Any membership row grants every organization action in this slice: rename, invite, revoke, list members, remove any member, leave. There is no role column, flag or enum, and request DTOs bind only named fields, so unknown fields (`role`, `isAdmin`, `owner`) are ignored. The boundary test asserts four things. No role, owner or admin column exists in the new tables. No route exists for member roles, ownership transfer or admin grants: `PUT/PATCH /members/{id}`, `/members/{id}/role`, `/transfer`, `/admins` all return `404/405`. Sending `role: "admin"` on create or redeem has no effect. The creator, once removed, is an ordinary non-member.
- **Rationale**: FR-015, FR-018 and FR-024. These are the maintainer's member-power and boundary decisions. GOZ-0004 introduces roles and will narrow these powers.
- **Alternatives**: A hidden "owner" flag kept for later was rejected: it would be speculative and would contradict the boundary.

## R9 — Removal takes effect on the next request without a sign-out

- **Decision**: No session state is touched. Because every organization endpoint re-checks membership from the database (R3), the removed account's next request returns 404. Its other organizations are unaffected. The security stamp is not rotated.
- **Rationale**: FR-017 and GOZ-0001-IF-04. Rotating the stamp would sign the account out everywhere.

## R10 — Web tier, pages and switcher

- **Decision**:
  - **Pages**: `/organizations` (list, create, empty state with "Create" and "Join with a code"), `/organizations/[organizationId]` (name and rename, members, invitations), and `/join` (redeem). Each page is gated server-side with `getSession()`, so anonymous visitors are redirected to `/login?returnTo=…`.
  - **Switcher**: an `OrganizationSwitcher` in a shared signed-in header (with `AccountBar`). It lists the caller's organizations and posts `select`, then navigates to the organization's page. The dashboard `/` gains a header with links to Organizations and Join. This supersedes the GOZ-0000 "zero links" assertion (IF-10).
  - **Route handlers**: new handlers under `web/src/app/api/organizations/…` and `web/src/app/api/invitations/redeem`. Every non-GET handler calls `isSameOrigin()` before `forwardToApi()` (GOZ-0001-IF-01). `forwardToApi` gains no new behaviour: PATCH and DELETE are forwarded as they are.
  - **Browser telemetry**: `traced()` gets new bounded flows: `org_create`, `org_rename`, `org_select`, `invite_create`, `invite_revoke`, `invite_redeem`, `member_remove` and `member_leave`.
- **Rationale**: Matches the GOZ-0001 web patterns and needs no CORS. There is no `proxy.ts`, so each page gates itself, as IF-02 requires.

## R11 — Telemetry and audit

- **Decision**:
  - **`OrganizationTelemetry`** mirrors `AuthTelemetry`. Spans are `gozleme.org.<operation>` for `create`, `rename`, `select`, `invite_create`, `invite_revoke`, `invite_redeem`, `member_remove` and `member_leave`. Each span carries `gozleme.org.outcome` and `gozleme.org.failure_category`, and `gozleme.organization.id` once the caller is known to be a member (for redemption: once the code is valid, naming the organization being joined; GOZ-0002-IF-17). Failure categories are bounded: `none`, `validation`, `not_found`, `invalid_invitation`, `last_member` and `rate_limited`. GOZ-0003 adds `refused_write`, for a write refused by row-level security (amended 2026-11-16, `bugfix/goz-0003-org-span-refused-write`).
  - **Counter**: `gozleme.org.operations{operation,outcome,failure_category}`. It never has an organization or account label, so cardinality stays bounded.
  - **Audit**: `SecurityAudit` gains events 1101–1108 (`gozleme.audit.org_*`) that carry actor, target account, organization and invitation only as opaque UUIDs.
  - **Never recorded**: display names, emails and codes never appear in spans, metrics or logs. SQL is parameterized, so `db.query.text` carries placeholders only. Codes travel in request bodies, never in URLs.
- **Rationale**: FR-023, FR-026, invariant 8, GOZ-0000-IF-11 and GOZ-0001-IF-11. An organization ID on a span is an opaque reference that the prompt allows. Account IDs stay out of spans, as in GOZ-0001.

## R12 — Seed and demo

- **Decision**:
  - **Seed**: the idempotent `seed` command adds the account `carol@demo.gozleme.test` and one fixed-ID organization, "Seeded Demo Org", with members alice and carol. Each run restores exactly that membership and removes any invitations left by earlier demo runs.
  - **Demo suite**: a new Playwright project `goz-0002` (depends on `goz-0001`) runs `organizations-demo.spec.ts`, and `specDirs` gains `"goz-0002": "002-organizations"`. alice and bob sign in afresh (GOZ-0001-IF-09). In the suite, alice creates two organizations with run-unique synthetic names and switches between them. She invites bob, bob redeems the code, alice removes bob, and bob's open page loses access. The suite also runs the negative, boundary and telemetry checks. Screenshots are saved to `evidence/screenshots/`, and earlier suites write theirs to `regression-goz-0000/` and `regression-goz-0001/`.
- **Rationale**: FR-027 and GOZ-0001-IF-09. carol gives the seeded organization a second member that no earlier suite changes.

## R13 — Superseded boundary assertions

- **Decision**: In `BootstrapBoundaryTests`, drop only `GET /api/v1/organizations` from the not-found theory, keep `/api/v1/users` (FR-024) and the rest, and rename the table-inventory test to cover the identity and organization tables, adding `invitations`, `memberships` and `organizations`. In `e2e/tests/bootstrap-demo.spec.ts`, narrow the boundary test: `/organizations` now exists, so the assertion moves to catalog and publish only, and the zero-links check becomes "links only to Organizations and Join". `EndpointAuthorizationTests` keeps the same anonymous list: there are no new anonymous endpoints.
- **Rationale**: FR-025, GOZ-0000-IF-01 and GOZ-0001-IF-10.

## R14 — Database outage

- **Decision**: No endpoint code catches Npgsql errors. `UseDatabaseUnavailableResponses` already maps them to `503 {"error":"unavailable"}` (GOZ-0001-IF-17), and an integration test covers one organizations endpoint with the database stopped. Pages show the existing honest degraded view when `getSession()` returns `unavailable`.
- **Rationale**: FR-022.
