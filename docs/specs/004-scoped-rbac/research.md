# Research: Scoped RBAC Permission Foundation

Decisions for `plan.md`. Each entry gives the decision, the rationale and the alternatives considered.
Maintainer decisions Q1–Q5 and the option-B initial-role rule are inputs here, not open questions
(`spec.md` Clarifications; `progress.yaml`).

## R1 — Role storage: a `text[]` column on `memberships`

**Decision**: `gozleme.memberships.roles text[] NOT NULL`, with a check constraint:
`roles <@ '{admin,approver,member,publisher,reviewer}'` AND `'member' = ANY (roles)` AND no NULL
elements. The guard trigger (R4) stores the set sorted and without duplicates, so equality comparisons
in policies are stable. EF maps it as `string[] Roles` (Npgsql native array mapping; no new package).

**Rationale**: Q5 = B (a set held in the membership row). A column keeps the option-B rule exact: a
schema default fills existing rows. The check constraint makes an unknown role or a missing `member`
baseline impossible at the database, independently of the application.

**Alternatives rejected**: a PostgreSQL enum array (needs Npgsql enum mapping and a type migration for
every later role; a text check constraint is simpler to extend); a role table (Q5 = C, withdrawn; a
schema default cannot create its rows); a bitmask (opaque in evidence and SQL review).

## R2 — Initial roles by schema default (option B), verified

**Decision**: In one migration:
1. `ADD COLUMN roles text[] NOT NULL DEFAULT '{admin,member}'`. PostgreSQL 11+ stores a constant default as catalog metadata, so every existing row reads it with no table rewrite and no row access. FORCE row-level security does not apply to DDL.
2. `ALTER COLUMN roles SET DEFAULT '{member}'` for future rows.
3. Then the constraint, helpers, policy changes and guard trigger (R3, R4).

The application always writes `Roles` explicitly: `{admin,member}` for a creator and `{member}` for an
invitee (FR-011). The column default is a backstop only.

**Verified empirically** (2026-11-16, scratch `postgres:17-bookworm` loaded with the dev stack's
`pg_dump --schema-only`, migration applied as `gozleme_migrator` without superuser rights): all
four pre-existing memberships read `{admin,member}`, and no data-changing statement ran. The
GOZ-0003-IF-03 lift is not used.

## R3 — Database enforcement of admin-only writes (Q3 = B) without a new definer helper

**Problem**: A policy on `memberships` cannot read `memberships` to learn the caller's role:
PostgreSQL rejects a policy that references its own table ("infinite recursion detected in policy").
The pinned definer helpers (`TableClassification.DefinerHelpers`) are a security-review item to extend.

**Decision**: A plain, non-definer SQL function `gozleme.account_is_admin(org uuid)`. It reads the
caller's **own** membership row through the existing `memberships_select` policy, whose own-row branch
needs no helper. Its `PUBLIC` execute is revoked and it is granted to `gozleme_app`.
- **Other tables (policies)**: `organizations_update`, `invitations_insert`, the member branch of `invitations_update`, and `invitations_delete` use `account_is_admin(organization_id)` in place of the member check. The invitation code-holder branch is unchanged.
- **`memberships` (trigger)**: the policies stay member-level. `memberships_update` widens from own row to "rows of my organizations", so an admin's change to another member actually applies (GOZ-0003-IF-05). The `BEFORE` guard trigger (R4) does the role-dependent checks, because a trigger's queries are separate statements and do not recurse.
- **Redemption**: the `memberships_insert` creator branch requires `roles <@ {admin,member}`. The invitation branch requires `roles = {member}`. The "member of the organization" branch stays, and the guard requires admin when the inserted account is not the caller (this is the path `seed` uses).

**Verified empirically** (same scratch database, as `gozleme_app` with signed contexts; script in the
session scratchpad, to be turned into `RbacIsolationTests`):

