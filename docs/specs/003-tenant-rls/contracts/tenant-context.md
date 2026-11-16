# Contract: Tenant context and row-level security (GOZ-0003)

This is the database-level contract that every later spec relies on. HTTP API contracts are unchanged
from GOZ-0002 (`docs/specs/002-organizations/contracts/organizations-api.openapi.yaml`). Every status
code, body and header stays the same.

## 1. Signed account context

```text
token   = "v1." account "." expires "." mac
account = lowercase hyphenated UUID (ClaimTypes.NameIdentifier of the authenticated session)
expires = Unix seconds, decimal; issued as now + 300
mac     = lowercase hex of HMAC-SHA256(key, UTF-8("v1." account "." expires))
key     = UTF-8 bytes of Gozleme:Rls:ContextKey (≥ 32 characters)
```

The database accepts a token when the MAC matches and `now() < expires <= now() + 600 s`. A token
names an account only, never an organization or a role.

## 2. Functions callable by `gozleme_app`

All are owned by `gozleme_migrator` and use `search_path = pg_catalog, pg_temp`. `EXECUTE` is revoked from `PUBLIC` on every function in `gozleme` and `gozleme_private` and granted to `gozleme_app` only for these. All are `SECURITY DEFINER` except `invitation_capability` and `rls_internal`, which read only the caller's own settings and session and so need no elevated rights. The pinned definer list is `TableClassification.DefinerHelpers`, which `RlsCatalogTests` enforces.

| Signature | Contract |
|---|---|
| `gozleme.establish_context(token text) RETURNS text` | Stores the token transaction-locally when valid and returns `ok`. Otherwise it stores `''` and returns `missing`, `invalid` or `expired`. Must be called inside an explicit transaction |
| `gozleme.current_account_id() RETURNS uuid` | The verified account, or NULL. It re-verifies on every call and never trusts a cached value |
| `gozleme.account_organizations() RETURNS SETOF uuid` | Organization IDs where the verified account has a membership row |
| `gozleme.is_new_organization_of_current_account(uuid) RETURNS boolean` | True only for an organization created by the verified account that has no members yet |
| `gozleme.invitation_capability() RETURNS bytea` | The 32-byte code hash from `gozleme.invitation_capability`, or NULL. Not a definer function |
| `gozleme.lock_invited_organization(bytea) RETURNS uuid` | Locks the invited organization `FOR KEY SHARE` and returns its ID, or NULL for an unknown hash |
| `gozleme.rls_internal() RETURNS boolean` | Policy guard. Not a definer function; it grants nothing to callers |

## 3. Guarantees

1. With no valid context, `gozleme_app` and `gozleme_migrator` logins see **zero** tenant rows and cannot insert, update or delete any.
2. With a valid context for account *X*, every statement sees only rows of organizations where *X* has a membership at that statement's snapshot, plus *X*'s own membership rows.
3. Caller-set settings other than a valid token never widen access. An organization or tenant setting has no effect.
4. Settings are transaction-local. No context survives `COMMIT` or `ROLLBACK`, so none survives a return to the pool.
5. `gozleme_app` cannot read, write or replace the key, and cannot create, replace or alter the helper functions.
6. Foreign-key checks and cascades are unaffected.

## 4. Non-guarantees (documented limits)

- SQL injected into a request runs with that request's valid context, and so with that account's access.
- Compromise of the owner or a superuser credential, or of the context key, defeats these rules.
- A token that leaks is valid for that account until it expires (at most 10 minutes).

## 5. Rules for future tenant-owned tables

A new tenant-owned table must:
1. carry `organization_id uuid NOT NULL REFERENCES gozleme.organizations(id)`;
2. in its creating migration, `ENABLE` and `FORCE ROW LEVEL SECURITY` and create its policies, using the `CASE WHEN gozleme.rls_internal() THEN true ELSE … END` shape and `organization_id IN (SELECT gozleme.account_organizations())`;
3. be added to `TableClassification.TenantOwned`.

Global account-level tables go to `TableClassification.GlobalIdentity`. Data migrations on tenant
tables must sign a context. They never rely on owner rights.

## 6. Configuration

| Process | Key | Required | Source (Compose / Helm) |
|---|---|---|---|
| API | `Gozleme__Rls__ContextKey` | yes; startup fails without it | `${GOZLEME_CONTEXT_KEY:-local-dev-context-key-not-a-secret-0001}` / secret `gozleme-database`, key `rls-context-key` |
| migrator | `Gozleme__Rls__ContextKey` | yes; provisions `gozleme_private.context_keys` | same |
| seed | `Gozleme__Rls__ContextKey` | yes | same (Compose `demo` profile) |
| reset-password, unlock | none | no | they touch global identity tables only |

## 7. Telemetry

| Kind | Name | Attributes and labels |
|---|---|---|
| Span | `gozleme.tenant.transaction` | events `gozleme.tenant.context.established` (`gozleme.tenant.context.result`) and `gozleme.tenant.context.cleared` (`gozleme.tenant.transaction.outcome`) |
| Counter | `gozleme.tenant.denials` | `gozleme.tenant.denial.reason` ∈ {`missing`, `invalid`, `expired`, `refused_write`} |
| Audit | `Gozleme.Security.Audit` 1201–1203 | opaque account ID or reason only |
