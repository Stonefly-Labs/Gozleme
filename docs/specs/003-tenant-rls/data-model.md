# Data Model: Tenant RLS and Visibility Scaffolding

No tenant table gains or loses a column. Migration `20261117000000_TenantRls` adds security objects
only and is fully reversible. Its design is in [research.md](./research.md) R1–R7, and its database
contract is in [contracts/tenant-context.md](./contracts/tenant-context.md).

## New schema `gozleme_private`

- **Owner**: `gozleme_migrator`.
- **Grants**: no `USAGE` for `gozleme_app` or `PUBLIC`. It is outside `gozleme`, so the GOZ-0000 default privileges, which grant DML on every new `gozleme` table, do not apply.
- **Contents**: the `pgcrypto` extension, installed into this schema, and the table `context_keys`:

| Column | Type | Rules |
|---|---|---|
| `id` | `smallint` PK | `CHECK (id = 1)`, single row |
| `key` | `bytea NOT NULL` | `CHECK (octet_length(key) >= 32)`; UTF-8 bytes of `Gozleme:Rls:ContextKey` |
| `provisioned_at` | `timestamptz NOT NULL DEFAULT now()` | Updated when the migrator writes a different key |

The migrator writes this table after migrating (upsert). No other process writes it.

## Tenant-owned tables (existing, from GOZ-0002)

| Table | Tenant key | RLS | FORCE | Policies |
|---|---|---|---|---|
| `organizations` | `id` | on | on | SELECT, INSERT, UPDATE (no DELETE: denied) |
| `memberships` | `organization_id` | on | on | SELECT, INSERT, UPDATE, DELETE |
| `invitations` | `organization_id` | on | on | SELECT, INSERT, UPDATE, DELETE |

The predicates are in research R7. Foreign keys, cascades, indexes and check constraints are unchanged.

## Global identity tables (allow-listed, no tenant RLS)

`users`, `user_claims`, `user_logins`, `user_tokens`, `auth_sessions`, `data_protection_keys`
(GOZ-0001). They stay readable and writable by `gozleme_app` without context, for session resolution
and sign-in.

## Shared/public tables

None. The category is reserved for GOZ-0012, and the catalog check fails if any table is placed in it.

## Transaction settings (not stored)

| Setting | Set by | Scope | Meaning |
|---|---|---|---|
| `gozleme.account_context` | `gozleme.establish_context` (or any caller) | transaction | Signed token. It identifies an account only if `current_account_id()` verifies it |
| `gozleme.invitation_capability` | application, redemption only | transaction | Hex SHA-256 of the presented invitation code; a bearer capability for one invitation |

## Seeded demo fixtures (`seed`, not migrations)

| Organization | Fixed ID | Members |
|---|---|---|
| Seeded Demo Org | `0193a000-0000-7000-8000-000000000002` | alice (creator), carol |
| Seeded Partner Org (new) | `0193a000-0000-7000-8000-000000000003` | dave (creator, new account `dave@demo.gozleme.test`) |

Every run restores names, exact members and no invitations, acting as a seeded member (research R10).

## Lifecycle of a tenant transaction

```text
request ─▶ TenantTransaction filter
            BEGIN
            establish_context(sign(account, now+5m)) ─▶ ok | missing | invalid | expired
            handler: membership check (application) ─▶ queries filtered by RLS
            COMMIT (or ROLLBACK)  ─▶ settings discarded; pooled connection carries nothing
```