| Attempt | Result |
|---|---|
| member raises own roles | refused `42501` (guard) |
| member changes another's roles / removes another / adds another account | refused `42501` |
| member renames the organization | `UPDATE 0` (policy hides the row) |
| member creates an invitation | refused (`WITH CHECK`) |
| member leaves / updates own `selected_at` | allowed |
| member updates another's `selected_at`, or its own `joined_via` | refused |
| member still reads the organization and all members | allowed |
| admin renames / invites / adds an account with roles / removes a member | allowed |
| admin moves a membership to another organization | refused |
| admin of A changes a role, renames, or removes a member in B | `UPDATE 0` / `DELETE 0` |
| sole admin demotes self or leaves | refused (`last admin`) |
| unknown role, or roles without `member` | check-constraint violation |
| superuser, or owner login without context, changes roles | refused / `UPDATE 0` |
| owner login with the `gozleme.rls_helper` flag changes roles or deletes a membership | refused (guard binds) |

**Alternatives rejected**: a new definer helper `account_admin_organizations()` (works, but extends the
pinned definer list, which would go back to the maintainer, and it is unnecessary); keeping the
database member-level (Q3 = B rejects that); a security-barrier view (views are forbidden in `gozleme`
by the catalog check, GOZ-0003-IF-12).

## R4 — `memberships_guard` trigger

**Decision**: `BEFORE INSERT OR UPDATE OR DELETE … FOR EACH ROW`, plain `plpgsql`, fixed
`search_path`, `PUBLIC` execute revoked, refusals raise `insufficient_privilege` (GOZ-0003-IF-19). In
order:
1. **Foreign-key cascades pass**: `rls_internal() AND pg_trigger_depth() > 1`. RI actions run as the table owner one trigger level down, so an account deletion still removes its memberships and an invitation deletion still nulls `invitation_id`. This is required by `RlsIsolationTests.Foreign_key_cascades_still_run_under_forced_rls` (GOZ-0003 US2-AC6). An owner login with the flag set directly is depth 1 and is still bound. Verified: app-role and superuser account deletion cascade; owner-with-flag direct delete refused.
2. Normalize `roles` (distinct, sorted).
3. **Immutable**: `organization_id`, `user_id`, `joined_at`, `joined_via`; `invitation_id` may only become NULL; `selected_at` may change only on the caller's own row (closes GOZ-0003-IF-19).
4. Unchanged roles on update, or the caller inserting its own row, return here (the insert policy fixes those roles).
5. **Serialize, then authorize**: `gozleme.lock_organization_members(org)` (R5), then `account_is_admin(org)` is required unless the caller deletes its own row (leaving). Locking before reading authority closes a time-of-check gap found in the prototype: two admins demoting each other concurrently. Verified: one succeeds, and the other is refused because, after the lock, it is no longer admin.
6. **Last admin (FR-013a)**: if an admin row loses `admin` or is deleted, at least one other admin must remain. The caller is still a member at `BEFORE` time, so it can see the rows. An `AFTER` trigger would not work, because a departing caller can no longer see its organization.

**Consequence documented**: deleting an **account** that is an organization's last admin still cascades,
because there is no account-deletion product path (the app role's `DELETE` on `users` is GOZ-0001/0003
posture). A future account-deletion spec must handle it (impact finding).

## R5 — Lock order and the per-organization membership lock

**Decision**: `gozleme.lock_organization_members(org)` = `pg_advisory_xact_lock(hashtextextended('gozleme.memberships:' || org, 0))`.
It is non-definer, with `PUBLIC` execute revoked and a grant to `gozleme_app`. The application calls
it first in every membership-changing transaction (role change, removal, leave). The guard calls it
again, which is re-entrant. Admin paths then take the existing organization row lock `FOR UPDATE`
before locking invitations. The order is **advisory → organization → invitation**. Redemption takes
organization (`FOR KEY SHARE`, via its helper) → invitation and never the advisory lock, so no cycle
exists (GOZ-0002-IF-20 preserved).

