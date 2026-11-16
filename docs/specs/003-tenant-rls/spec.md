# Feature Specification: Tenant RLS and Visibility Scaffolding

**Feature Branch**: `feature/goz-0003-tenant-rls`

**Created**: 2026-11-15

**Status**: Draft

**Input**: User description: "GOZ-0003 — Tenant RLS and visibility scaffolding. Build Tenant RLS and visibility scaffolding as one bounded runnable tested vertical slice: exercise two tenants through UI/API and demonstrate unauthorized cross-tenant access denied. In scope: transaction-scoped tenant context; RLS, DB privilege boundaries, negative tests. Boundary: never rely on caller-set session variables as trusted identity." (full prompt: `docs/prompts/spec-0003.md`, revision 4, including implementation-alignment bullets GOZ-0000-IF-03/09/10/11, GOZ-0001-IF-01/02/03/04/09/11/17 and GOZ-0002-IF-01/02/05/06/07/08/12/14)

## Clarifications

### Session 2026-11-15

Maintainer decisions taken before drafting (recorded in `progress.yaml`):

- Q: How does the database learn which account is acting in a transaction? → A: Through a short-lived account context that the application signs for each transaction. The database checks the signature with a key that the ordinary application role cannot read. A missing, tampered, expired or replayed-after-expiry context identifies nobody, so the database returns no tenant rows and accepts no tenant writes. Injected SQL can then act only as the account of the request it runs in, never as another account.
- Q: Should the database owner role also be bound by the row-level rules? → A: Yes. Every tenant-owned table enforces its rules on the owner too. Migrations stay schema-only, and any later data change in a migration must establish a context explicitly. Tests cover both the owner role and the application role.
- Q: What context does the operator `seed` command use? → A: It acts as one of the seeded member accounts of each demo organization, so it never has more power than a member. No new database role or privilege is added.
- Q: May the opaque organization identifier appear in traces? → A: Yes, keeping the GOZ-0002 precedent. A span carries the organization identifier only after membership is confirmed. It is never a metric label. Names, emails, codes and the signed context never appear in telemetry.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Two tenants stay isolated in the running product (Priority: P1)

Two organizations exist side by side, each with members who do not belong to the other. A member of the first organization uses the product normally: they see their organization, its members and its open invitations. When they try to reach the second organization by its identifier, the UI shows "Organization not found" and the API answers with the same empty "not found" as for an identifier that does not exist. A member of the second organization has the same experience the other way round. Every organization flow from GOZ-0002 (create, rename, switch, invite, redeem, remove, leave) keeps working, now with the database enforcing the same boundary.

**Why this priority**: This is the stated user-visible outcome. It closes the interim exception to invariant 1 that GOZ-0002 accepted (GOZ-0002-IF-01), and no release may be cut until it lands.

**Independent Test**: Seed the two fixed demo organizations. Sign in as a member of each in turn. Confirm each member sees only their own organization, members and invitations, and that every request for the other organization's pages, members or invitations gets "not found". Re-run the GOZ-0002 demo flows and confirm they still pass.

**Acceptance Scenarios**:

1. **Given** two seeded organizations with disjoint members, **When** a member of the first lists their organizations, **Then** the list contains the first organization and never the second.
2. **Given** a member of the first organization, **When** they open the second organization's page, member list or invitation list by identifier, **Then** the page shows "Organization not found" and the API returns the same empty "not found" response, with no caching, as for an unknown identifier.
3. **Given** a member of the first organization, **When** they try to rename, invite into, remove a member of, or revoke an invitation of the second organization, **Then** the request is refused with "not found" and nothing in the second organization changes.
4. **Given** the cumulative demo, **When** the GOZ-0000, GOZ-0001 and GOZ-0002 suites run against the RLS-enabled database, **Then** they all pass unchanged in behaviour.

---

### User Story 2 - The database refuses cross-tenant rows even if the application check is skipped (Priority: P1)

A security reviewer wants proof that isolation does not depend on the application remembering to check membership. Connected as the ordinary application role, they query each tenant-owned table directly: with no account context, with a valid context for an account that belongs to only one organization, and with a valid context while filtering for the other organization's rows. The database returns only the rows of organizations that the verified account belongs to. Writes into another organization are rejected.

**Why this priority**: Invariant 1 and constitution principle IV require both application authorization and effective row-level security. Without this story, RLS would be an untested claim.

**Independent Test**: Through the ordinary application role, run the same reads and writes against every tenant-owned table under each context variant, and compare the results with the expected visible set. The owner role is tested the same way.

