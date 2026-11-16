# Implementation Plan: Organizations and Membership

**Branch**: `feature/goz-0002-organizations` | **Date**: 2026-11-15 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/002-organizations/spec.md`

## Summary

Add organizations (permanent UUIDv7 ID plus an editable display name that need not be unique)
and role-neutral membership to the cumulative stack as a new `Organizations/` API module on the
existing `GozlemeDbContext`.

- **Tables**: three, each with an explicit tenant key column (`organizations`, `memberships`,
  `invitations`), added in one additive migration.
- **Joining**: only through single-use, 160-bit invitation codes. Only a SHA-256 hash of each code
  is stored, and codes are revocable and expire after 7 days by default.
- **Redemption**: atomic (row lock), with one generic failure response and a per-account rate limit.
- **Interim powers**: any member renames, invites, revokes, removes any other member, or leaves.
  A lock on the organization row protects the last member under concurrent requests.
- **Active context**: a `selected_at` preference on the caller's membership rows. It is never a
  session claim, and every organization-scoped request re-checks membership in the database.
  Non-member, nonexistent and malformed IDs answer an identical empty 404.
- **Web tier**: `/organizations`, `/organizations/[id]` and `/join` pages, a header switcher, and
  same-origin-checked route handlers.
- **Telemetry and audit**: `gozleme.org.*` spans and a bounded counter. Organizations appear only as
  opaque IDs, and security audit events carry opaque IDs only.
- **Seed and demo**: the seed adds carol and a fixed-ID demo organization. A new `goz-0002`
  Playwright project extends the cumulative demo.
- **Superseded GOZ-0000 boundary assertions**: these are narrowed, not removed.
- **Not built**: roles, ownership, delegation, deletion and email.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK via `global.json`); TypeScript on Node 24; Python 3 for governance scripts

**Primary Dependencies**: Existing only: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql, ASP.NET Core rate limiting, OpenTelemetry .NET, Next.js + React, Playwright + axe. **No new packages** (`System.Security.Cryptography` covers codes and hashes).

**Storage**: PostgreSQL 17, schema `gozleme`. New tables `organizations`, `memberships` and `invitations` via migration `20261116000000_Organizations` ([data-model.md](./data-model.md))

**Testing**: xUnit v3 + Testcontainers. Integration tests cover organization CRUD and isolation parity, invitations (single-use, revoke, expiry, generic failure, rate limit, concurrency), the last-member guard (concurrency), removal taking effect on the next request, CSRF, the outage 503, the boundary (no delegation, table inventory, endpoint authorization), telemetry without secrets, and the seed. Unit tests cover code normalization and display-name validation. Vitest covers route handlers (same-origin, forwarding) and components. Playwright runs the cumulative `goz-0002` project with axe.

**Target Platform**: Linux containers (Docker Compose locally; Kubernetes via Helm)

**Project Type**: Web application (API + Next.js BFF) in the existing monorepo

**Performance Goals**: SC-001, each organization action < 1 s locally. At most 2 indexed queries for the membership check per organization-scoped request.

**Constraints**: Offline; no email or external services. No secrets, emails, display names or codes in telemetry, logs, URLs or screenshots. The API runs as `gozleme_app` (DML only). The migration is additive (the GOZ-0001 release keeps running during pre-upgrade). No RLS yet: isolation in this slice is application-layer only and is labelled as such (GOZ-0003 owns RLS).

**Scale/Scope**: MVP; tens of organizations per account and hundreds of members per organization (no paging in this slice). 11 API endpoints (10 under `/organizations` plus `/invitations/redeem`), 3 pages, 1 header component and 8 route handlers.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | Organization and membership management only; no traffic brokering | PASS |
| II. Portable baseline | Existing stack only; no new dependencies; Compose stays offline | PASS |
| III. Permanent identity | Organization IDs are permanent opaque UUIDs that are never reused; renaming never changes them. No publisher or namespace semantics are claimed (GOZ-0005) | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | Application authorization checks database membership on every request, deny by default, with existence-hiding 404s and negative tests. Tables carry tenant keys for RLS. **RLS itself is GOZ-0003**, the next spec, by approved roadmap order (GOZ-0000-IF-03). It is recorded under Complexity Tracking and never claimed. No secrets in telemetry; no add-by-email lookup | PASS (scheduled RLS gap, justified below) |
| V. Contracts and bounded slices | Organizations API v1 contract in `contracts/`. Native Spec Kit artifacts. No roles, delegation, deletion, handles or email | PASS |
| VI. Demo-first | Cumulative Playwright project `goz-0002`, deterministic seed separate from migrations, real screenshots, negative and boundary tests; earlier suites kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Planned through `drive-speckit` | PASS |
| IX/X. Progress and decisions | `progress.yaml` and the registry are updated; the four maintainer decisions are recorded verbatim in intent | PASS |

Post-design re-check (after Phase 1): unchanged, **PASS**. The RLS gap below is the only exception.

## Project Structure

### Documentation (this feature)

```text
docs/specs/002-organizations/
├── spec.md
├── plan.md               # this file
├── research.md           # R1–R14 decisions
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── organizations-api.openapi.yaml   # API ↔ web contract (v1)
│   └── web-routes.md                    # pages, route handlers, browser flows, seed changes
├── checklists/requirements.md
├── progress.yaml
├── evidence/                            # demo-run.json, screenshots/ (created by the demo)
├── reviews/                             # impact analysis (post-convergence)
└── tasks.md                             # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── GozlemeApp.cs                       # + AddGozlemeOrganizations(), api.MapOrganizationEndpoints()
├── Organizations/                      # new module
│   ├── Organization.cs  Membership.cs  Invitation.cs              # entities
│   ├── OrganizationOptions.cs                                     # Gozleme:Organizations:* (expiry, redeem limit)
│   ├── OrganizationServiceCollectionExtensions.cs                 # options + redeem rate-limit policy
│   ├── OrganizationContracts.cs                                   # request/response records
│   ├── OrganizationEndpoints.cs                                   # /api/v1/organizations/*, /api/v1/invitations/redeem
│   ├── OrganizationAccess.cs                                      # membership lookup → identical 404
│   ├── InvitationCodes.cs                                         # generate / normalize / hash
│   ├── DisplayNames.cs                                            # validation
│   └── OrganizationTelemetry.cs                                   # spans, counter, bounded categories
├── Identity/
│   ├── SecurityAudit.cs                # + org audit events 1101–1108
│   ├── IdentityServiceCollectionExtensions.cs  # rate-limit rejection labels from endpoint metadata
│   └── OperatorCommands.cs             # seed: + carol, + Seeded Demo Org
└── Persistence/
    ├── GozlemeDbContext.cs             # + Organizations, Memberships, Invitations
    └── Migrations/20261116000000_Organizations.cs (+ Designer, snapshot)
