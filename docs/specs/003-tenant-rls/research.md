# Research: Tenant RLS and Visibility Scaffolding

Decisions for GOZ-0003. The four maintainer decisions in `spec.md` (Clarifications) are inputs, not
open questions. Each entry gives the decision, the rationale and the alternatives rejected.

## R1 — Signed account context: construction

**Decision**: The token is `v1.<account-uuid>.<expires-unix-seconds>.<hex HMAC-SHA256>`. The MAC is
computed over the text `v1.<account-uuid>.<expires-unix-seconds>`. The application signs with the key
from configuration. The database verifies the token with `pgcrypto`'s `hmac(text, bytea, 'sha256')`
and the key held in an owner-only table. Lifetime: 5 minutes. The database accepts a token only when
`now() < expires <= now() + 10 minutes`, which bounds future-dated tokens and tolerates up to 5 minutes
of clock skew in either direction.

**Rationale**: An injected statement runs as `gozleme_app`, exactly like the application. The only
thing the application has that injected SQL does not is process memory, so the key stays out of any
table or setting that `gozleme_app` can read. HMAC-SHA256 is standard. `pgcrypto` is a trusted
extension on PostgreSQL 13 and later, so the database owner (`gozleme_migrator`) can install it
without superuser rights, and it ships in both test images (`pgvector/pgvector:pg17`,
`postgres:17-alpine`). The token carries no organization (spec FR-006), so a removal takes effect on
the next statement even while the token is still valid.

**Alternatives considered**:
- *Plain `set_config` account ID*: rejected by the maintainer, because the forged-account test could not pass.
- *Bearer session key checked against `auth_sessions`*: rejected. `gozleme_app` has DML on `auth_sessions`, so injected SQL could insert a session row for any account.
- *Key generated inside the database*: rejected. The API must also hold the key to sign, and it can only read it from the database as `gozleme_app`, which would expose it to injection.
- *HMAC built by hand from `sha256()`*: rejected. PostgreSQL has no built-in bytea XOR, and the construction is error-prone.
- *Constant-time comparison*: not available in SQL. A timing attack through SQL statements against a per-transaction MAC is impractical, and the residual risk is documented.

## R2 — Key provisioning and rotation

**Decision**: Configuration key `Gozleme:Rls:ContextKey` (environment `Gozleme__Rls__ContextKey`): a
string of at least 32 characters whose UTF-8 bytes are the HMAC key. The **migrator** writes it into
`gozleme_private.context_keys` (single row, upsert) after applying migrations. The **API** and the
**seed** command load it and refuse to start if it is missing or shorter than 32 characters. Compose
supplies `${GOZLEME_CONTEXT_KEY:-local-dev-context-key-not-a-secret-0001}`, with the same throwaway
convention as the database passwords and an allow-listed gitleaks pattern. Helm reads it from the
existing operator-managed `gozleme-database` secret under the new key `rls-context-key`. Rotation:
change the secret, re-run the migrator, restart the API. Contexts in flight under the old key are
rejected (fail closed).

**Rationale**: The migrator already holds secrets and runs before the API (Compose `depends_on`, Helm
pre-upgrade job). A text key avoids encoding mistakes and matches the existing secret style. On
startup the API runs one self-check (R9), so a mismatch is visible immediately instead of surfacing
as "every organization is not found".

**Alternatives considered**: A separate Helm secret (more operator wiring for no gain). Base64 binary
keys (looks like a credential to gitleaks and adds a decode step). Writing the key from the API
(impossible: `gozleme_app` has no access to the key table, by design).

## R3 — Where the account context is established (transaction scope)

**Decision**: A new endpoint filter, `TenantTransaction`, on the `/organizations` and `/invitations`
route groups. For each request it begins one database transaction, calls
`gozleme.establish_context(token)` with a freshly signed token for
`ClaimTypes.NameIdentifier`, runs the handler, and commits if the transaction is still open.
Disposal rolls back on an exception. `RemoveMemberAsync` and `RedeemAsync` stop opening their own
transactions and use the ambient one (`db.Database.CurrentTransaction`). The token goes into
`set_config(..., true)` (transaction-local) inside the function, so it disappears at commit or
rollback and never persists on a pooled connection.

**Rationale**: A transaction-local setting in autocommit mode would last only one statement, so every
organization request needs an explicit transaction. One filter covers every current and future
endpoint in these groups. Forgetting it fails closed: no context means zero rows, and tests catch it.
Session resolution (`DbTicketStore`, `SessionPrincipalValidator`) and the identity endpoints never run
the filter, so they work with no tenant context (GOZ-0001-IF-03).