**Why not the organization row lock alone**: with `organizations_update` now admin-only, a non-admin's
`SELECT … FOR UPDATE` on its organization returns zero rows and locks nothing. Row locks need the
`UPDATE` policy. A hash collision between organizations only adds serialization.

**Non-admin departure and invitations**: only admins can create invitations, and a demotion revokes the
account's open invitations (Q2), so a non-admin never holds an open invitation. Its departure skips the
organization lock. The invitation revocation query returns nothing for it, because `FOR UPDATE` filters
by the admin-only update policy. A test asserts the invariant.

## R6 — Authorization decision point and the action/resource contract

**Decision**: `Organizations/OrganizationPermissions.cs` holds the action catalogue (`OrgAction`
constants, each with a resource type) and the built-in role table (`role → actions`).
`OrganizationAccess.AuthorizeAsync(db, organizationId, user, action, ct)` replaces
`FindMembershipAsync` in every organization-scoped handler:
1. Membership check, unchanged: null gives the empty 404.
2. Permitted actions = the union of the caller's roles' actions; allow or deny.
3. Span and metric (R8). On deny, audit 1301 (deferred with `TenantTransaction.AfterCommit`) and `403 {"error":"forbidden"}`.

It accepts only `(roles, action)`. There are no conditions, attributes, per-user grants or policy
documents (FR-016). GOZ-0018 replaces the body behind the same signature.

**Pinning (FR-006)**: each organization-scoped endpoint declares `.WithMetadata(new OrgActionAttribute(OrgAction.X))`.
`OrganizationPermissionContractTests` enumerates `EndpointDataSource` and fails on an
`/organizations/{organizationId}…` endpoint without exactly one action, an action without a role-table
entry, or a role-table row that differs from `contracts/permissions.md`. The test renders the table
from code and compares it with the fenced block in the document.

**Alternatives rejected**: ASP.NET Core authorization policies with requirement handlers (they would
run before the membership check and before the tenant transaction, breaking "404 before 403" and
"read from the database inside the transaction"); session claims (forbidden by GOZ-0001-IF-04).

## R7 — Action catalogue and role table (Q1, Q2, Q4)

| Action | Resource type | Roles |
|---|---|---|
| `organization.read` | organization | every role |
| `organization.select` | organization | every role |
| `organization.rename` | organization | admin |
| `membership.list` | membership | every role |
| `membership.leave` | membership | every role (last-admin rule applies) |
| `membership.remove` | membership | admin |
| `membership.roles.change` | membership | admin |
| `invitation.list` | invitation | admin |
| `invitation.create` | invitation | admin |
| `invitation.revoke` | invitation | admin |

`organization.create` and `invitation.redeem` are account-level (no organization yet) and stay open to
every signed-in account (FR-007). `publisher`, `reviewer` and `approver` hold exactly `member`'s actions
(Q4 = A). No role implies another. Admins may change their own roles, subject to the last-admin rule.

## R8 — Telemetry and audit

- Span `gozleme.authz.decision` (child of the `gozleme.org.*` span) with `gozleme.authz.action`, `gozleme.authz.resource_type` and `gozleme.authz.outcome` ∈ {`allow`, `deny`}. `gozleme.organization.id` is set only after membership is confirmed (GOZ-0002-IF-07). Roles are never on the span; neither are account ids or names.
- Counter `gozleme.authz.decisions` with labels action, resource type and outcome (at most 10 × 2 series).
- `gozleme.org.operations`: new operation `member_roles` and failure categories `forbidden` and `last_admin`.
- Audit range 1301–1310 (`SecurityAudit`): **1301** authorization denied (organization id, account id, action); **1302** member roles changed (organization id, target id, actor id, the sorted role lists before and after; the role names are a fixed vocabulary). A demotion's revoked invitations reuse **1104**.

## R9 — HTTP contract changes (additive, plus deliberate narrowing)

