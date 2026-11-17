# Implementation Plan: Publisher Identities and Canonical Capability IDs

**Branch**: `feature/goz-0005-publisher-identities` | **Date**: 2026-11-16 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/005-publisher-identities/spec.md`

## Summary

Give `publisher` its first actions. Organizations register globally unique publisher handles. Publisher
members create capabilities with permanent, origin-qualified canonical IDs and permanently reserved
aliases. The operator can reserve handles and reclaim squatted ones without touching tenant rows.

- **Origin** (Q1): `Gozleme:Registry:Origin` is recorded once by the migrator in `registry_origin`, a new `InstanceReference` table that `gozleme_app` may only `SELECT`. A missing, invalid or mismatched value refuses both `migrate` and API start. A blocking start check keeps the GOZ-0000 outage behaviour (research R6).
- **Identities** (Q2, Q4): tenant-owned `publishers` and `capabilities` (forced RLS) with UUIDv7 IDs, rendered as `gz://<origin>/pub/<id>` and `gz://<origin>/cap/<id>`. Only display names change, and guard triggers refuse every other change for every login (R5).
- **Namespace** (Q3, Q10): a new `InstanceClaims` class.
  - `handle_claims` is one table for held claims and operator reservations. Its partial unique index is the only handle-uniqueness rule (R2).
  - `alias_reservations` is insert-only for the app, has no `SELECT`, and is filled by a trigger in the same statement as its capability (R5).
  - The app sees only its own organizations' entries (R3).
- **Operator** (Q9, Q11): `handle-reserve`, `handle-release` and `handle-reclaim` run as the owner login against non-forced claim tables. A guard limits them to one-way transitions. Suspension is derived from the claim and enforced in the database (R4).
- **Roles** (Q5, GOZ-0004-IF-02): eight new actions. `admin` registers and renames publishers, and `publisher` creates and renames capabilities. Writes are also enforced by the new non-definer `account_has_role(org, role)` (R5, R9).
- **Product**: publisher list and detail pages, permission-gated forms, a suspension badge, canonical IDs with copy buttons, and four new same-origin route handlers (R13).
- **Telemetry and audit**: spans, `gozleme.publisher.operations`, and audit 1401–1408 by opaque IDs only (R10).
- **Demo**: Playwright project `goz-0005`, the full US1–US5 walkthrough including origin mismatch and reserve/reclaim, with regression of every earlier suite ([quickstart.md](./quickstart.md)).

## Technical Context

**Language/Version**: C# on .NET 10; SQL (PostgreSQL 17, PL/pgSQL); TypeScript (Next.js web tier, Node 24 e2e); Python 3 governance scripts

**Primary Dependencies**: existing only: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql, OpenTelemetry .NET, Next.js, Vitest, Playwright, xUnit v3 + Testcontainers. No new package or PostgreSQL extension.

**Storage**: PostgreSQL 17, migration `20261119000000_PublisherIdentities` ([data-model.md](./data-model.md)):
- five tables: `registry_origin`, `publishers`, `capabilities`, `handle_claims`, `alias_reservations`;
- one function, `account_has_role`;
- six trigger functions: `immutable_row_guard`, `publishers_guard`, `capabilities_guard`, `handle_claims_guard`, `capabilities_reserve_alias`, `alias_reservations_insert_guard`;
- policies.

It is additive and DDL only. It adds no new database role and needs no volume reset. The migrator then
records the origin.