**Alternatives considered**:
- *EF command interceptor that prefixes each command*: it changes result sets, and its interaction with EF batching is fragile.
- *Session-level setting on connection open, reset on close*: this relies on pool reset behaviour, which spec FR-009 forbids.
- *A transaction for every API request*: it would wrap identity and status endpoints that touch no tenant data.

## R4 — Policy predicates and the FORCE recursion problem

**Decision**: Policies call owner-defined `SECURITY DEFINER` helpers (R5). Every policy predicate has
this shape:

```sql
CASE WHEN gozleme.rls_internal() THEN true ELSE <tenant predicate> END
```

`gozleme.rls_internal()` is true only when `current_user = 'gozleme_migrator'` **and**
`session_user <> 'gozleme_migrator'`, that is, only while an owner-defined `SECURITY DEFINER` helper
runs on behalf of another login. `CASE` guarantees evaluation order, so the tenant predicate is never
evaluated inside a helper.

**Rationale**: Under FORCE (maintainer decision) the owner is bound by the policies, and no role in
the system has `BYPASSRLS`. A membership helper that reads `memberships` is therefore itself filtered
by the `memberships` policy, which calls the helper again: unbounded recursion. PostgreSQL does not
guarantee the evaluation order of `OR` arguments or of multiple permissive policies, so the guard must
be a `CASE`. `gozleme_app` cannot change `current_user` to the owner: it is `NOINHERIT`, is not a
member of the owner role, and `SET SESSION AUTHORIZATION` needs superuser. The owner's own login has
`session_user = 'gozleme_migrator'`, so it stays fully bound. Future data migrations must sign a
context (FR-020). The only code that gets the bypass is the fixed set of owner-defined definer
functions, and the catalog check (R11) pins that set.

**Verified 2026-11-15** in a scratch `pgvector/pg17` container initialized with `01-roles.sh`:
- **First attempt, rejected**: a function-level `SET gozleme.rls_internal = 'on'` flag fails with `permission denied to set parameter` for a non-superuser owner on PostgreSQL 17, so it was replaced by the `session_user` guard.
- **`gozleme_app` with each context variant**: no context, a plain forged account ID, a tampered token, and a set bypass flag each returned 0 rows. A valid token returned exactly its own organization. A filter for the other organization returned 0 rows. After commit, the context had cleared (0 rows).
- **Key schema**: `gozleme_app` got `permission denied for schema gozleme_private`.
- **Owner login**: with no context it saw 0 rows (FORCE effective). With a valid token it saw 1 row.
- **Recursion and cascade**: the membership helper did not recurse, and `ON DELETE CASCADE` from `users` removed the memberships under forced RLS.
- **Probe artifact**: `SET ROLE gozleme_migrator` from a superuser leaves `session_user = postgres`, which satisfies the guard. Tests must log in as the owner directly, never `SET ROLE` from a superuser.

**Amended 2026-11-15 (implementation, found by the cumulative demo)**: the owner-login check above
passed only because the plan used the `memberships.user_id` index. Under a sequential scan the
policy is evaluated on every row a helper reads, and for the owner login (`session_user` =
`current_user` = owner) the guard was false inside the helper too, so `account_organizations()`
recursed until `stack depth limit exceeded`. This failed closed, but it broke the owner-login
guarantees and signed-context data migrations. The fix: the three helpers that read tenant tables
(`account_organizations`, `is_new_organization_of_current_account` and `lock_invited_organization`,
now PL/pgSQL) set the transaction-local `gozleme.rls_helper` flag with `set_config` in their body
(the rejected function-level `SET` clause is not used) and restore its previous value. The guard
accepts the flag only when `current_user` is the owner:
`current_user = 'gozleme_migrator' AND (session_user <> 'gozleme_migrator' OR current_setting('gozleme.rls_helper', true) = 'on')`.
The serving role gains nothing by setting the flag (`TenantContextBoundaryTests`). An owner login
that sets it by hand bypasses its own FORCE, which it can already do with `ALTER TABLE … NO FORCE`.
That falls under the compromised-owner non-guarantee (contract §4). `RlsIsolationTests.Helpers_never_recurse_through_their_own_policy_whatever_the_plan`
forces sequential scans for both roles.