**Acceptance Scenarios**:

1. **Given** no account context in the transaction, **When** the application role reads any tenant-owned table, **Then** it sees zero rows, and tenant writes are rejected.
2. **Given** a valid context for an account in organization A only, **When** it reads organizations, memberships and invitations, **Then** it sees only organization A's rows (plus its own membership rows, which are in A).
3. **Given** the same context, **When** it explicitly filters for organization B's identifier, **Then** it gets zero rows, which is indistinguishable from an identifier that does not exist.
4. **Given** the same context, **When** it tries to insert, update or delete a row belonging to organization B, or to move a row from A to B, **Then** the change is rejected, and organization B's data is unchanged.
5. **Given** the database owner role, **When** it reads tenant-owned tables with no account context, **Then** it also sees zero rows. The owner is bound by the same rules.
6. **Given** a cascade that GOZ-0002 relies on (an organization's memberships and invitations, or an account's memberships), **When** the parent row is removed by a path allowed to remove it, **Then** the cascade still completes.

---

### User Story 3 - A caller-set or stale context cannot widen access (Priority: P1)

The important boundary: the database never trusts a session setting just because someone set it. An attacker who can make the application role set any setting tries to claim another account, another organization, or a context left over from an earlier request on a reused connection. Every such attempt identifies nobody or only the account the request was really made for.

**Why this priority**: This is the stated boundary of the spec ("Never rely on caller-set session variables as trusted identity") and GOZ-0002-IF-02's test requirement that a forged or stale setting cannot read another account's memberships or invitations.

**Independent Test**: Through the application role, set each forged variant directly and run the same reads as in User Story 2. Then use a pooled connection for one account's request, return it to the pool, and use it for another account's request and for an identity lookup.

**Acceptance Scenarios**:

1. **Given** a plain setting naming another account or another organization without a valid signature, **When** the application role reads tenant-owned tables, **Then** it sees zero rows.
2. **Given** a correctly signed context whose signature or content has been altered, **When** it is used, **Then** it identifies nobody, and the attempt is counted as a context rejection.
3. **Given** a correctly signed context that has expired, **When** it is used, **Then** it identifies nobody.
4. **Given** a correctly signed context for account X, **When** the caller adds a separate setting naming organization B that X does not belong to, **Then** no row of B is visible. Membership is re-checked in the database, not taken from the setting.
5. **Given** a pooled connection that served account X's transaction, **When** it is reused for account Y's request, for a session lookup or for sign-in, **Then** none of X's context remains, and identity lookups work without any tenant context.
6. **Given** an account that has just been removed from an organization, **When** its next request presents a still-valid signed context, **Then** it sees none of that organization's rows. The context names an account, not its memberships.
7. **Given** a request that carries a header, cookie or claim naming an organization or account, **When** the application builds the database context, **Then** it ignores that value and uses only the authenticated session's account.

---

### User Story 4 - Every table is classified, and new tenant tables cannot ship unprotected (Priority: P2)

Future specs will add tenant-owned tables. A developer who adds a table without row-level rules, or forgets to classify it, gets a failing automated check before review. The check knows three kinds of table: tenant-owned (must enforce row-level rules on every role, including the owner), global identity infrastructure (an explicit, reviewed allow-list, such as accounts and sessions), and shared or public records, which are reserved. No table may be classified as shared or public until GOZ-0012 defines its separately verified access path.

**Why this priority**: The application role is automatically granted data rights on every new table (GOZ-0000-IF-03). Without a guard, the next spec could silently reopen the gap this spec closes. The classification is the visibility scaffolding that GOZ-0012 builds on (CR-02).

**Independent Test**: Run the catalog check on the migrated database and confirm it passes. In a scratch database, add an unclassified table and a tenant table without rules, and confirm the check fails for each with a clear message.

**Acceptance Scenarios**:

1. **Given** the migrated database, **When** the check runs, **Then** every table in the application schema is classified, and every tenant-owned table enforces row-level rules for all roles, including its owner, and has at least one rule.
2. **Given** a new table that is not in any classification, **When** the check runs, **Then** it fails and names the table.
3. **Given** a table classified as tenant-owned that lacks enforced rules, **When** the check runs, **Then** it fails and names the table.
4. **Given** a table placed in the reserved shared/public category, **When** the check runs, **Then** it fails until a spec defines that category's access path.
5. **Given** the global identity tables from GOZ-0001, **When** the check runs, **Then** they pass as allow-listed non-tenant tables without tenant rules.

