# Data Model: Local Identity and Session Security (GOZ-0001)

All tables are in schema `gozleme`, created by migration `20261115000000_LocalIdentity` and owned by
`gozleme_migrator`. `gozleme_app` gets `SELECT, INSERT, UPDATE, DELETE` through the Foundation
default privileges. Nothing here is tenant-owned (FR-034), so there is no RLS in this slice (GOZ-0003).

## users (`GozlemeUser : IdentityUser<Guid>`)

| Column | Type | Notes |
|---|---|---|
| id | uuid PK | Opaque permanent id (UUIDv7), never reused. The only identifier allowed in audit logs |
| user_name / normalized_user_name | varchar(256) | Set to email / normalized email (Identity requires a user name). Unique index on normalized |
| email / normalized_email | varchar(256) | Trimmed. Unique index on `normalized_email` (FR-002) |
| email_confirmed | boolean | Always `false` in this slice (FR-005) |
| password_hash | text | Identity v3 PBKDF2 format (FR-004) |
| security_stamp | text | Rotated on sign-out-everywhere, operator reset and lockout |
| concurrency_stamp | text | Optimistic concurrency |
| lockout_enabled | boolean | `true` for all accounts |
| lockout_end | timestamptz null | Lockout until (FR-018) |
| access_failed_count | integer | Consecutive failures |
| phone_number, phone_number_confirmed, two_factor_enabled | — | Identity base columns. Unused, always null/false |
| created_at | timestamptz | Set on registration |
| last_sign_in_at | timestamptz null | Updated on successful sign-in |

Validation: email ≤ 254 chars and syntactically valid. Password 12–128 chars, not equal to the
email or its local part, and not on the common list.

## user_claims, user_logins, user_tokens

These are standard Identity user-only store tables with an FK to `users(id)` and cascade delete.
In this slice they are unused and empty. `user_logins` stays empty because no external provider is
registered, and the boundary test asserts this.

## auth_sessions (`AuthSession`)

| Column | Type | Notes |
|---|---|---|
| key_hash | bytea PK (32 bytes) | SHA-256 of the 256-bit random session key carried in the protected cookie. The raw key is never stored |
| user_id | uuid FK → users(id) on delete cascade | Indexed (revoke-all) |
| ticket | bytea | Serialized `AuthenticationTicket`, encrypted with Data Protection purpose `Gozleme.Identity.SessionTicket.v1` |
| created_at | timestamptz | Sign-in time |
| last_seen_at | timestamptz | Updated on sliding renewal |
| expires_at | timestamptz | Absolute cap (created_at + 12 h). Indexed for purge |

State transitions: a row is created on sign-in (new key every time), updated on sliding renew and
deleted on sign-out, sign-out-everywhere (all rows for the user), operator reset, lockout and
expiry. A request whose row is missing or expired is anonymous.

## data_protection_keys (`DataProtectionKey`)

| Column | Type | Notes |
|---|---|---|
| id | integer identity PK | |
| friendly_name | text | |
| xml | text | Key ring XML. **Unencrypted at rest by default** (research R3); access is restricted to DB roles |

## Security audit events (logs, not a table)

These are structured log records from category `Gozleme.Security.Audit`, with event names
`gozleme.audit.registered | signed_in | sign_in_failed | locked_out | signed_out | signed_out_all |
registration_refused | operator_reset | operator_unlock | seeded`. Fields: `UserId` (opaque, when
known), `Outcome`, `Category` (bounded), plus `TraceId`/`SpanId` from scopes. Records never carry an
email, password, cookie or session key. They are exported through the existing log pipeline
(Loki locally).