- `GET /organizations/{id}` adds `roles` (caller's) and `permissions` (caller's allowed actions), which the UI uses to show controls.
- `GET …/members` adds `roles` to each member.
- **New** `PUT …/members/{userId}/roles` with body `{"roles": [...]}`. `member` is added if omitted, and an unknown role gives `400 validation_failed`. Answers `200` (member), `403`, `404` (non-member caller, unknown or malformed target) or `409 last_admin`. Assigning an identical set is a no-op without an audit event.
- Admin-only operations answer `403 forbidden` to other members: rename, invitation list, create and revoke, and removal of another member.
- Conflicts: `last_member` keeps its GOZ-0002 meaning (sole member leaving). **New** `last_admin` covers any change that would remove the last admin while other members remain.
- Contract: `contracts/rbac-api.openapi.yaml` (delta over `docs/specs/002-organizations/contracts/organizations-api.openapi.yaml`, which stays historical).

## R10 — Web tier

- `MemberList` shows role badges for everyone. For admins it adds an "Edit roles" form per member: four checkboxes (`admin`, `publisher`, `reviewer`, `approver`; `member` shown as fixed) and Save. No combobox is added.
- The organization page hides `RenameOrganizationForm`, `InvitationPanel` and the Remove buttons unless `permissions` allows them. Leave stays for everyone.
- On `403`, components show "You no longer have permission to do that." and call `router.refresh()`. On `409 last_admin`, "An organization needs at least one admin."
- New route handler `web/src/app/api/organizations/[organizationId]/members/[userId]/roles/route.ts` (`PUT`, `isSameOrigin()` then `forwardToApi()`, path built with `segment()`).

## R11 — Seed and fixtures

`seed` restores on every run: **Seeded Demo Org** with alice `{admin,member}` and carol `{member}`;
**Seeded Partner Org** with dave `{admin,member}`. It acts through the signed context of the first
seeded member who currently holds `admin` (alice, else carol). If none does, it fails with "run make
reset". Order: give alice `admin` (acting as carol if needed), then set carol `{member}`. It never acts
with owner rights (GOZ-0003-IF-10). `TwoTenants` test fixtures gain explicit roles.

## R12 — Existing tests and demo

Deliberate changes, each named in tasks:
- `OrganizationBoundaryTests`: the column pin gains `roles`. The forbidden-word scan allows the built-in role vocabulary on `memberships.roles` only. `Delegation_routes_do_not_exist` keeps `/transfer`, `/owner`, `/admins`, `DELETE` organization and `PATCH /members/{user}`, and drops `PUT /members/{user}/role`, which is replaced by `/roles`. `Extra_privilege_fields_are_ignored_and_the_creator_has_no_special_standing` becomes "the creator holds admin through a role; extra privilege fields are ignored".
- `MembershipTests` and `InvitationTests`: the invitees that previously performed admin-only actions are granted `admin` first, or the assertion becomes the 403. The GOZ-0002-IF-04 revocation-on-removal test is kept.
- `e2e/tests/organizations-demo.spec.ts` boundary test: allow role badges and role checkboxes. Still forbid owner, transfer and administrator wording and any second combobox.
- Pins: `LATEST_MIGRATION` → `/_ScopedRbac$/`; `BootstrapBoundaryTests` table list unchanged (no new table).
- `RlsIsolationTests`: add the R3 matrix for both logins (new `RbacIsolationTests`, fixtures `TwoTenants`). `RlsCatalogTests` stay green: no new table, no definer, both new functions and the trigger function have `PUBLIC` revoked.

## R13 — Not built

Custom roles, per-user or per-resource grants, deny rules, conditions, policy documents and versions,
policy authoring UI (GOZ-0018); publisher, review and approval actions (GOZ-0005, GOZ-0021); ownership
transfer, organization deletion, platform administrator, HTTP recovery (R3-08, R3-24, GOZ-0001-IF-05);
rate limits on role changes; paging; account deletion.
