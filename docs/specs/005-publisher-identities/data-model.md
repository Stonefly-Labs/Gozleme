# Data Model: Publisher Identities and Canonical Capability IDs

Migration `20261119000000_PublisherIdentities`. It is additive: five tables, one non-definer role
function, guard and alias triggers, and policies. It has no data migration, no change to an existing
table or policy, no new database role, no lift of the owner binding and no disabled guard (FR-031).
`Down` drops the objects in reverse order. The design is in research R1–R7, verified on PostgreSQL 17.

## Namespace rule (shared)

`^[a-z][a-z0-9]*(-[a-z0-9]+)*$`: lowercase ASCII letters and digits, single interior hyphens, starting with a
letter and not ending with a hyphen. Handles are 2–39 characters and capability names 2–64. Reserved
handles are refused ([contracts/namespace.md](./contracts/namespace.md)). The rule is enforced in
`NamespaceRules` and as `CHECK` constraints, and input is never normalized.

## `gozleme.registry_origin` — InstanceReference

| Column | Type | Rules |
|---|---|---|
| `id` | `smallint` PK | `CHECK (id = 1)`: exactly one row |
| `origin` | `text` | host-name rule, ≤ 253 characters (research R6) |
| `recorded_at` | `timestamptz` | default `now()` |

- Written once, by the migrator from `Gozleme:Registry:Origin`.
- `gozleme_app` holds `SELECT` only, which is the pinned read path.
- `immutable_row_guard` refuses `UPDATE`, `DELETE` and `TRUNCATE` for every login.

## `gozleme.publishers` — TenantOwned (RLS forced)

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | UUIDv7 from the application; immutable |
| `organization_id` | `uuid` FK → `organizations(id)` | immutable |
| `handle` | `text` | namespace rule, 2–39, not reserved; the handle it registered; immutable; **not unique here** |
| `display_name` | `text` | 1–100 characters, trimmed, no control characters (`DisplayNames`); the only mutable column |
| `created_at` | `timestamptz` | immutable |
| `created_by` | `uuid` FK → `users(id)` `ON DELETE SET NULL` | changes only to `NULL` (cascade) |
| `updated_at` | `timestamptz` | changes with the display name |

`UNIQUE (id, organization_id)` is the target of the composite foreign keys.

- **Policies**:
  - select: member;
  - insert: `account_has_role(org,'admin') AND created_by = current_account_id()`;
  - update: admin, and `WITH CHECK` also requires that the publisher holds a `held` claim;
  - no delete policy.
- `DELETE` and `TRUNCATE` are revoked.
- `publishers_guard` (`BEFORE UPDATE OR DELETE`) allows only `display_name` and `updated_at` to change, and `created_by` only to change to `NULL`.

**Derived state**: *active* when a `handle_claims` row with this `publisher_id` and `status = 'held'`
exists, and otherwise *suspended*. It is never stored on this row (FR-037).

**Publisher ID** (rendered): `gz://<origin>/pub/<id>`.

## `gozleme.capabilities` — TenantOwned (RLS forced)

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | UUIDv7 from the application; immutable |
| `organization_id` | `uuid` FK → `organizations(id)` | immutable |
| `publisher_id` | `uuid` | composite FK `(publisher_id, organization_id)` → `publishers(id, organization_id)`; immutable |
| `name` | `text` | namespace rule, 2–64; immutable |
| `display_name` | `text` | 1–100 characters; mutable |
| `created_at`, `created_by`, `updated_at` | | as for `publishers` |

`UNIQUE (publisher_id, name)` and `UNIQUE (id, organization_id)`.

- **Policies**:
  - select: member;
  - insert: `account_has_role(org,'publisher') AND created_by = current_account_id()`, and the publisher must be active;
  - update: publisher role, and `WITH CHECK` also requires that the publisher is active;
  - no delete policy.
- `DELETE` and `TRUNCATE` are revoked.
- `capabilities_guard` keeps every column fixed except `display_name` and `updated_at`.
- `capabilities_reserve_alias` (`AFTER INSERT`) reserves the alias in the same statement.

**Canonical ID** (rendered): `gz://<origin>/cap/<id>`. **Alias** (derived):
`<publisher.handle>/<name>`.

