# Implementation Plan: Scoped RBAC Permission Foundation

**Branch**: `feature/goz-0004-scoped-rbac` | **Date**: 2026-11-16 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/004-scoped-rbac/spec.md`

## Summary

Replace GOZ-0002's interim role-neutral rule with five built-in organization roles. Enforce them at one
application decision point and, independently, in PostgreSQL.

- **Roles** (Q1, Q4, Q5): `admin`, `approver`, `member`, `publisher` and `reviewer`, held as a set in a new `memberships.roles text[]` column. Every membership holds `member`. Only `admin` adds rights in this slice, and no role implies another (research R1, R7).
- **Upgrade** (option B): a schema default gives every existing membership `{admin,member}`, then the default becomes `{member}`. There is no data migration and no GOZ-0003-IF-03 lift. A creator gets `{admin,member}` and an invitee `{member}`. Verified on PostgreSQL 17 (R2).
- **Decision point**: `OrganizationAccess.AuthorizeAsync` checks membership first, so non-members still get the empty 404, then evaluates (roles, action) → 403 `forbidden`. Roles are read from the database in the request's transaction, so a change takes effect on the next request with no sign-out (R6).
- **Contract**: `docs/architecture/contracts/organization-permissions.md` lists ten actions on three resource types and the role table. Endpoints declare their action, and a test pins both the endpoint metadata and the document (R6, R7).
- **Database layer** (Q3 = B): admin-only policies on `organizations` and `invitations` through a **non-definer** `account_is_admin()`, plus a `memberships_guard` trigger. The guard covers role changes, removals, adding another account, immutable identity columns and the last admin, serialized per organization. No new privileged helper is needed (verified, R3–R5).
- **Invitations** (Q2): admin-only. Losing `admin` revokes the account's open invitations in the same transaction.
- **Product**: role badges for every member, a role editor for admins, permission-aware controls, a new `PUT …/members/{userId}/roles` endpoint and its web route (R9, R10).
- **Telemetry and audit**: `gozleme.authz.decision` spans and the `gozleme.authz.decisions` counter; audit 1301 (denied) and 1302 (roles changed) (R8).
- **Demo**: Playwright project `goz-0004` with denied → granted → permitted → revoked → denied, the last-admin refusal, revocation on demotion, database refusals, the policy-evaluator boundary and telemetry (quickstart).

## Technical Context

**Language/Version**: C# on .NET 10; SQL (PostgreSQL 17, PL/pgSQL); TypeScript (Next.js web tier, Node 24 e2e); Python 3 governance scripts

**Primary Dependencies**: Existing only: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql (native `text[]` ↔ `string[]`), OpenTelemetry .NET, Next.js, Vitest, Playwright, xUnit v3 + Testcontainers. No new package or extension.

**Storage**: PostgreSQL 17. Migration `20261118000000_ScopedRbac`:
- one column, one check constraint;
- two non-definer functions and one trigger function plus its trigger;
- seven altered policies.

There is no new table, no data migration, and no new role or volume reset. `Down` reverses it
([data-model.md](./data-model.md)).

**Testing**:
- New integration suites: `RbacIsolationTests` (R3 matrix as `gozleme_app` and the owner login, cascades, concurrency), `RoleAssignmentTests` (API role × action matrix, next-request effect, last admin under concurrency, revocation on demotion, non-member 404s), `OrganizationPermissionContractTests` (FR-006), `RbacMigrationTests` (a pre-existing membership reads `{admin,member}`; no `UPDATE` in the migration SQL), `RbacTelemetryTests` (spans, counter, audit, no private values).
- Unit tests: `OrganizationPermissionsTests`.
- Web (Vitest): `MemberList` role editor, permission-hidden controls, 403/409 messages, the new route handler's same-origin check.
- Updated suites per research R12.
- Playwright project `goz-0004`.

**Target Platform**: Linux containers (Compose locally; Kubernetes via Helm). The Helm `pre-upgrade` migration is DDL-only.

**Project Type**: Web application (API + Next.js BFF) in the existing monorepo.

**Performance Goals**: The organization pages stay under 1 s (GOZ-0003 SC-005). The authorization check reuses the membership row that is already read, so it adds no query. `account_is_admin()` is a single primary-key lookup per policy evaluation. The advisory lock is taken only on membership-changing requests.

**Constraints**:
- Offline, with no new services.
- Membership check before permission (404 before 403).
- Roles never come from session claims.
- No security-stamp rotation.
- Lock order advisory → organization → invitation.
- No `SECURITY DEFINER` additions.
- `PUBLIC` execute revoked on every new function.
- No names, emails, codes, contexts or account ids as metric labels.
- Earlier demos keep passing, with the deliberate boundary narrowing in R12.

**Scale/Scope**:
- API: 1 migration, 1 new endpoint, 10 handlers moved to `AuthorizeAsync`, 1 permissions module, telemetry and audit additions, seed changes.
- Web: 1 new route handler, 3 components changed (`MemberList`, the organization page, `InvitationPanel` visibility).
- e2e: 1 Playwright suite.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | Authorization for existing organization operations only | PASS |
| II. Portable baseline | No new service, package, extension or configuration. Compose stays offline. Helm upgrade is DDL-only | PASS |
| III. Permanent identity | Roles are assigned by permanent opaque account id only, never by email (GOZ-0002-IF-14) | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | The membership check stays first, with identical 404s. Roles are read from the database per request. RLS stays forced, and the role rules are a second independent layer, tested as the app and owner logins. No `BYPASSRLS`, no new definer, no new role. The limits (SQL inside one's own valid context; the account-deletion cascade) are documented | PASS |
| V. Contracts and bounded slices | `docs/architecture/contracts/organization-permissions.md` (pinned by test) and an OpenAPI delta. No policy evaluator, custom roles or grants (GOZ-0018); publisher and approval actions deferred (GOZ-0005, GOZ-0021) | PASS |
| VI. Demo-first | Cumulative `goz-0004` browser demo with screenshots, database evidence, Jaeger spans and a no-secret scan; earlier suites kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Through `drive-speckit` | PASS |
| IX/X. Progress and decisions | Q1–Q5 and the option-B rule are recorded in `progress.yaml` and the spec. The agent design choices (`member` baseline, FR-013a) are disclosed | PASS |

Post-design re-check (after Phase 1): **PASS**. The Q3 = B design needs no addition to
`TableClassification.DefinerHelpers`, so no security-review escalation is triggered (research R3).

## Project Structure

### Documentation (this feature)

```text
docs/specs/004-scoped-rbac/
├── spec.md
├── plan.md                    # this file
├── research.md                # R1–R13 (R2–R5 verified on PostgreSQL 17)
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── permissions.md         # action/resource contract + role table (pinned by test)
│   └── rbac-api.openapi.yaml  # delta over the GOZ-0002 organizations contract
├── checklists/requirements.md
├── progress.yaml
├── evidence/                  # demo-run.json, rbac-evidence.json, screenshots/ (created by the demo)
├── reviews/                   # impact analysis (post-convergence)
└── tasks.md                   # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── Organizations/
│   ├── OrganizationPermissions.cs        # new: OrganizationRole, OrgAction (+ resource type), role table, OrgActionAttribute
│   ├── OrganizationAccess.cs             # + AuthorizeAsync (membership → decision → span/metric/audit)
│   ├── OrganizationEndpoints.cs          # every org-scoped handler authorizes; + PUT members/{userId}/roles;
│   │                                     # advisory lock first in membership changes; last_admin; revoke on demotion
│   ├── OrganizationContracts.cs          # + Roles/Permissions on responses, RolesRequest, LastAdmin/Forbidden
│   ├── OrganizationTelemetry.cs          # + member_roles, forbidden, last_admin
│   ├── AuthorizationTelemetry.cs         # new: gozleme.authz.decision span, gozleme.authz.decisions counter
│   └── Membership.cs                     # + Roles
├── Identity/
│   ├── SecurityAudit.cs                  # + 1301 authorization denied, 1302 member roles changed
│   └── OperatorCommands.cs               # seed: explicit roles, acts as a seeded admin
└── Persistence/
    ├── GozlemeDbContext.cs               # roles column mapping
    └── Migrations/20261118000000_ScopedRbac.cs (+ Designer, snapshot)