tests/Gozleme.Api.IntegrationTests/
├── Infrastructure/OrgClient.cs         # helpers over AuthClient
├── OrganizationTests.cs  InvitationTests.cs  MembershipTests.cs
├── OrganizationIsolationTests.cs       # parity 404s, tampered context, CSRF
├── OrganizationBoundaryTests.cs        # no delegation
├── OrganizationTelemetryTests.cs
├── BootstrapBoundaryTests.cs (narrowed)  DatabaseOutageTests.cs (+ org)  OperatorCommandTests.cs (+ seed)
tests/Gozleme.Api.Tests/OrganizationPolicyTests.cs (invitation codes and display names; GOZ-0002-IF-17)
web/src/
├── lib/organizations.ts                # server-side fetchers for pages (session-forwarding)
├── app/organizations/page.tsx  app/organizations/[organizationId]/page.tsx  app/join/page.tsx
├── app/page.tsx                        # + signed-in header
├── components/{SignedInHeader,OrganizationSwitcher,CreateOrganizationForm,RenameOrganizationForm,
│               MemberList,InvitationPanel,RedeemInvitationForm}.tsx (+ tests)
├── app/api/organizations/**/route.ts  app/api/invitations/redeem/route.ts  (+ routes.test.ts)
└── telemetry/ui.ts                     # + org flows
e2e/
├── playwright.config.ts                # + goz-0002 project (depends on goz-0001)
└── tests/{organizations-demo.spec.ts, evidence.ts (+ specDirs, carol), bootstrap-demo.spec.ts (narrowed)}
docs/engineering/local-development.md   # organizations walkthrough, seed fixtures
```

**Structure Decision**: Keep the single API host project and add one `Organizations/` module folder.
The web tier adds pages, components and route handlers in its existing layout. Nothing is
generalized ahead of need: no repository layer and no permission framework (GOZ-0004 owns that).

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Tenant-owned rows exist one spec before effective RLS (Constitution IV) | The approved roadmap orders GOZ-0002 (tenants) before GOZ-0003 (RLS on those tenants). RLS needs tables and a membership model to protect. The prompt (GOZ-0000-IF-03) directs application-layer authorization now and tables shaped for RLS | Building RLS here would pull GOZ-0003's scope (transaction-scoped tenant context, privilege boundaries) into this slice and break CR-13 bounding. Mitigations: deny-by-default membership checks on every request, existence-hiding negative tests, explicit tenant keys, and a documented "application-layer only" claim. GOZ-0003 is next on the roadmap |