**Testing**:
- **New integration suites**: `PublisherIsolationTests`, `HandleClaimTests`, `PublisherEndpointTests`, `RegistryOriginTests`, `OperatorHandleCommandTests`, `PublisherTelemetryTests`.
- **New unit suites**: `NamespaceRulesTests`, `CanonicalIdTests`, plus `OrganizationPermissionsTests` additions.
- **Updated**: `RlsCatalogTests` (new classes and negative tests), `RlsIsolationTests` (+2 tenant tables), `OrganizationPermissionContractTests`, `BootstrapBoundaryTests`, `MigrationAndRoleTests`, `StatusEndpointTests`, `TenantSeedTests` (carol's `publisher`).
- **Web (Vitest)**: publisher components, permission-hidden controls, suspended state, 403/409 messages, and the same-origin check in the new route handlers.
- **Playwright**: project `goz-0005`.

**Target Platform**: Linux containers (Compose locally; Kubernetes via Helm). Helm gets a required `registry.origin` value, and the migrator records it in the existing `pre-upgrade` job.

**Project Type**: Web application (API + Next.js BFF) in the existing monorepo.

**Performance Goals**: Publisher pages render in under 1 s with the demo data (GOZ-0003 SC-005). Each policy evaluation adds one primary-key or partial-index lookup (`account_has_role` and the held-claim `EXISTS`). Registration takes one extra insert or update on an indexed table.

**Constraints**:
- Runs offline, with no new services.
- Order: membership (404) → permission (403) → validation (400) → target (404) → suspension (409).
- Roles are read from the database per request.
- No new `SECURITY DEFINER` function, `BYPASSRLS` role or member service account.
- `PUBLIC` execute is revoked on every new function.
- Handles, names, aliases, display names and emails never appear in URLs, span names, metric labels or logs.
- Reserved and invalid input is rejected and never normalized (ordinal, invariant globalization).
- The GOZ-0004 lock order is unchanged.
- Earlier demos keep passing, with the deliberate pin updates in R12.

**Scale/Scope**:
- **API**: 1 migration, 8 endpoints, 8 actions and 2 resources, a `Publishers/` module (endpoints, store, namespace rules, origin, telemetry), 3 operator commands, origin checks in `MigrationRunner` and at API start, and a seed change.
- **Web**: 2 pages, 4 route handlers, about 5 components.
- **e2e**: 1 Playwright suite.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | Identity allocation only. There is no resolution, download or execution, and the capability catalogue and publish endpoints stay `404` | PASS |
| II. Portable baseline | No new service, package, extension or role. One new configuration value, with a throwaway Compose default and a required Helm value. Offline | PASS |
| III. Permanent identity | The origin is recorded once and guarded. Canonical IDs are origin + UUIDv7 and never embed an organization or a name. Aliases are reserved forever, reclaim included. Immutability is enforced for every login (R5, R6) | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | New tenant tables are forced, with member reads and role-gated writes in the database. The claim tables expose only the caller's organizations' entries to `gozleme_app`, and `alias_reservations` is never readable. Conflicts are limited to the decided reservation answers, and every other constraint error maps to the unknown-ID `404` (R3). Reclaim never touches a tenant row (Q10). The owner's view of the claim tables is the owner's existing authority, decided by the maintainer as Q11 (R4) | PASS |
| V. Contracts and bounded slices | OpenAPI and namespace contracts, the operator command contract, and the permission delta applied to the living contract and pinned by test. No discovery, manifests, signing or verification (GOZ-0006/0007/0012) | PASS |
| VI. Demo-first | Cumulative `goz-0005` browser demo with screenshots, database evidence, operator walkthrough, origin refusal and a telemetry no-secret scan, with earlier suites kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Through `drive-speckit` | PASS |
| IX/X. Progress and decisions | Q1–Q11 are recorded in `progress.yaml` and the spec. The agent choice (publisher rename is admin-only) is disclosed | PASS |

**Post-design re-check (after Phase 1): PASS.**
- `TableClassification.DefinerHelpers` is unchanged.
- `TableClassification` gains `InstanceReference` (GOZ-0003-IF-08 option B) and `InstanceClaims` (maintainer Q10), each with its own `RlsCatalog` rule and negative tests.
- The one security-sensitive design point, the owner login seeing the non-forced claim tables, was put to the maintainer and decided (Q11 = A). It is flagged for `review-security` at convergence.

## Project Structure

### Documentation (this feature)

```text
docs/specs/005-publisher-identities/
├── spec.md
├── plan.md                        # this file
├── research.md                    # R1–R13 (R2–R5 verified on PostgreSQL 17)
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── publishers-api.openapi.yaml
│   ├── namespace.md               # origin, identifier forms, namespace rule, reserved handles
│   ├── operator-commands.md       # handle-reserve / handle-release / handle-reclaim
│   └── permissions-delta.md       # applied to docs/architecture/contracts/organization-permissions.md
├── checklists/requirements.md
├── progress.yaml
├── evidence/                      # demo-run.json, publisher-evidence.json, screenshots/ (created by the demo)
├── reviews/                       # impact analysis (post-convergence)
└── tasks.md                       # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── Publishers/                              # new module
│   ├── PublisherEndpoints.cs                # 8 endpoints under /organizations/{id}/publishers, OrgActionAttribute, rate limit
│   ├── PublisherContracts.cs                # requests, responses, errors (handle_unavailable, conflict, suspended)
│   ├── PublisherStore.cs                    # registration (earmark consume | held insert), active-state reads, SQLSTATE mapping
│   ├── Publisher.cs  Capability.cs          # EF entities
│   ├── NamespaceRules.cs                    # ordinal rule, reserved handles
│   ├── RegistryOrigin.cs                    # options, validation, reader (pinned read path), start check, CanonicalId rendering
│   ├── PublisherOptions.cs                  # Gozleme:Publishers:RegisterPerHour
│   ├── PublisherTelemetry.cs                # spans, gozleme.publisher.operations
│   └── PublisherServiceCollectionExtensions.cs
├── Organizations/OrganizationPermissions.cs # + resources, 8 actions, explicit admin row
├── Identity/
│   ├── SecurityAudit.cs                     # + 1401–1408
│   ├── OperatorCommands.cs                  # seed grants carol publisher (as alice)
│   └── HandleOperatorCommands.cs            # new: handle-reserve / -release / -reclaim (migrator login)
├── Persistence/
│   ├── TableClassification.cs               # + publishers, capabilities; + InstanceReference, InstanceClaims
│   ├── RlsCatalog.cs                        # + two class rules (RLS state, organization_id, pinned privileges)
│   ├── MigrationRunner.cs                   # origin: validate → compare → migrate → record → verify
│   ├── GozlemeDbContext.cs                  # Publisher, Capability mappings
│   └── Migrations/20261119000000_PublisherIdentities.cs (+ Designer, snapshot)
├── GozlemeApp.cs  Program.cs                # module registration, origin start check, command dispatch
web/src/
├── app/organizations/[organizationId]/publishers/page.tsx
├── app/organizations/[organizationId]/publishers/[publisherId]/page.tsx
├── app/api/organizations/[organizationId]/publishers/route.ts                                   # POST
├── app/api/organizations/[organizationId]/publishers/[publisherId]/route.ts                     # PATCH
├── app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/route.ts        # POST
├── app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/route.ts  # PATCH
├── components/{PublisherList,RegisterPublisherForm,CapabilityList,CreateCapabilityForm,RenameDisplayNameForm,CanonicalId}.tsx
└── lib/publishers.ts                                                                            # types, messages
tests/Gozleme.Api.IntegrationTests/
├── PublisherIsolationTests.cs  HandleClaimTests.cs  PublisherEndpointTests.cs
├── RegistryOriginTests.cs  OperatorHandleCommandTests.cs  PublisherTelemetryTests.cs
├── Infrastructure/{GozlemeApiFactory,GozlemeDatabase,TwoTenants,OrgClient}.cs   # origin config, owner runner, publisher client
└── RlsCatalogTests.cs RlsIsolationTests.cs OrganizationPermissionContractTests.cs BootstrapBoundaryTests.cs
    MigrationAndRoleTests.cs StatusEndpointTests.cs TenantSeedTests.cs          (deliberate pin updates, research R12)
tests/Gozleme.Api.Tests/{NamespaceRulesTests,CanonicalIdTests,OrganizationPermissionsTests}.cs
docs/architecture/contracts/organization-permissions.md     # permissions delta applied
compose.yaml  .env.example  deploy/helm/gozleme/{values.yaml,templates/*}   # Gozleme__Registry__Origin
e2e/
├── playwright.config.ts                  # + goz-0005 (depends on goz-0004)
└── tests/{publisher-demo.spec.ts, evidence.ts (+ specDirs), bootstrap-demo.spec.ts (LATEST_MIGRATION)}
```

**Structure Decision**: a new `Publishers/` module beside `Organizations/`. Publishers are their own
resource with their own store, telemetry and audit range. They reuse the organization decision point
(`OrganizationAccess.AuthorizeAsync`) and the permission table rather than duplicating them, and they
map on the organizations route group so that `TenantTransaction`, `NoStore` and the contract test's
`IsScoped` apply unchanged. Operator handle commands sit beside the existing operator commands in
`Identity/` but in their own file, because they use a different login.

## Complexity Tracking

No constitution violations. Deliberate design points:

| Design point | Why needed | Simpler alternative rejected because |
|---|---|---|
| A new `InstanceClaims` table class with per-table pinned privileges | Q10 needs request-written, installation-owned rows. IF-08's `InstanceReference` forbids app writes | Tenant-owned claims would make reclaim change another tenant's row. `GlobalIdentity` is forbidden for them |
| Claim tables enable RLS without forcing it | The operator (owner) must reclaim across organizations with no account context (Q11) | A new operator login adds a secret and manual role creation on existing volumes. A non-account token is GOZ-0015's (IF-04) |
| One `handle_claims` table for claims and reservations | Uniqueness must span both, and only one partial unique index can do that atomically | Two tables need a cross-table trigger check that cannot see other organizations' earmarks under RLS, and races |
| Alias reserved by a trigger, with a depth guard | The app must not read aliases, yet a forged or missing alias must be impossible | An application-side insert can be skipped or forged by SQL inside one's own context |
| Blocking origin start check that tolerates an outage | FR-003 refuses a mismatched start, while GOZ-0000 requires start without the database | Failing start on outage regresses `DatabaseOutageTests`. Checking only lazily never "refuses to start" |
