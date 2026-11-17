# Research: Publisher Identities and Canonical Capability IDs

Decisions for `plan.md`. Each entry gives the decision, the rationale and the alternatives considered.
Maintainer decisions Q1–Q11 are inputs here, not open questions (`spec.md` Clarifications;
`progress.yaml`). Q11 (the operator login) was decided during this plan (R4).

**Verified empirically** (2026-11-16): a scratch `postgres:17-bookworm` loaded with the dev stack's
`pg_dump --schema-only` at `20261118000000_ScopedRbac`. The prototype migration was applied as
`gozleme_migrator` without superuser rights. Every statement then ran as `gozleme_app` with signed
contexts, or as the owner with no context, one committed transaction each. The tables in R2–R7 are
the observed results, and the script is to be turned into `PublisherIsolationTests` and
`HandleClaimTests`.

## R1 — Table classes: two new tenant tables and two new instance classes

**Decision**:

| Table | Class | RLS | `gozleme_app` privileges |
|---|---|---|---|
| `publishers`, `capabilities` | `TenantOwned` | enabled, **forced** | `SELECT, INSERT, UPDATE` (no `DELETE`/`TRUNCATE`) |
| `registry_origin` | `InstanceReference` (new; GOZ-0003-IF-08 option B) | none (no tenant content) | `SELECT` only: the pinned read path |
| `handle_claims`, `alias_reservations` | `InstanceClaims` (new; maintainer Q10) | enabled, **not forced** (R4) | `handle_claims`: `SELECT, INSERT, UPDATE`; `alias_reservations`: `INSERT` only |

`RlsCatalog` gets one rule per new class, and each rule has a negative catalog test:
- **`InstanceReference`**: no `organization_id` column, and `gozleme_app` holds exactly `SELECT`.
- **`InstanceClaims`**: RLS enabled and not forced, at least one policy, and `gozleme_app` holds exactly the pinned privilege set for that table (no `DELETE` or `TRUNCATE` on any of them).

`TableClassification.DefinerHelpers` is unchanged.

**Rationale**: IF-08 option B covers origin-style data that only migrations write. A table that request
handlers write "does not fit this class" (GOZ-0003 impact analysis, IF-08 scope note). Q10 therefore
creates a distinct class for request-written, installation-owned namespace entries, rather than
stretching `InstanceReference`. Pinning privileges per table makes "insert-only, never readable" a
catalog-checked fact rather than a convention.