---

### User Story 5 - Operators can see tenant context and denials without seeing tenant data (Priority: P2)

An operator looking at traces can see when a request established an account context and when it was cleared, which organization (by opaque identifier) a confirmed member acted in, and how often contexts were rejected or rows were refused. They never see organization names, emails, invitation codes, the signed context or the signing key.

**Why this priority**: OpenTelemetry is foundational for every spec, and this prompt asks for a context span and a denial counter. Telemetry backends are visible to operators across tenants (R3-19), so they must carry no tenant-confidential values.

**Independent Test**: Run the demo with tracing on. Find the context spans for an organization request in the trace viewer. Read the denial counter after a forged-context test. Search all captured telemetry for the synthetic names, emails, codes, signed contexts and the key, and confirm none appear.

**Acceptance Scenarios**:

1. **Given** an organization request, **When** its trace is viewed, **Then** it shows the account context being established and cleared within the request's database work, in the same single trace from browser to database.
2. **Given** a rejected (tampered or expired) context or a refused cross-tenant write, **When** metrics are read, **Then** a denial counter has increased, labelled only by a small fixed set of reasons.
3. **Given** any captured spans, logs and metrics from the demo, **When** they are searched, **Then** they contain no organization names, emails, invitation codes, signed contexts or key material. An organization identifier appears only on spans after membership was confirmed.

---

### User Story 6 - Operator and no-request paths keep working under explicit contexts (Priority: P3)

The operator `seed` command resets the demo data without a signed-in user. It acts as a seeded member of each demo organization and so can do only what a member can do. Other operator commands (`reset-password`, `unlock`) and session resolution touch only global identity tables and need no tenant context.

**Why this priority**: The demo depends on `seed`, and R3-19 requires operator and tenant access to stay separate. It is lower priority because it serves the demo rather than end users.

**Independent Test**: Run `seed` twice on a fresh stack and on a stack where the demo flows have changed the demo organizations. Confirm both demo organizations are restored to their fixed members and no open invitations. Run `reset-password` and `unlock` and confirm they still work.

**Acceptance Scenarios**:

1. **Given** a fresh database, **When** `seed` runs, **Then** both fixed demo organizations exist with their fixed, disjoint members and no invitations.
2. **Given** demo organizations changed by earlier demo runs, **When** `seed` runs again, **Then** it restores names, exact members and no open invitations, acting only through a seeded member's context.
3. **Given** `reset-password`, `unlock` or a session lookup, **When** it runs, **Then** it works without any tenant context.

---

### Edge Cases

- **Signing key missing or too short at startup**: the API (and any process that must sign contexts) refuses to start with a clear configuration error that does not print the key. It never falls back to an unsigned mode.
- **Key mismatch between the application and the database** (for example, the key was rotated in configuration but the migrator has not run): every context is rejected, organization data becomes unreachable, and rejections are counted. Nothing becomes more visible.
- **Key rotation**: rotating the key takes effect after the migrator re-provisions it. In-flight contexts signed with the old key stop working. This is documented for Compose and Helm.
- **Clock skew**: the context lifetime is short but tolerates small skew between application and database. An expired context identifies nobody.
- **Organization creation**: a new organization has no members yet when it is inserted. The creating account may insert an organization it creates and its own first membership in the same transaction, and nothing else.
- **Invitation redemption**: the redeeming account is not yet a member. It may find an open invitation only by presenting the code (the hash of a 160-bit secret). It learns nothing about other invitations, and it may add only its own membership through that invitation.
- **Listing organizations**: the account's own memberships across all its organizations are visible to it, so the organization list works before any organization is chosen.
- **Member list emails**: the member list joins the global accounts table. Emails of members are shown only inside an organization the account belongs to, as today.
- **Removed member mid-request**: membership is re-read in the database by the row-level rules on every statement, so a removal committed by another transaction takes effect on the next statement.
- **Lock order**: row-level rules and their helper checks must not add locks that reverse the organization-then-invitation lock order (GOZ-0002-IF-20), and must not cause deadlocks between removal and redemption.
- **Unique code-hash lookup**: row-level rules must not make the unique code-hash index unusable or let a non-member detect another organization's code by a uniqueness error.
- **Database outage**: behaviour is unchanged. Every endpoint fails closed with the existing "unavailable" response.
- **Owner-run data migrations**: a future migration that changes tenant data must set an explicit context. Otherwise it sees and changes nothing.

## Requirements *(mandatory)*

### Functional Requirements

**Row-level protection of tenant data**

