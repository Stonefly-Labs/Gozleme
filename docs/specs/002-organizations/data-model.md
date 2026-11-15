# Data Model: Organizations and Membership (GOZ-0002)

All tables are in schema `gozleme`. Migration `20261116000000_Organizations` is additive only.
`gozleme_app` receives `SELECT, INSERT, UPDATE, DELETE` through the Foundation default privileges.
No RLS policy is defined yet: GOZ-0003 enables RLS on these tables using the explicit tenant key
columns marked **(tenant key)** below (FR-019, GOZ-0000-IF-03).

## organizations

| Column | Type | Rules |
|---|---|---|
| `id` **(tenant key)** | `uuid` PK | UUIDv7, permanent, never reused |
| `display_name` | `varchar(100)` not null | Trimmed, 1–100 chars, no control characters, not unique (FR-002) |
| `created_at` | `timestamptz` not null | default `now()` |
| `created_by` | `uuid` null → `users(id)` `ON DELETE SET NULL` | Audit only, grants nothing (FR-018) |
| `updated_at` | `timestamptz` not null | Set on rename |

No deletion and no lifecycle status in this slice (R3-24, post-MVP).

## memberships

| Column | Type | Rules |
|---|---|---|
| `organization_id` **(tenant key)** | `uuid` → `organizations(id)` `ON DELETE CASCADE` | |
| `user_id` | `uuid` → `users(id)` `ON DELETE CASCADE` | Account deletion does not exist yet; cascade is the chosen behaviour (IF-03) |
| `joined_at` | `timestamptz` not null | default `now()` |
| `joined_via` | `varchar(16)` not null | `creator` or `invitation` (check constraint) |
| `invitation_id` | `uuid` null → `invitations(id)` `ON DELETE SET NULL` | Which invitation admitted the account |
| `selected_at` | `timestamptz` null | Context preference only (research R4) |

- PK `(organization_id, user_id)`, which also enforces one membership per account per organization.
- Index `(user_id, selected_at DESC)` for the switcher and active-context lookup.
- **No role, owner, admin or permission column** (FR-018; the boundary test asserts this).
- Removal and leaving delete the row. History lives in the security audit log.

## invitations

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | UUIDv7; the revoke target |
| `organization_id` **(tenant key)** | `uuid` → `organizations(id)` `ON DELETE CASCADE` | |
| `code_hash` | `bytea` (32) not null, unique | SHA-256 of the normalized code; the plaintext code is never stored (FR-007) |
| `created_by` | `uuid` null → `users(id)` `ON DELETE SET NULL` | |
| `created_at` | `timestamptz` not null | |
| `expires_at` | `timestamptz` not null | `created_at` + configured expiry (default 7 days) |
| `used_at` / `used_by` | `timestamptz` null / `uuid` null → `users(id)` `SET NULL` | Set together exactly once |
| `revoked_at` / `revoked_by` | `timestamptz` null / `uuid` null → `users(id)` `SET NULL` | Set together exactly once |

- Check constraint: `used_at` and `revoked_at` are never both set.
- Index `(organization_id, created_at)` for the open-invitations list.

### Invitation state (derived, never one stored status field)

```text
open ──redeem by non-member──▶ used
open ──revoke──▶ revoked
open ──expires_at ≤ now──▶ expired
open ──redeem by existing member──▶ open (unchanged, FR-009)
used | revoked | expired ──any redeem──▶ generic invalid_invitation (FR-010)
```

The "open" list shows only invitations with `used_at IS NULL AND revoked_at IS NULL AND expires_at > now()`.

## Derived: active organization (context preference)

For the caller: the membership with the greatest `selected_at`. If no membership has a
`selected_at`, the greatest `joined_at`. With no memberships, `null`. It is computed on every
`GET /api/v1/organizations`, is never cached in the session, and is never used as authority.

## Configuration (`Gozleme:Organizations`, validated at start-up)

| Key | Default | Bounds |
|---|---|---|
| `InvitationExpiryHours` | 168 | 1–720 |
| `RedeemPerMinute` | 10 | 1–100 |

## Seeded fixtures (operator `seed`, idempotent)

- Account `carol@demo.gozleme.test`, added to the GOZ-0001 accounts.
- Organization `Seeded Demo Org` with fixed ID `0193a000-0000-7000-8000-000000000002`. Each run resets its members to alice and carol, its display name, and removes all of its invitations.