**Alternatives rejected**: claims as tenant rows (Q10 = A rejected it, because reclaim would then
change another tenant's row); claims in `GlobalIdentity` (forbidden by IF-08); `SharedPublic`
(reserved until GOZ-0012).

## R2 — One namespace table for handle claims and operator reservations

**Decision**: `gozleme.handle_claims` holds every handle namespace entry as one row with a `status`:

```text
blocked ──release──▶ released        (operator)
earmarked ─release─▶ released        (operator)
earmarked ─consume─▶ held            (admin of the earmarked organization, during registration)
(insert) held                         (admin, during registration of a free handle)
held ────reclaim───▶ reclaimed        (operator; permanent in this slice)
```

A partial unique index `ux_handle_claims_live (handle) WHERE status IN ('blocked','earmarked','held')`
is the **only** thing that enforces installation-wide handle uniqueness (FR-007, FR-037). The spec's
"handle claim" and "handle reservation" entities are rows of this table in different states. An
earmarked row becomes the claim when it is consumed, which is how FR-033's "registering an earmarked
handle consumes the earmark" happens atomically.

**Rationale**: Uniqueness has to span claims *and* reservations, and PostgreSQL cannot enforce a unique
constraint across two tables. A cross-table trigger check would run with the caller's rights. Under RLS
it cannot see other organizations' earmarks, and it races. One table and one partial index give the
conflict and the serialization for free.

**Verified**:

| Attempt | Result |
|---|---|
| A admin: publisher `acme-tools` + held claim | ok |
| B admin: claim `acme-tools` (held by A) | `23505 ux_handle_claims_live` → "unavailable" |
| B admin: claim `gozleme-official` (blocked) / `acme` (earmarked for C) | `23505 ux_handle_claims_live`, identical |
| B admin: consume C's earmark | `UPDATE 0` (policy hides the row) |
| C admin: register `acme`, consuming its earmark | ok, the row becomes `held` |
| C races A for an uncommitted `race` claim | waits, then `23505`; exactly one holder |
| B admin inserts a `blocked` row | refused by the guard (`42501`) |

## R3 — Visibility: the application sees only its own organizations' entries

**Decision**: `handle_claims_select` admits a row only when `organization_id IN account_organizations()`.
Blocked rows have no organization, so no account sees them. `alias_reservations` grants
`gozleme_app` no `SELECT` at all. The application never needs to read aliases, because the alias is
derived as `publisher.handle || '/' || capability.name`. A conflict reveals only that the value is
taken: the error detail echoes only the caller's own input, and the API maps it to a fixed body.

**Verified**: B admin sees 0 claims and A member sees A's 1 claim. With no context, 0 claims are visible.
`SELECT` on `alias_reservations` is denied by privilege. The owner sees every claim (R4).

**Oracle review (GOZ-0003-IF-06)**:

| Constraint | Who can trigger it | Disposition |
|---|---|---|
| `ux_handle_claims_live` | any admin | **revealed**: "unavailable" (`409`), inherent to a global namespace (Q3) |
| `alias_reservations_pkey` | a publisher member of the handle's current holder | **revealed**: `409 conflict`. Before a reclaim it can only be the caller's own alias. After one, only "a retired alias exists under this handle" (FR-014) |
| `capabilities (publisher_id, name)` | own publisher only | `409 conflict`; no cross-tenant reach |
| `ux_handle_claims_publisher (publisher_id)` | — | the insert policy checks `publisher_id` against the caller's visible publishers **before** the index (verified: a foreign publisher id gives an RLS violation, not `23505`); unreachable from the API, which inserts only the publisher it just created; mapped to the unknown-ID `404` |
| composite FKs `(publisher_id, organization_id)` | — | the caller's own organization id is always half of the key, so a foreign id fails identically whether or not it exists; `404` |

## R4 — The operator login (maintainer Q11 = A)

**Decision**: `handle-reserve`, `handle-release` and `handle-reclaim` connect with
`ConnectionStrings:GozlemeMigrator` (the owner) and run with no account context. `handle_claims` and
`alias_reservations` **enable** row security but do not **force** it. The owner is therefore the
installation authority over these two tables, and `gozleme_app` remains bound by their policies. The
guard trigger (R5) binds the owner to the operator transitions only: insert `blocked` or `earmarked`,
release an unused reservation, and reclaim a held claim. It cannot insert a held claim, un-reclaim,
release a held handle or rewrite a handle. The owner without context still sees **zero** rows of the
forced tenant tables.

**Rationale**: The operator commands that exist today run as `gozleme_app`, and so do request handlers.
If `gozleme_app` could reclaim with no context, any request path could too. Q10 requires that no tenant
row is touched, which rules out a non-account tenant token (GOZ-0003-IF-04 is reserved for GOZ-0015 and
tenant rows). The owner already holds every right that RLS could withhold from it (`ALTER TABLE … NO
FORCE`), so letting it see the claim tables adds no capability. It does not touch `publishers` or
`capabilities`. An earmark's organization is checked by the foreign key, whose check bypasses RLS and
reads no tenant column.

**Verified**:

| Operator attempt (owner, no context) | Result |
|---|---|
| block `gozleme-official`; earmark `acme` for C | ok |
| earmark for an unknown organization | `23503` → operator error "no such organization" |
| earmark a held handle | `23505` → operator error "handle is not free" |
| reclaim `acme-labs` and earmark it for C, in one transaction | ok; returns the former holder's opaque ids |
| reclaimed → held (un-suspend) | refused (`42501`, guard) |
| release a held handle; insert a held claim; change a handle | refused (guard) |
| read `publishers` / `capabilities` | 0 rows (forced RLS) |

**Alternatives rejected** (maintainer Q11): a dedicated `gozleme_operator` login. It would add a
secret to Compose and Helm, and roles are created only by the first-initialization script, so existing
volumes would need a manual role or a reset. Dropping reclaim would reverse Q9. A non-account signed
token belongs to GOZ-0015 (IF-04).

**Documented limit**: inside any owner-defined definer helper `current_user` is the owner, so such a
helper would see every claim. None of the pinned helpers reads these tables, and `RlsCatalog` already
fails on any new definer function.

## R5 — Database enforcement of roles, suspension and immutability

**Decision**:
- **`gozleme.account_has_role(org uuid, role text)`**: plain `sql`, `STABLE`, fixed `search_path`, not a definer. It reads the caller's own membership through `memberships_select`, with `PUBLIC` execute revoked and a grant to `gozleme_app` (GOZ-0004-IF-02 option A). `account_is_admin` stays as it is, so the GOZ-0004 policies are untouched.
- **Policies** (all `CASE` with the `rls_internal()` branch first on the tenant tables):
  - `publishers`: insert and update need `account_has_role(org, 'admin')`.
  - `capabilities`: insert and update need `account_has_role(org, 'publisher')`.
  - Inserts also require `created_by = current_account_id()`.
  - Update `WITH CHECK` and capability insert also require that the publisher still holds a `held` claim, so **suspension is enforced in the database** and derived from the claim (FR-037).
  - `handle_claims`: insert only `held` by an admin, for a publisher visible in that organization; update only `earmarked` → `held` by an admin.
  - No `DELETE` policy anywhere.
- **Guards** (plain `plpgsql`, `PUBLIC` execute revoked, raise `42501`, bind every login):
  - `publishers_guard` and `capabilities_guard` allow only `display_name` and `updated_at` to change. `created_by` may change only to `NULL`, through the account-deletion cascade (verified).
  - `handle_claims_guard` enforces the R2 state machine and who may take each step (R4), refuses `DELETE`, and keeps `id`, `handle`, `organization_id` and `created_at` fixed.
  - `immutable_row_guard` refuses `UPDATE`, `DELETE` and `TRUNCATE` on `registry_origin` and `alias_reservations` for every login, the owner included (GOZ-0000-IF-04).
- **Alias reservation is derived**: an `AFTER INSERT` trigger on `capabilities` inserts `handle/name` with invoker rights. A `BEFORE INSERT` guard on `alias_reservations` refuses any insert at `pg_trigger_depth() < 2`, so an alias cannot be forged, inserted early or reserved without its capability.

**Verified**:

| Attempt (as `gozleme_app`) | Result |
|---|---|
| publisher (non-admin) inserts a publisher | refused (`WITH CHECK`) |
| admin without `publisher` creates a capability | refused (`WITH CHECK`) |
| member renames a capability / publisher member renames a publisher | `UPDATE 0` (`USING` hides it) |
| publisher renames a capability; admin renames a publisher | ok |
| change capability `name`, publisher `handle` or `organization_id` | refused (guard) |
| delete or truncate a publisher, capability or claim | `permission denied` |
| admin updates its own held claim to `reclaimed` | `UPDATE 0` |
| direct insert into `alias_reservations` | refused (guard, depth 1) |
| owner updates or deletes `registry_origin` or an alias | refused (guard) |
| suspended publisher: create a capability, rename it or its capability | refused (`WITH CHECK`) |
| new holder of a reclaimed handle creates a retired name / a new name | `23505 alias_reservations_pkey` / ok |

Every row in the matrices above is repeated as the owner login in the integration tests (FR-024).

## R6 — Registry origin: recorded by the migrator, verified at start

**Decision**:
- Configuration key `Gozleme:Registry:Origin`, validated by `RegistryOrigin.IsValid`. The rule is ordinal and ASCII only (FR-001): dot-separated labels of 1–63 lowercase ASCII letters, digits and hyphens, no label starting or ending with a hyphen, at most 253 characters ([contracts/namespace.md](./contracts/namespace.md)). The same expression is a `CHECK` on the table.
- **Migrate**: `MigrationRunner` refuses a missing or invalid value before touching the database. If `registry_origin` already exists and holds a different value, it refuses before applying any migration. After `MigrateAsync` it runs `INSERT … ON CONFLICT (id) DO NOTHING` and then re-reads the row. A mismatch exits non-zero with "configured origin differs from the recorded origin". This mirrors the context-key provisioning, except that the origin is never overwritten.
- **API**: options `ValidateOnStart` refuses to start on a missing or invalid value. A blocking `IHostedService` (`RegistryOriginCheck.StartAsync`) reads the recorded origin as `gozleme_app`, the one pinned read path (`RegistryOriginReader`), and throws on a mismatch, so the host does not start. If the database is unreachable at start, the service starts so that GOZ-0000 outage behaviour is preserved. Publisher endpoints then answer `503 unavailable` until the origin has been verified on first use. A mismatch found then stops the application.
- **Rendering**: `gz://<origin>/pub/<uuid>` and `gz://<origin>/cap/<uuid>`, with the lowercase hyphenated UUID. Only the UUID is stored. The canonical string is derived, which is stable because the origin is immutable (R5) and verified at start.
- Compose: `Gozleme__Registry__Origin: ${GOZLEME_REGISTRY_ORIGIN:-registry.demo.gozleme.test}` on `migrate`, `api` and the operator invocations. Helm: required value `registry.origin`, with `required` failing the render when it is empty.

**Rationale**: Q1 = A. Recording from configuration in the migrator keeps request handlers unable to
write it (FR-002). Comparing before migrating means a mismatched run never applies anything.

**Alternatives rejected**: recording inside the EF migration class (it cannot read runtime
configuration cleanly, and a re-run would need special-casing); storing the canonical string per row
(duplicates the origin and invites drift); failing the API start when the database is unreachable
(regresses GOZ-0000 `DatabaseOutageTests`).

## R7 — Namespace rule and reserved handles

**Decision**: One ordinal rule, `^[a-z][a-z0-9]*(-[a-z0-9]+)*$`. Handles are 2–39 characters and capability names
2–64. The rule is implemented once in `NamespaceRules` (C#, `string.CompareOrdinal`/`char` range
checks, no regex culture) and once as table `CHECK`s. Input is never trimmed, lowercased or
normalized, and any deviation gives `400 validation_failed` with field `handle` or `name` and the rule
text. Reserved handles (FR-018) are a fixed list in `NamespaceRules.ReservedHandles`, also a `CHECK` on
`publishers.handle` and `handle_claims.handle`: `gozleme`, `admin`, `api`, `system`, `root`,
`registry`, `publisher`, `publishers`, `capability`, `capabilities`, `operator`, `support`,
`security`, `www`. The list is documented in `contracts/namespace.md`.

**Rationale**: Under `InvariantGlobalization=true` (GOZ-0000-IF-16), ASCII range checks behave the same
everywhere. Rejecting input rather than normalizing it removes look-alikes (US4). Duplicating the rule
as a `CHECK` means that even owner-written rows obey it.

## R8 — API shape and error mapping

**Decision**: Eight endpoints under `/api/v1/organizations/{organizationId}`
([contracts/publishers-api.openapi.yaml](./contracts/publishers-api.openapi.yaml)):
- list, register, read and rename publishers;
- list, create, read and rename capabilities, nested under `/publishers/{publisherId}`.

A capability ID under the wrong publisher is a `404`. Each endpoint declares one new action. The
procedure is `AuthorizeAsync` (membership `404` → `403 forbidden`), then validation (`400`), then the
target read (`404` if it is not visible), then suspension (`409 suspended`), then the write.

| Failure | Response |
|---|---|
| `23505 ux_handle_claims_live` | `409 {"error":"handle_unavailable"}`, one fixed body (`unavailable` already means the `503` outage answer) |
| `23505` on `capabilities (publisher_id, name)` or `alias_reservations_pkey` | `409 {"error":"conflict"}` |
| any other `23505`, `23503`, `42501` | the empty `404` (the `42501` path is `TenantTransaction`'s) |
| rate limit (registration) | `429 {"error":"rate_limited"}` with `Retry-After`, only after `AuthorizeAsync` passes (see **Rate limit**) |

A unique violation aborts the request transaction. The handler returns its result and `TenantTransaction` sees no live transaction and does not commit, which is the existing "lost redemption race" path.

**Rate limit**: `Gozleme:Publishers:RegisterPerHour` (default 10, range 1–100), a fixed window
partitioned by account id (GOZ-0002-IF-08).

The limiter is checked **in the handler, after `AuthorizeAsync` passes** (member holding `admin`),
before validation. It is not the `RequireRateLimiting` middleware that sign-in and invite redemption
use (maintainer decision on analysis finding U1, 2026-11-16). The reasons:
- A non-member or unknown organization always gets the identical empty `404` and never a `429` (FR-023).
- Refused (`404`, `403`) attempts do not use up the budget.
- Audit 1405 and the `rate_limited` outcome name only an organization whose membership has been confirmed (FR-026).

The rejection is recorded as the `register` operation with outcome `rate_limited` and audit 1405.
It is never recorded as a sign-in or account-registration failure (FR-008).

## R9 — Permission contract delta

**Decision**: new resources `publisher` and `capability` and eight actions: `publisher.list`,
`publisher.read`, `publisher.register`, `publisher.rename`, `capability.list`, `capability.read`,
`capability.create` and `capability.rename`.
- `admin` gets register and rename publisher.
- `publisher` gets create and rename capability.
- Every role gets the four read actions.
- The `admin` row stops being "all actions" (`OrgAction.All`) and becomes an explicit list, because Q5 keeps `capability.*` writes away from `admin`. A unit test asserts that `admin` lacks `capability.create`.
- Scoped endpoint count 9 → 17; resource set + `publisher`, `capability`.

The living `docs/architecture/contracts/organization-permissions.md` gets the rows, the endpoint → action
rows and the database-enforcement rows in this slice ([contracts/permissions-delta.md](./contracts/permissions-delta.md)).

## R10 — Telemetry and audit

**Decision**:
- **Spans** `gozleme.publisher.register`, `gozleme.capability.create`, `gozleme.publisher.rename` and `gozleme.capability.rename`. Attributes after membership: `gozleme.organization.id`, `gozleme.publisher.id`, `gozleme.capability.id` (opaque), and `gozleme.publisher.outcome`. The existing `gozleme.authz.decision` span is the parent.
- **Counter** `gozleme.publisher.operations` with the labels `gozleme.publisher.operation` ∈ {`register`, `rename`, `capability_create`, `capability_rename`} and `gozleme.publisher.outcome` ∈ {`success`, `unchanged`, `validation_failed`, `handle_unavailable`, `conflict`, `forbidden`, `suspended`, `not_found`, `rate_limited`}.
- **Operator spans** `gozleme.operator.handle_reserve`, `gozleme.operator.handle_release` and `gozleme.operator.handle_reclaim`, with exit codes only.
- **Audit range 1401–1410**:

| Id | Event | Fields |
|---|---|---|
| 1401 | `publisher_registered` | publisher, organization, user, claim |
| 1402 | `publisher_renamed` | publisher, organization, user |
| 1403 | `capability_created` | capability, publisher, organization, user |
| 1404 | `capability_renamed` | capability, publisher, organization, user |
| 1405 | `publisher_registration_refused` | category, organization, user |
| 1406 | `handle_reserved` | claim, kind, earmarked organization (opaque ids only) |
| 1407 | `handle_reservation_released` | claim |
| 1408 | `handle_reclaimed` | claim, publisher, organization, earmark |

  Handles, names, aliases and display names never appear. An unchanged display name writes no event.
  An operator event is written after commit.

## R11 — Seed and demo fixtures

**Decision**: `seed` grants carol `{member,publisher}` in Seeded Demo Org, acting as alice (the seeded
admin), in the existing restore path, so every run restores it. alice stays `{admin,member}`: no
`publisher`, which demonstrates US3. dave (Seeded Partner Org) stays `{admin,member}` and is the
"second organization". `seed` creates no publisher, because a handle is permanent and the demo creates
run-unique handles (`demo-<6 digits>`), so reruns never collide. The `goz-0005` suite runs the operator
commands with `docker compose run --rm migrate handle-…`, using the migrator connection that the
service already has.

## R12 — Pins and boundary tests narrowed deliberately

| Pin | Change |
|---|---|
| `LATEST_MIGRATION` (`bootstrap-demo.spec.ts`) | `/_PublisherIdentities$/` |
| `MigrationAndRoleTests` | 6 migrations, latest `20261119000000_PublisherIdentities` |
| `StatusEndpointTests` | new `schemaVersion` |
| `BootstrapBoundaryTests.Gozleme_schema_holds_exactly_…` | + `publishers`, `capabilities`, `handle_claims`, `alias_reservations`, `registry_origin` |
| `OrganizationPermissionContractTests` | 17 scoped endpoints; resources + `publisher`, `capability` |
| `RlsIsolationTests` | + `publishers`, `capabilities` for app and owner |
| `RlsCatalogTests` | + `InstanceReference` and `InstanceClaims` rules, each with a negative test |
| `GET /api/v1/capabilities`, `POST /api/v1/publish` | **unchanged**, still `404` (FR-025) |
| `OrganizationBoundaryTests` | unchanged; no new table matches `(polic\|grant\|role\|permission)` and no new route matches its list |

## R13 — Web

**Decision**:
- **Pages**:
  - `/organizations/[organizationId]/publishers` lists publishers and has the register form when the caller has `publisher.register`.
  - `/organizations/[organizationId]/publishers/[publisherId]` shows the detail, the capabilities, the create form when the caller has `capability.create`, and the rename forms.
  - Both gate with `getSession()` and render "Organization not found" on a `404`.
  - A suspended publisher shows a "Suspended" badge, and its forms are hidden.
  - The origin and canonical IDs are shown in monospace with a copy button.
- **Route handlers**: four new `POST`/`PATCH` handlers, each with `isSameOrigin()`.
- **Error messages**: a `409 handle_unavailable` shows "That handle isn't available." and a `409 conflict` shows "That name is already used under this publisher." A `403` routes to `forbiddenNoticePath`.
- **Registration confirmation** shows the handle and the organization's opaque id (FR-009).
- Browser spans `gozleme.web.publisher.register` and `gozleme.web.capability.create`.