- **FR-001**: The system MUST enable row-level security and enforce it for all roles, including the table owner, on every tenant-owned table: `organizations`, `memberships` and `invitations` (closing GOZ-0002-IF-01).
- **FR-002**: Each tenant-owned table MUST allow a row to be read or changed only when the transaction's verified account is a member of that row's organization, as recorded in the database at the time of the statement.
- **FR-003**: The rules MUST also cover the paths that run before an organization is known (GOZ-0002-IF-02):
  - an account's own membership rows are visible to it across organizations, for the organization list and the membership check;
  - the creating account may insert one new organization that names it as creator, and its own first membership, in one transaction;
  - a redeeming account may find an invitation only by its code hash, and may add only its own membership through an open invitation it found that way.
- **FR-004**: Writes MUST be checked against the same rule as reads, so a row cannot be created in, moved into or changed within an organization the account does not belong to.
- **FR-005**: The rules MUST keep the GOZ-0002 foreign-key cascades working, MUST keep the unique code-hash lookup usable, and MUST preserve the organization-then-invitation lock order on every path, including any helper checks (GOZ-0002-IF-12, IF-20).

**Verified account context (the boundary)**

- **FR-006**: The application MUST establish, at the start of each transaction that touches tenant-owned data, an account context that it signs. The context MUST name only the authenticated session's account and an expiry. It MUST NOT carry organization membership.
- **FR-007**: The database MUST verify the signature and expiry with a key that the ordinary application role cannot read, change or replace. An absent, malformed, tampered or expired context MUST identify no account.
- **FR-008**: The account MUST come only from the authenticated session principal of the current request. It MUST NOT come from session-ticket claims, request headers, cookies, query values or any database setting that a caller can set (GOZ-0001-IF-04).
- **FR-009**: The context MUST be scoped to a single transaction and MUST NOT survive on a pooled connection. Session resolution, sign-in, registration and identity operator commands MUST work without any tenant context (GOZ-0001-IF-03).
- **FR-010**: A separate caller-set organization or tenant setting MUST NOT grant access. Membership MUST always be re-checked in the database.
- **FR-011**: The context lifetime MUST be short (minutes at most) and MUST be documented. It MUST tolerate small clock skew between the application and the database.
- **FR-012**: The signing key MUST be provided to the processes that need it through configuration (Compose `.env`, Helm secret). The API MUST refuse to start without a key of adequate length. The key MUST never appear in logs, telemetry, error messages, SQL text or screenshots.
- **FR-013**: The documentation MUST state the remaining limits plainly: row-level rules do not protect against SQL that runs inside a request with that request's own valid context; they do not protect against compromised owner, superuser or signing-key credentials.

**Database roles and privileges**

- **FR-014**: The ordinary application role MUST remain non-owner, non-superuser and without the right to bypass row-level security. The owner role MUST be bound by the rules. Tests MUST check both roles against the real database.
- **FR-015**: The key store and the verification helper MUST be owned by the owner role. The application role MUST be able to call the verification helper, but MUST NOT be able to read, change or replace the key, or to replace or alter the helper.
- **FR-016**: No new database role is added in this slice.

**Table classification (visibility scaffolding)**

- **FR-017**: Every table in the application schema MUST be classified as tenant-owned, global identity infrastructure (explicit allow-list), or shared/public (reserved; no table may use it until GOZ-0012 defines its separately verified access path).
- **FR-018**: An automated check against the migrated database MUST fail when any table is unclassified, when a tenant-owned table does not enforce rules for all roles or has no rule, or when any table is classified as shared/public.
- **FR-019**: The GOZ-0001 identity tables (`users`, `user_claims`, `user_logins`, `user_tokens`, `auth_sessions`, `data_protection_keys`) MUST be on the global allow-list. Any GOZ-0040 or GOZ-0041 account-level tables that merge first MUST be added there too (GOZ-0002-IF-14).
- **FR-020**: The pattern for future tenant-owned tables MUST be documented: the table carries `organization_id` with a foreign key to `organizations`, and its row-level rules are created in the same migration that creates it.

**Product behaviour**

- **FR-021**: Every existing API response and page MUST keep its current behaviour: same status codes, the same empty "not found" for malformed, unknown and non-member organization identifiers, the same no-store caching, and the same generic invalid-invitation refusal.
- **FR-022**: The application membership check MUST remain the first step of every organization-scoped endpoint. Row-level security is a second, independent layer, not a replacement.
- **FR-023**: If a write is refused by the database's row-level rules, the request MUST be answered without revealing whether the target exists (the same "not found" as a non-member), and the refusal MUST be counted.