**Alternatives considered**:
- *Mirror table of memberships in a private schema, maintained by triggers*: it duplicates data and adds consistency risk.
- *Organization claim inside the signed token*: removal would not take effect until expiry, and spec FR-006 rules it out.
- *No FORCE*: rejected by the maintainer.
- *Helper owned by a BYPASSRLS role*: needs a new role, which spec FR-016 rules out and which requires `make reset`.

## R5 — Helper functions

**Decision**: These functions are all in schema `gozleme` and owned by `gozleme_migrator`, with
`SET search_path = pg_catalog, pg_temp`. `EXECUTE` is revoked from `PUBLIC` on every one of them, and
each is granted explicitly to `gozleme_app`. All are `SECURITY DEFINER` except `invitation_capability`
and `rls_internal` (as implemented; amended per GOZ-0003-IF-17):

| Function | Returns | Purpose |
|---|---|---|
| `establish_context(token text)` | `text` (`ok`/`missing`/`invalid`/`expired`) | Verifies and stores the token transaction-locally; on failure stores an empty value |
| `current_account_id()` | `uuid` or NULL | Re-verifies the stored token on every call (STABLE) |
| `account_organizations()` | `SETOF uuid` | Organizations of the verified account (internal, R4) |
| `is_new_organization_of_current_account(org uuid)` | `boolean` | `created_by` = account and no memberships yet (internal) |
| `invitation_capability()` | `bytea` or NULL | Decoded `gozleme.invitation_capability` setting (32 bytes, else NULL); not `SECURITY DEFINER` |
| `lock_invited_organization(code_hash bytea)` | `uuid` or NULL | Takes `FOR KEY SHARE` on the organization of the invitation with that hash (internal) |
| `rls_internal()` | `boolean` | Owner-in-definer guard (R4, amended); not `SECURITY DEFINER`; `PUBLIC` revoked like the others |

HMAC input is `convert_to(payload, 'UTF8')` because `pgcrypto` has no `hmac(text, bytea, text)`
overload. As implemented, the verifier traps no errors at all: it validates the token's shape with
regular expressions (UUID, an expiry of at most 12 digits, 64 hex characters) before any cast, so
malformed input returns `invalid` without an exception. A missing extension still raises an error.
A missing key row returns `invalid`, which the startup self-check reports as audit event 1203.

Policies use `(SELECT gozleme.current_account_id())` and `IN (SELECT gozleme.account_organizations())`.
The planner evaluates both once per statement (initplan or hashed subplan), not once per row.

**Rationale**: The helpers have small fixed bodies, take no SQL text and expose nothing beyond what
the policy would allow. `establish_context` lets the application learn why a context failed, for
telemetry (FR-027), without trusting the result: the policies call `current_account_id()`, which
verifies again.

## R6 — Pre-tenant paths (GOZ-0002-IF-02)

**Decision**:
- **List and membership check**: a membership row is visible when `user_id` is the verified account, or its organization is one of the account's organizations.
- **Create**: the `organizations` INSERT check is `created_by = account`. The `organizations` SELECT policy also admits `is_new_organization_of_current_account(id)`, so EF's INSERT and any RETURNING clause see the row before the first membership exists. The first membership is admitted for `user_id = account AND joined_via = 'creator' AND is_new_organization_of_current_account(organization_id)`.
- **Redeem**: the application sets `gozleme.invitation_capability` to the hex SHA-256 of the presented code (transaction-local). The `invitations` SELECT and UPDATE policies admit the row whose `code_hash` equals it. `lock_invited_organization(hash)` replaces the unguarded lookup and the `FOR KEY SHARE` lock (the organization stays invisible to the redeemer). The membership INSERT is admitted for `user_id = account AND joined_via = 'invitation'` when an invitation with that `invitation_id`, the same organization and the capability hash is not revoked, has not expired, and is unused or used by this account. The invitation UPDATE check admits the redemption only when `used_by = account`.

**Rationale**: Possession of a 160-bit code is the proof of invitation (GOZ-0002), so a setting that
carries its hash is a bearer capability, not an identity claim. Without the code, the hash can only be
read through RLS, which shows invitations only to members of their organization. Injected SQL
therefore cannot produce another organization's hash. Lock order stays organization (KEY SHARE, inside
the helper) then invitation (FOR UPDATE), as in removal (GOZ-0002-IF-20).

## R7 — Policy set per table

**Decision**: These are explicit per-command policies. `acct` is `(SELECT gozleme.current_account_id())`
and `orgs` is `(SELECT gozleme.account_organizations())`, and each predicate is wrapped as in R4.

