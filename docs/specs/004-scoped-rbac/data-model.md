# Data Model: Scoped RBAC Permission Foundation

There is one migration, `20261118000000_ScopedRbac`. It adds no new table: the role set lives on the
existing tenant-owned `gozleme.memberships` row (Q5 = B; research R1). `TableClassification` is
unchanged.

## `gozleme.memberships` (changed)

| Column | Type | Rules |
|---|---|---|
| `organization_id` | uuid | **immutable** (guard; was already the FK and PK part) |
| `user_id` | uuid | **immutable** (guard) |
| `joined_at` | timestamptz | **immutable** (guard; GOZ-0003-IF-19) |
| `joined_via` | varchar(16) | **immutable** (guard; GOZ-0003-IF-19) |
| `invitation_id` | uuid null | may only become NULL (the `ON DELETE SET NULL` cascade) |
| `selected_at` | timestamptz null | changed only on the caller's own row |
| **`roles`** (new) | `text[] NOT NULL` | default `'{member}'` (existing rows read `'{admin,member}'`, R2); `ck_memberships_roles`: subset of `{admin, approver, member, publisher, reviewer}`, contains `member`, no NULL element; stored distinct and sorted by the guard |

**Invariants**

- Every membership holds `member` (the baseline; FR-002).
- Every organization with at least one membership has at least one membership holding `admin`. This holds at the application under the per-organization lock and in the database guard (FR-010, FR-013a). Exception: an account deletion cascade (no product path; research R4).
- Only an `admin` of the organization changes `roles`, removes another member or adds another account. A member may delete only its own row (leave).
- A non-admin never holds an open invitation that it created (Q2; research R5).

**Lifecycle of a membership's roles**

```text
create organization ──► {admin, member}
redeem invitation   ──► {member}
upgrade (existing)  ──► {admin, member}        (schema default, no data migration)
admin edits roles   ──► any valid set ∋ member  (refused if it removes the last admin;
                                                 losing admin revokes the account's open invitations)
leave / removed     ──► row deleted             (refused if it removes the last admin)
```

## Database objects added or changed

| Object | Kind | Notes |
|---|---|---|
| `gozleme.account_is_admin(uuid)` | SQL function, **not** definer | reads the caller's own membership through `memberships_select`; `PUBLIC` revoked, granted to `gozleme_app` |
| `gozleme.lock_organization_members(uuid)` | SQL function, not definer | `pg_advisory_xact_lock` keyed on the organization; `PUBLIC` revoked, granted to `gozleme_app` |
| `gozleme.memberships_guard()` | plpgsql trigger function, not definer | fixed `search_path`, `PUBLIC` revoked, raises `42501` (research R4) |
| `memberships_guard` | `BEFORE INSERT OR UPDATE OR DELETE` row trigger | |
| `organizations_update` | policy (altered) | member → admin |
| `invitations_insert`, `invitations_delete` | policies (altered) | member → admin |
| `invitations_update` | policy (altered) | member branch → admin; code-holder branch unchanged |
| `memberships_insert` | policy (altered) | creator branch `roles <@ {admin,member}`; invitation branch `roles = {member}` |
| `memberships_update` | policy (altered) | own row → rows of the caller's organizations (the guard narrows it) |
| `memberships_select`, `memberships_delete`, `organizations_select/insert`, `invitations_select` | unchanged | reads stay member-level (application hides admin-only lists) |

`Down` restores the GOZ-0003 policies, drops the trigger, functions and constraint, and drops the column.

## Application model

- `Membership.Roles: string[]`. It is always written explicitly: a creator gets `["admin","member"]` and an invitee `["member"]`.
- `OrganizationRole` constants: `admin`, `approver`, `member`, `publisher`, `reviewer`.
- `OrgAction` constants with a resource type (research R7), and `OrganizationPermissions.For(roles) → IReadOnlySet<string>`.
- Responses: `OrganizationResponse` + `Roles`, `Permissions` (only on `GET /organizations/{id}`); `MemberResponse` + `Roles`; new `RolesRequest(string[]? Roles)`.
- `OrganizationError.LastAdmin = "last_admin"`, `OrganizationError.Forbidden = "forbidden"`.