**Operator and demo paths**

- **FR-024**: `seed` MUST act as a seeded member account of each demo organization. It MUST create a second fixed-identifier demo organization whose members are disjoint from the first, and restore both organizations' names, exact members and no open invitations on every run (GOZ-0002-IF-06).
- **FR-025**: `seed`, `reset-password` and `unlock` MUST NOT gain any right to read or change tenant data beyond what the account they act as may do.

**Observability**

- **FR-026**: Establishing and clearing the account context MUST be visible in traces as a span or span event within the request's single trace, using `gozleme.*` names.
- **FR-027**: The system MUST count context rejections and row-level refusals with a counter labelled only by a fixed set of reasons (for example missing, invalid, expired, refused-write).
- **FR-028**: The organization identifier MAY appear as a span attribute only after membership is confirmed, as in GOZ-0002. It MUST NOT be a metric label. Names, emails, codes, the signed context and the key MUST NOT appear in any span, metric, log or audit event.
- **FR-029**: Security-relevant events from this module (for example context rejected) MUST use the security audit log category with their own event ID range, referring to accounts by opaque identifier only.

**Delivery**

- **FR-030**: The cumulative demo MUST show two tenants through the UI and API with cross-tenant access denied, and MUST record direct-database evidence of User Stories 2 and 3 as sanitized test or log output.
- **FR-031**: When this spec merges, the Helm README note "do not cut a release before then" MUST be removed and the closure of the GOZ-0002-IF-01 exception recorded.
- **FR-032**: Storage migrations, configuration, compatibility impacts (including any local volume reset or re-migration needed) and unsupported modes MUST be documented.

### Key Entities

- **Tenant-owned row**: any row of `organizations`, `memberships` or `invitations`, and of any later table that carries an organization key. Visible and writable only to verified members of its organization.
- **Signed account context**: a per-transaction statement "this transaction acts for account X until time T", signed by the application. It carries no organization or membership.
- **Context signing key**: a secret shared by the application and the database's verification helper. The ordinary application role cannot read it.
- **Table classification**: the reviewed list that assigns every application table to tenant-owned, global identity, or reserved shared/public.
- **Second demo organization**: a fixed-identifier seeded organization whose members are disjoint from the first demo organization, for the two-tenant demo.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the cross-tenant test matrix (every tenant-owned table × read, insert, update, delete × each context variant: none, plain forged setting, tampered, expired, other-organization filter, stale pooled connection), 100% of attempts to reach another organization's rows are refused or return nothing.
- **SC-002**: 100% of application tables are classified, and the classification check fails on each of three seeded violations (unclassified table, tenant table without enforced rules, shared/public table).
- **SC-003**: All previous cumulative demo suites (GOZ-0000, GOZ-0001, GOZ-0002) pass unchanged against the RLS-enabled database, and the new two-tenant demo passes with real screenshots.
- **SC-004**: A member of either demo organization sees "Organization not found" for every page of the other organization. API responses for the other organization are byte-identical to those for an identifier that does not exist.
- **SC-005**: Organization pages in the demo still load in under 1 second each, as in GOZ-0002.
- **SC-006**: A search of all telemetry and logs captured during the demo finds zero occurrences of synthetic organization names, emails, invitation codes, signed contexts or key material.

## Assumptions

- The signing scheme is an HMAC over the account identifier and expiry, verified in the database with the `pgcrypto` extension on PostgreSQL 17. `pgcrypto` is a trusted extension that the database owner may install. The exact construction is a planning decision.
- The key reaches the database through the one-shot migrator, which is already configured with secrets and runs before the API. Re-running the migrator re-provisions a rotated key.
- Local Compose gets a throwaway development key in `.env.example`, like the existing development passwords. Production operators supply their own through the Helm secret.
- The existing local data volume does not need a reset, because no role changes. Running the migrator applies the new migration.
- The second demo organization's members are chosen during planning, from the existing synthetic accounts or a new one, so that earlier suites' assertions about seeded accounts still hold (GOZ-0002-IF-06).
- Roles, permissions and the policy evaluator remain out of scope (GOZ-0004 and later). Membership is the only authorization fact.
- Public and shared catalog records, private visibility grants and overlays are out of scope (GOZ-0012). This slice only reserves their classification.
- Row-level security is defence in depth beside application authorization. It is not claimed as protection against SQL injection within the attacker's own request context (invariant 2).