web/src/
├── app/organizations/[organizationId]/page.tsx              # permission-aware controls
├── app/api/organizations/[organizationId]/members/[userId]/roles/route.ts   # new PUT
├── components/{MemberList,InvitationPanel,RenameOrganizationForm}.tsx       # roles, editor, 403/409 handling
└── lib/organizations.ts                                     # types: roles, permissions
tests/Gozleme.Api.IntegrationTests/
├── RbacIsolationTests.cs  RoleAssignmentTests.cs  OrganizationPermissionContractTests.cs
├── RbacMigrationTests.cs  RbacTelemetryTests.cs
├── Infrastructure/{TwoTenants,OrgClient}.cs                 # roles in fixtures; roles client
└── OrganizationBoundaryTests.cs MembershipTests.cs InvitationTests.cs TenantSeedTests.cs  (deliberately narrowed, R12)
tests/Gozleme.Api.Tests/OrganizationPermissionsTests.cs
e2e/
├── playwright.config.ts                  # + goz-0004 (depends on goz-0003)
└── tests/{rbac-demo.spec.ts, evidence.ts (+ specDirs), organizations-demo.spec.ts (boundary narrowed),
          bootstrap-demo.spec.ts (LATEST_MIGRATION)}
```

**Structure Decision**: Keep RBAC inside the existing `Organizations/` module. Roles are a property of
membership, and the decision point evolves `OrganizationAccess`, as GOZ-0002-IF-03 directs. A separate
`Authorization/` module would be premature before GOZ-0018 defines the general policy evaluator.

## Complexity Tracking

No constitution violations. Deliberate design points:

| Design point | Why needed | Simpler alternative rejected because |
|---|---|---|
| Role checks for `memberships` in a trigger, not policies | A policy cannot read its own table (recursion), and Q3 = B requires database enforcement | A new definer helper extends the pinned security list. Views are forbidden in `gozleme` |
| Advisory per-organization lock | The organization row lock now needs the admin-only update policy, so a non-admin could not take it. The last-admin check needs serialization for every caller | A row lock on all admin memberships deadlocks between concurrent demotions |
| Cascade bypass `rls_internal() AND pg_trigger_depth() > 1` | GOZ-0003 requires account deletion to cascade under FORCE | Refusing cascades breaks `Foreign_key_cascades_still_run_under_forced_rls`. A broader `rls_internal()` bypass would release the owner login with the flag |