| Table | SELECT (USING) | INSERT (CHECK) | UPDATE (USING / CHECK) | DELETE (USING) |
|---|---|---|---|---|
| `organizations` | `id IN orgs OR is_new_organization_of_current_account(id)` | `created_by = acct` | `id IN orgs` / `id IN orgs` | none: denied (no deletion feature) |
| `memberships` | `user_id = acct OR organization_id IN orgs` | `organization_id IN orgs` OR first-member OR invitation path (R6) | `user_id = acct AND organization_id IN orgs` / same | `organization_id IN orgs` |
| `invitations` | `organization_id IN orgs OR code_hash = invitation_capability()` | `organization_id IN orgs AND created_by = acct` | same as SELECT / `organization_id IN orgs OR (code_hash = invitation_capability() AND used_by = acct)` | `organization_id IN orgs` |

RLS is enabled and **forced** on all three tables.

**Rationale**: The row-level rule is "members of the organization", which is coarser than the
application's rules (for example, the last-member guard stays in the application; spec FR-022).
`memberships` INSERT by an existing member lets `seed` add the fixed members while acting as a member
(maintainer decision). The API never uses that path, and GOZ-0004 can narrow it with roles.
Foreign-key checks and `ON DELETE CASCADE`/`SET NULL` actions run with row security disabled
internally in PostgreSQL, so the GOZ-0002 cascades keep working (GOZ-0002-IF-12). Tests verify this.

**Amended 2026-11-16 (second convergence pass, C1)**: the capability branch of `invitations_update`
originally checked only the code hash and `used_by`. Policies cannot compare old and new rows, so a
code holder could move the invitation into another organization (and then join it through
`memberships_insert`), reopen a revoked one, or extend it. The capability branch's `USING` now requires
an open invitation. A `BEFORE UPDATE` trigger, `gozleme.invitations_guard` (not a definer, `PUBLIC`
revoked), keeps `id`, `organization_id`, `code_hash` and `created_at` fixed, lets `expires_at` move
only earlier, and makes `revoked_at` and `used_at` set-once. The `*_by` columns may change only to NULL,
for the GOZ-0002 `SET NULL` cascades. It applies to every role. The regression test is
`TenantContextBoundaryTests.An_invitation_code_cannot_move_reopen_or_extend_its_invitation`.

## R8 — Refused writes and existence hiding

**Decision**: `TenantTransaction` catches `PostgresException` with SQLSTATE `42501` (raised for
`new row violates row-level security policy`), directly or inside `DbUpdateException`. It answers the
same empty `404` with `no-store`, increments the denial counter with `refused_write`, and writes audit
event 1202. Reads never raise: an invisible row is simply absent.

**Rationale**: The application check runs first, so a refusal means a defect or an attack. Treating it
as "not found" keeps responses identical to the non-member case (spec FR-023).

## R9 — Telemetry and audit

**Decision**:
- **Span**: `gozleme.tenant.transaction` (ActivitySource `Gozleme.Api`) spans the filter's transaction. It carries the events `gozleme.tenant.context.established` (attribute `gozleme.tenant.context.result`) and `gozleme.tenant.context.cleared` (attribute `gozleme.tenant.transaction.outcome` = `commit` or `rollback`). The `gozleme.org.*` spans become its children. No organization identifier is added at this level, because membership is not yet confirmed. `gozleme.organization.id` stays on the `gozleme.org.*` spans as in GOZ-0002 (maintainer decision).
- **Counter**: `gozleme.tenant.denials`, labelled `gozleme.tenant.denial.reason` with the values `missing`, `invalid`, `expired`, `refused_write`. That is bounded cardinality and carries no tenant label.
- **Audit**: module range 1201–1210. 1201 context rejected (reason, account ID). 1202 row-level write refused (account ID). 1203 context self-check failed at startup (reason only).
- **Startup self-check**: the API signs a probe token for the nil UUID and calls `establish_context`. A result other than `ok` logs 1203 and increments the counter. The API still starts, failing closed, because the database can be fixed without redeploying.
- **Secrecy**: the token and the code hash are passed only as SQL parameters, so `db.query.text` shows placeholders. Npgsql instrumentation does not record parameter values. The key never leaves `IOptions`. Tests search captured telemetry for the token prefix `v1.`, the key and the hash.

## R10 — Seed acts as a seeded member