## `gozleme.handle_claims` — InstanceClaims (RLS enabled, not forced)

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | immutable |
| `handle` | `text` | namespace rule, not reserved; immutable |
| `status` | `text` | `blocked`, `earmarked`, `held`, `released`, `reclaimed` |
| `organization_id` | `uuid` FK → `organizations(id)`, nullable | earmarked or holding organization; immutable |
| `publisher_id` | `uuid`, nullable | composite FK `(publisher_id, organization_id)` → `publishers` |
| `created_at` | `timestamptz` | immutable |
| `held_at`, `ended_at` | `timestamptz`, nullable | set by consume/insert and by reclaim/release |

- A `CHECK` ties the columns to the status. For example, `blocked` has no organization and no publisher, and `held` has a publisher and `held_at`.
- `ux_handle_claims_live UNIQUE (handle) WHERE status IN ('blocked','earmarked','held')` enforces handle uniqueness.
- `ux_handle_claims_publisher UNIQUE (publisher_id) WHERE publisher_id IS NOT NULL` means one claim per publisher, ever.

**State transitions** (`handle_claims_guard`, every login; research R2, R4):

| From | To | Who |
|---|---|---|
| (new) | `blocked`, `earmarked` | owner, no account context (operator) |
| (new) | `held` | `gozleme_app`, admin of the organization, for a publisher of that organization |
| `earmarked` | `held` | `gozleme_app`, admin of the earmarked organization (consume) |
| `blocked`, `earmarked` | `released` | operator |
| `held` | `reclaimed` | operator; `publisher_id` and `held_at` unchanged |
| `released`, `reclaimed` | — | terminal; a suspension is never undone in this slice |

There is no `DELETE` for any login.

- **`gozleme_app` policies**:
  - select: rows of the caller's organizations;
  - insert: `held` only, by an admin, for a publisher visible in that organization;
  - update: `earmarked` → `held` only, by an admin.
- `gozleme_app` holds `SELECT, INSERT, UPDATE`.

## `gozleme.alias_reservations` — InstanceClaims (RLS enabled, not forced)

| Column | Type | Rules |
|---|---|---|
| `alias` | `text` PK | `<handle>/<name>`; unique forever, including retired aliases |
| `capability_id` | `uuid` UNIQUE | composite FK `(capability_id, organization_id)` → `capabilities` |
| `organization_id` | `uuid` | the capability's organization |
| `created_at` | `timestamptz` | |

- `gozleme_app` holds `INSERT` only, with no `SELECT`. The insert policy requires `account_has_role(org,'publisher')`.
- `alias_reservations_insert_guard` admits only inserts made by `capabilities_reserve_alias` (`pg_trigger_depth() ≥ 2`).
- `immutable_row_guard` refuses `UPDATE`, `DELETE` and `TRUNCATE` for every login.
- A reclaim never touches this table, so a retired alias stays reserved to its capability.

## Function

`gozleme.account_has_role(org uuid, role text) RETURNS boolean`:
- plain `sql`, `STABLE`, `search_path = pg_catalog, pg_temp`;
- reads the caller's own membership through `memberships_select`;
- not a definer: `PUBLIC` execute is revoked and `gozleme_app` has the grant;
- `account_is_admin` is unchanged.

Every new trigger function has `PUBLIC` execute revoked and no grant.

## EF model

`Publisher` and `Capability` entities map their tables, with no navigation into the claim tables.
Claims and origin are accessed through narrow SQL in `PublisherStore` and `RegistryOriginReader`, with
parameters only, because `alias_reservations` has no `SELECT` and `handle_claims` rows are written by
state transition. The model snapshot gains the two entities.

## Lock and transaction order

Registration runs in one request transaction:
1. Insert the publisher.
2. Consume the caller's earmark (`UPDATE … WHERE handle = $1 AND status = 'earmarked' AND organization_id = $2`), or insert a `held` claim.
3. Commit.

A concurrent registration waits on the live unique index and then gets `23505`. Capability creation
is one insert, and its alias is reserved in the same statement. Reclaim is one owner transaction
(`UPDATE … status = 'reclaimed'`, then an optional earmark insert). None of these takes the
per-organization advisory lock or a row lock on `organizations`, so the GOZ-0004 lock order is
unaffected.
