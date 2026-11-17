# GOZ-0004 post-convergence spec impact analysis

**Source spec:** GOZ-0004, Scoped RBAC permission foundation
**Implementation revision:** `7179977` on `feature/goz-0004-scoped-rbac`, after convergence pass 2 (fix `498facd`). drive-speckit reports, at this revision:
- `make test`: 318 .NET and 139 web tests passing;
- `make lint` and `make validate`: clean;
- `make demo`: 41 passed (goz-0000 to goz-0004), recorded in `evidence/demo-run.json` and `evidence/rbac-evidence.json`.

I ran read-only queries, and one reproduction inside a rolled-back transaction (IF-14), against the running Compose stack at this revision.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-16
**Structured findings:** `docs/specs/004-scoped-rbac/reviews/impact-findings.json` (14 findings; validates against `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **Diff:** `git diff main...HEAD` (102 files).
- **Migration:** `src/Gozleme.Api/Persistence/Migrations/20261118000000_ScopedRbac.cs`, read in full: the `roles` column and check, `account_is_admin`, `lock_organization_members`, the altered policies, `memberships_guard`, and `Down`.
- **Model:** `Persistence/GozlemeDbContext.cs` (`RolesCheck`).
- **Application code:**
  - `Organizations/OrganizationPermissions.cs`, `OrganizationAccess.cs` and `AuthorizationTelemetry.cs`;
  - the `OrganizationEndpoints.cs` diff (`SetRolesAsync`, `LockMembersAsync`, `IsLastAdminAsync`, `RevokeOpenInvitationsAsync`, the `OrgActionAttribute` metadata) and `OrganizationTelemetry.cs`;
  - `Identity/SecurityAudit.cs` (1301, 1302) and `Identity/OperatorCommands.cs` (seed).
- **Web:** `app/organizations/[organizationId]/page.tsx`, `lib/organizations.ts`, `lib/client.ts`, and the `MemberList`, `InvitationPanel` and `RenameOrganizationForm` 403 handling.
- **Tests:**
  - `OrganizationPermissionContractTests`, `OrganizationBoundaryTests`, `RoleAssignmentTests` and `RbacIsolationTests`;
  - `RbacMigrationTests`, `RbacTelemetryTests` and `TenantSeedTests`;
  - the changed `MigrationAndRoleTests`, `StatusEndpointTests`, `TenantContextBoundaryTests` and `EndpointAuthorizationTests`;
  - the `Infrastructure/TwoTenants.cs` and `OrgClient.cs` fixtures and `Unit/OrganizationPermissionsTests`.
- **Demo:** `e2e/tests/rbac-demo.spec.ts`, plus the `playwright.config.ts`, `evidence.ts`, `bootstrap-demo.spec.ts` and `organizations-demo.spec.ts` changes.
- **Spec workspace:**
  - spec.md, plan.md and research.md (R1 to R13);
  - `contracts/permissions.md` (moved to `docs/architecture/contracts/organization-permissions.md` under IF-13, option B) and `contracts/rbac-api.openapi.yaml`;
  - progress.yaml, including maintainer decisions Q1 to Q5 and option B.
- **Governance:** `security-invariants.md`, `architecture-baseline.md` (it does not mention RBAC), `.gozleme/spec-registry.yaml` (DAG), and the GOZ-0003 impact analysis as the precedent.
- **Downstream prompts:** all 37 prompts from GOZ-0005 to GOZ-0041.
  - GOZ-0005 to GOZ-0039 depend on GOZ-0004, directly or transitively.
  - GOZ-0040 and GOZ-0041 do not, but they merge onto a `main` that runs GOZ-0004's tests and pins.

## Verified implementation facts that downstream specs consume

| Area | Fact at `7179977` (evidence) |
|---|---|
| Roles | `memberships.roles text[]`, with `ck_memberships_roles`. The roles are a subset of {admin, approver, member, publisher, reviewer}; `member` is always held and NULL is not allowed. The guard stores the set distinct and sorted. Existing rows became `{admin,member}` through a temporary default, and the default is now `{member}`. Creators get `{admin,member}` and redeemers `{member}`. |
| Decision | `OrganizationAccess.AuthorizeAsync` checks membership first (empty 404, no decision recorded), then calls `OrganizationPermissions.Allows(roles, action, resourceType)`. A deny gets `403 {"error":"forbidden"}` and audit 1301 after commit. Role-dependent writes re-check under `lock_organization_members`. |
| Contract | 10 actions over 3 resource types. publisher, reviewer and approver equal member (Q4 = A). `OrganizationPermissionContractTests` pins the rendered table to the contract (now `docs/architecture/contracts/organization-permissions.md`, IF-13), 9 scoped endpoints, the declared set equal to `OrgAction.All`, and 3 resource types. It recognises only routes under `/api/v1/organizations/{organizationId}`. |
| Database | Admin-only `organizations_update` and invitation writes, through the non-definer `account_is_admin`. `memberships_insert` limits role sets. `memberships_guard` makes identity columns immutable, limits `selected_at` to the caller's own row, requires admin for role changes, removals and adding others, and enforces the last-admin rule under the advisory lock. It binds the owner and superusers; foreign-key cascades pass at `pg_trigger_depth() > 1`. Reads stay member-level. No new definer. |
| Locks | Order: advisory → organization → invitation. A non-admin's `FOR UPDATE` on `organizations` and `invitations` locks nothing, because the UPDATE policy hides the rows. |
| Telemetry | Span `gozleme.authz.decision` and counter `gozleme.authz.decisions`, both with {action, resource_type, outcome}. `gozleme.org.operations` gains `forbidden`, `last_admin` and `member_roles`. Audit 1301 and 1302, in the range 1301–1310. |
| Seed and demo | Seed acts as a seeded admin. Seeded Demo Org has alice `{admin,member}` and carol `{member}`; Seeded Partner Org has dave `{admin,member}`, confirmed on the running stack. The `goz-0004` suite restores the seeded state. In `TwoTenants`, carol is an admin of A. |
| Boundary | `OrganizationBoundaryTests.No_policy_or_custom_role_surface_exists` forbids tables matching `(polic\|grant\|role\|permission)` and pins the `Allows` and `AuthorizeAsync` parameters. `Delegation_routes_do_not_exist` keeps `/roles`, `/policies`, `/permissions`, `/grants` and others at 404. |

## Findings

| ID | Sev | Finding | Affected specs | Disposition |
|---|---|---|---|---|
| GOZ-0004-IF-01 | high | Single decision point and pinned action/resource contract: obligations for every new organization-scoped endpoint, web gating on `permissions` | 0005–0039 (0040, 0041 conditional) | applied to prompts |
| GOZ-0004-IF-02 | high | Database-layer posture for later role-gated writes and reads is undecided; Q3 = B covered GOZ-0004's admin writes only | 0005, 0012, 0014, 0018–0021, 0034, 0035, all with role-gated tables | **human-decided 2026-11-16: option A**; applied to prompts |
| GOZ-0004-IF-03 | high | GOZ-0018 replaces the evaluation behind `AuthorizeAsync`, but the database already caps organization-management actions to admins | 0018, 0019, 0020 | applied to prompts |
| GOZ-0004-IF-04 | medium | Boundary pins later specs must narrow deliberately (table names, decision signatures, delegation routes) | 0018, 0019, 0020, 0034, all | applied to prompts |
| GOZ-0004-IF-05 | high | publisher, reviewer and approver have no holders and no backfill; their owners handle role loss and separation of duties | 0005, 0021, 0014, 0034 | applied to prompts |
| GOZ-0004-IF-06 | medium | Row locks under role-restricted UPDATE policies lock nothing; lock order; the invitation invariant behind Q2 | 0005–0039 | applied to prompts |
| GOZ-0004-IF-07 | medium | Authorization telemetry and audit; 1301–1310 is taken, so the next range is 1401 (37 prompts said "from 1301") | 0005–0041 | applied to prompts |
| GOZ-0004-IF-08 | medium | Database enforcement facts; the guard binds the owner; migration pins; superseded GOZ-0002 and GOZ-0003 wording | 0005–0041 | applied to prompts |
| GOZ-0004-IF-09 | medium | Seed, fixtures and demo state after GOZ-0004 | 0005–0041 | applied to prompts |
| GOZ-0004-IF-10 | medium | Known residual: account deletion cascades past the last-member and last-admin rules, and nothing can restore an admin | 0035, 0039, 0040, 0041 | applied to prompts |
| GOZ-0004-IF-11 | medium | Restore and release gate must account for guard triggers | 0039 | applied to prompts |
| GOZ-0004-IF-12 | low | Spec-specific stale references made concrete | 0014, 0018, 0021, 0023, 0034, 0040, 0041 | applied to prompts |
| GOZ-0004-IF-13 | low | The living permission contract sits in GOZ-0004's workspace, pinned by a test, and later specs must edit it | 0004, 0005, 0014, 0018, 0021, 0034 | **human-decided 2026-11-16: option B**; contract moved on the branch; applied to prompts |
| GOZ-0004-IF-14 | low | Defect: any member can set another member's `invitation_id` to NULL in the database (reproduced) | 0004 | **fixed on the branch** (guard refuses any direct `invitation_id` change; `RbacIsolationTests`) |

Totals after the 2026-11-16 decisions: 11 applied to prompts, 2 decided by the maintainer and applied (IF-02 option A, IF-13 option B), and 1 was returned to drive-speckit as a bounded correction (IF-14) and fixed on the branch before the PR. No finding is critical, none changes the DAG, and none changes an outcome, scope, prerequisite or invariant.

## Prompt edits applied (within authority)

**All 37 prompts from GOZ-0005 to GOZ-0041** get one new section, with provenance: `### Scoped RBAC conventions (GOZ-0004-IF-01, IF-04, IF-06, IF-07, IF-08, IF-09, 2026-11-16)`. It covers:
- roles and the decision point;
- the contract obligations for a new action;
- web gating;
- database enforcement and the guard;
- the boundary pins;
- row locks and lock order;
- telemetry and audit (next range 1401);
- migration pins;
- seed, fixtures and demo.

The section explicitly supersedes four pieces of earlier wording: the GOZ-0002-IF-05 "no role column" and `FindMembershipAsync`; the GOZ-0003-IF-09 "from 1301"; the GOZ-0003-IF-01 `/_TenantRls$/` pin; and the GOZ-0003-IF-10 "seeded member". In GOZ-0040 and GOZ-0041 a lead bullet notes that they do not depend on GOZ-0004 and that the contract bullets apply only if they add an organization-scoped endpoint.

**Spec-specific sections,** headed `### Implementation alignment (GOZ-0004-…, 2026-11-16)`:

| Prompt | Findings |
|---|---|
| 0005 | IF-05 (no holders, no backfill, role loss), IF-01 (publisher actions and resource), IF-02 (pointer) |
| 0012 | IF-02 (role-dependent visibility; database reads are member-level) |
| 0014 | IF-12 (supersedes the GOZ-0002-IF-03 interim rule), IF-01, IF-02 (pointer) |
| 0018 | IF-03, IF-04, IF-12 (subject facts), IF-02 (pointer) |
| 0019, 0020 | IF-04 |
| 0021 | IF-05 (separation of duties, role loss, decision-time re-check), IF-12 (supersedes GOZ-0002-IF-15), IF-02 (pointer) |
| 0023 | IF-12 (role changes invalidate cached permits) |
| 0034 | IF-04 (grant tables and `/grants`), IF-12, IF-01, IF-02 (pointer) |
| 0035 | IF-10 |
| 0039 | IF-10, IF-11 |
| 0040 | IF-12 (no role assignment by address exists), IF-10 |
| 0041 | IF-12, IF-10 |

**Roadmap Steward:**
- Bump `promptRevision` from 5 to 6 for GOZ-0005 to GOZ-0039, and from 3 to 4 for GOZ-0040 and GOZ-0041.
- Record GOZ-0004-IF-01 to IF-14 on the GOZ-0004 registry entry, with the `impactAnalysis` gate and the evidence paths.
- Record the two maintainer decisions (IF-02 option A and IF-13 option B, 2026-11-16) as decided.

I did not edit the registry.

Earlier provenance bullets are left as written, and the new sections supersede them explicitly, as in the GOZ-0002 and GOZ-0003 precedents. A script applied the edits idempotently: it skips any prompt that already contains `GOZ-0004-IF-`. `make validate` passes after the edits (see below).

## Maintainer decisions (2026-11-16)

The maintainer replied verbatim "1: A, 2: B".

- **IF-02: option A.** Every later role-gated **write** is also enforced in the database, mirroring Q3 = B, through a non-definer SQL function `gozleme.account_has_role(org, role)` that generalises `account_is_admin`. Reads stay member-level in the database, and GOZ-0018 conditions stay application-only.
  - In all 37 prompts, the "open; ask the maintainer" bullet in the common section was replaced in place with this rule. The spec-specific IF-02 bullets in 0005, 0012, 0014, 0018, 0021 and 0034 were resolved.
  - Left to consuming specs: the first consumer (expected GOZ-0005) adds the function and decides whether `account_is_admin` is re-expressed through it. Each spec designs its own per-table policy shape. GOZ-0021 decides whether its row-comparing separation-of-duties rules use a guard trigger or stay in the application.
- **IF-13: option B.** The coordinator moved the contract with `git mv`, adding a provenance note, to `docs/architecture/contracts/organization-permissions.md`. `OrganizationPermissionContractTests`, the code, tests, the web and the GOZ-0004 spec artifacts now reference the new path.
  - The prompts call it a living contract that each spec edits in its own slice.
- Each of the 37 prompts ends with a `Maintainer decisions applied (GOZ-0004-IF-02, IF-13, 2026-11-16)` provenance note.
- The coordinator had already updated the prompts' `invitation_id` wording for IF-14 ("fixed before merge").

## Maintainer decisions requested (2026-11-16; now decided, kept for the record)

1. **GOZ-0004-IF-02: database posture for later role-gated actions.** Decide before the GOZ-0005 plan.
   - **A.** Every later role-gated *write* is also enforced in PostgreSQL, through a non-definer generalisation of `account_is_admin` such as `gozleme.account_has_role(org, role)`, with a per-role isolation matrix. GOZ-0018 policy conditions stay in the application only. Reads stay member-level.
   - **B.** The database stays member-level for later tables, and role and policy decisions are application-only. Each PR states this as a known limitation. Only GOZ-0004's admin writes are mirrored.
   - **C.** Decide per spec, in each spec's clarification.

   Consequences:
   - A strengthens defence in depth, but every new action needs a policy change and a test.
   - B is simpler, but a defect in the application's decision is no longer caught by the database for those tables.
   - The prompts currently say "open; ask; do not invent".
2. **GOZ-0004-IF-13: where the living permission contract lives.** Decide before the GOZ-0005 plan. This does not block the GOZ-0004 PR unless option B is chosen.
   - **A.** Later specs edit `docs/specs/004-scoped-rbac/contracts/permissions.md` in place, as its text says, and record the delta in their own contracts.
   - **B.** drive-speckit moves it now, in this PR, to a living location such as `docs/architecture/contracts/organization-permissions.md`, updating the test path and references.
   - **C.** Each spec keeps its own full copy, and the test reads the newest copy. Not recommended, because the copies drift.

Points I recorded in the prompts **for the consuming spec's clarification** and did not decide:
- whether creators should also start with `publisher` (or whether to backfill it), which would change GOZ-0004's option-B initial-role rule (GOZ-0005);
- the consequences of losing a role for in-flight work (GOZ-0005, GOZ-0021);
- whether role combinations or self-grants should be forbidden for separation of duties (GOZ-0021);
- which role holds the overlay actions (GOZ-0014) and the sharing-grant actions (GOZ-0034);
- whether any policy may grant the admin-only organization-management actions, which would change the Q3 = B database guarantee (GOZ-0018);
- whether an operator command to restore an admin is in scope (GOZ-0039).

## Implementation defects and corrections for drive-speckit

**GOZ-0004-IF-14 (low).** `memberships_guard` refuses an `invitation_id` change only when the new value is not NULL. An update with unchanged roles returns before the admin check, and `memberships_update` now admits every row of the caller's organizations. Any member can therefore null a co-member's (or its own) `invitation_id` at the database layer.

Reproduction on the running stack at `7179977`:
- inside one rolled-back transaction, as superuser in replica mode, alice's row got a placeholder `invitation_id`;
- then, as `gozleme_app` in carol's signed context (`account_is_admin` = false), `UPDATE … SET invitation_id = NULL` on alice's row changed **1** row;
- a follow-up read showed nothing persisted.

The `ON DELETE SET NULL` cascade already passes through the `rls_internal() AND pg_trigger_depth() > 1` branch, so the depth-1 NULL allowance is not needed. No application path exposes this, and `invitations.used_by` still records the redemption.

Suggested bounded correction:
1. Refuse any `invitation_id` change at depth 1.
2. Add an `RbacIsolationTests` case for a member nulling its own and a co-member's `invitation_id`.
3. Update research.md R4 step 3.
4. Re-run convergence and this analysis.

Alternatively, the maintainer may accept it as a documented residual.

## Not asserted

- I did not run `make test` or `make demo` myself. The pass counts are drive-speckit's, at `7179977`.
- The restore behaviour in IF-11 (post-data trigger creation; a data-only restore fires the guards) is documented PostgreSQL and `pg_dump` behaviour, inferred from the guard code. It was not reproduced.
- The account-deletion cascade in IF-10 was verified by the GOZ-0004 plan (research R4), not re-run here.
- The row-lock behaviour in IF-06 comes from research R5 and the `LockMembersAsync` code path; I did not reproduce it separately.