**Decision**: `seed` loads the key and acts through `TenantTransaction`'s signer (a shared
`TenantContextSigner` service). For each fixed demo organization (Seeded Demo Org = alice and carol;
new **Seeded Partner Org** `0193a000-0000-7000-8000-000000000003` = dave), it works in one transaction:
1. Pick the first seeded member who is currently a member.
2. If there is none and the organization does not exist, create it as the first seeded member, using the creator path.
3. If the organization exists with no seeded member, fail with exit code 1 and the message "demo organization cannot be restored as a member; run make reset".
4. As that member: restore the name, delete every invitation, delete non-seeded memberships, and add missing seeded members.

`dave@demo.gozleme.test` is added to `DemoAccounts`.

**Rationale**: This is the maintainer decision: no rights beyond a member's. A new account `dave`
keeps `bob`'s GOZ-0002 assertion (exactly one organization in the switcher, `organizations-demo.spec.ts:174`)
true. Case 3 cannot arise from the product, because the last member can never leave. It could arise
only from manual database edits.

## R11 — Table classification check

**Decision**: `src/Gozleme.Api/Persistence/TableClassification.cs` holds three explicit sets:
`TenantOwned` (`organizations`, `memberships`, `invitations`), `GlobalIdentity` (the six GOZ-0001
tables) and `SharedPublic` (empty, reserved for GOZ-0012). `RlsCatalog.ViolationsAsync(connection)`
queries `pg_class`/`pg_policy` for schema `gozleme` and reports:
- an unclassified table;
- a tenant-owned table without `relrowsecurity`, without `relforcerowsecurity` or with no policy;
- any table in `SharedPublic`;
- a `SECURITY DEFINER` function in `gozleme` that is not in the pinned helper list (R5), or lacks a fixed `search_path`. Any such function would get the R4 owner bypass;
- a migrator-owned `SECURITY DEFINER` function in any other non-system schema (GOZ-0003-IF-12);
- a view, materialized view or foreign table in `gozleme`, since none can be classified: a materialized view stores rows without row-level security, and a foreign table reaches data outside these policies (GOZ-0003-IF-12). A probe on 2026-11-16 confirmed that a plain view does not bypass the guard: inside it `current_user` stays the caller, so `rls_internal()` is false and the serving role saw 0 of 5 rows;
- `EXECUTE` granted to `PUBLIC` on any non-extension function in `gozleme` or `gozleme_private`;
- any privilege for `gozleme_app` in `gozleme_private`.

An integration test runs it on the migrated database and expects no violations. Negative tests create
offending tables as the migrator inside a rolled-back transaction and expect the matching violation.

**Rationale**: Keeping the list in `src` puts it next to the migrations that future specs edit. The
check is a library method, so the GOZ-0039 release gate can reuse it (GOZ-0002-IF-01, condition 3).

## R12 — Existing tests, demo and compatibility

**Decision**:
- **Tests**: `GozlemeApiFactory` and `GozlemeDatabase.MigrateAsync` pass a test key (`it-context-key-…`, allow-listed). Tests that inspect tenant tables through `ScalarAsync` keep using the superuser, which bypasses RLS even under FORCE, so they still see ground truth. Leak tests use `AppConnectionString` with explicit contexts.
- **Demo**: a new Playwright project `goz-0003` depends on `goz-0002`. It covers the two-tenant UI and API, the Jaeger tenant span, the denial counter and the no-secret scan. A database-evidence step in the same suite uses `compose()` (`e2e/tests/compose.ts`) to run `docker compose exec -T postgres psql` as `gozleme_app` and as `gozleme_migrator`. It signs test tokens in Node with the Compose development key and writes sanitized JSON into `evidence/rls-evidence.json`. No new script is needed.
- **Upgrade**: the migration is additive and reversible. Existing volumes need no reset (no role change). `.env` gains `GOZLEME_CONTEXT_KEY`, which defaults in Compose. The Helm secret needs `rls-context-key` before upgrading, a breaking operator prerequisite that is documented. The external PostgreSQL must have `pgcrypto` available.

## R13 — Release note closure (FR-031)

**Decision**: Remove "do not cut a release before then" from the `deploy/helm/gozleme/README.md`
Organizations bullet, replacing it with "Organization data is protected by application authorization
and PostgreSQL row-level security (GOZ-0003)". Record the closure of GOZ-0002-IF-01 in
`progress.yaml` and in the PR.

## R14 — Not built

- **Roles and permissions**: GOZ-0004.
- **Public or shared records, grants and overlays**: GOZ-0012. Only the classification is reserved.
- **No new UI pages**: the existing organization pages show the two-tenant outcome.
- **No protection against**: SQL running within the attacker's own request context, a compromised owner or superuser credential, or a leaked context key. These limits are documented (spec FR-013).
