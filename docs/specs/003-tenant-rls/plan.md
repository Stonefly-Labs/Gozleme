# Implementation Plan: Tenant RLS and Visibility Scaffolding

**Branch**: `feature/goz-0003-tenant-rls` | **Date**: 2026-11-15 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/003-tenant-rls/spec.md`

## Summary

Put effective PostgreSQL row-level security under the GOZ-0002 tenant tables, so isolation no longer
depends on application authorization alone. This closes the interim exception to invariant 1
(GOZ-0002-IF-01).

- **Identity binding**: the application signs a 5-minute account context (`v1.<account>.<expires>.<HMAC>`) for each transaction. The owner-defined `SECURITY DEFINER` helpers verify it with a key held in `gozleme_private`, which `gozleme_app` cannot read. Forged, tampered, expired and stale contexts identify nobody (maintainer decision; research R1–R2).
- **Policies**: per-command policies on `organizations`, `memberships` and `invitations`, enabled and **forced**. Rows are visible and writable only for members of the row's organization, as the database records it at each statement. Membership is never taken from the token (R4–R7).
- **FORCE without recursion**: each predicate is `CASE WHEN gozleme.rls_internal() THEN true ELSE … END`. The guard is true only inside owner-defined definer helpers called from another login, which was verified empirically (R4).
- **Pre-tenant paths**: listing, creation (creator path) and redemption (code-hash bearer capability plus a locking helper) keep working with the GOZ-0002 lock order (R6).
- **Transaction scope**: a `TenantTransaction` endpoint filter on the `/organizations` and `/invitations` groups opens one transaction, establishes the context, and commits or rolls back. A database refusal maps to the same empty 404 (R3, R8).
- **Visibility scaffolding**: `TableClassification` (tenant-owned / global identity / reserved shared-public) and the `RlsCatalog` check, which fails on unprotected or unclassified tables (R11).
- **Telemetry and audit**: the `gozleme.tenant.transaction` span with established and cleared events, the bounded `gozleme.tenant.denials` counter, audit events 1201–1203 and a startup self-check (R9).
- **Seed and demo**: `seed` acts as a seeded member. It adds `dave` and **Seeded Partner Org**. The new `goz-0003` Playwright project shows two tenants in the UI and API, plus direct-database evidence (R10, R12).
- **Not built**: roles, public/shared records, grants, new UI pages and new database roles (R14).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK via `global.json`); SQL (PostgreSQL 17, PL/pgSQL); TypeScript on Node 24 (e2e); Python 3 for governance scripts

**Primary Dependencies**: Existing packages only: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql, OpenTelemetry .NET, Playwright. `System.Security.Cryptography.HMACSHA256` signs the token. The PostgreSQL contrib extension **`pgcrypto`** is new: it is trusted, installable by the database owner and ships in every supported image. External PostgreSQL deployments must make it available (documented).

**Storage**: PostgreSQL 17. Migration `20261117000000_TenantRls` adds schema `gozleme_private` (`pgcrypto`, `context_keys`), seven helper functions, and RLS + FORCE + policies on three tables ([data-model.md](./data-model.md), [contracts/tenant-context.md](./contracts/tenant-context.md)). There are no column changes and no data migration. `Down` reverses everything.

**Testing**: xUnit v3 + Testcontainers on the real migrated database.
- **New integration suites**: `RlsIsolationTests` (table × command × context matrix, through the app login and the owner login), `TenantContextBoundaryTests` (forged, tampered, expired and future-dated tokens, a stray organization setting, a stale pooled connection, a removed member with a still-valid token, an identity lookup after tenant use), `RlsCatalogTests` (clean pass plus negative cases), `TenantTransactionTests` (refused write → 404 + counter, rollback clears the context, outage → 503), `TenantTelemetryTests` (span, events, counter, no secrets) and `TenantSeedTests`.
- **Unit tests**: `TenantContextSignerTests` covers the token format and key validation.
- **Regression**: all GOZ-0000…0002 suites, which run with a test key.
- **Playwright**: project `goz-0003`.

**Target Platform**: Linux containers (Docker Compose locally; Kubernetes via Helm)

**Project Type**: Web application (API + Next.js BFF) in the existing monorepo. This slice changes the API, the database, deployment configuration and e2e. The web tier is unchanged.

**Performance Goals**: SC-005 keeps organization pages under 1 s. Per organization request the slice adds one `BEGIN` and `COMMIT` plus one `establish_context` call. Policies evaluate `current_account_id()` and `account_organizations()` once per statement (initplan or hashed subplan), never once per row.

**Constraints**:
- Offline, with no new services.
- The token, key and code hash travel only as SQL parameters and never appear in SQL text, logs, spans or metrics (invariant 8).
- `gozleme_app` stays non-owner and `NOBYPASSRLS`, and no role is added (no `make reset`).
- Lock order stays organization, then invitation (GOZ-0002-IF-20).
- Owner logins are bound by RLS (FORCE), and tests log in as the owner directly, never by `SET ROLE` from a superuser (R4).

**Scale/Scope**:
- 1 migration and 7 SQL functions.
- 3 tables with 11 policies.
- 1 endpoint filter and 2 refactored handlers.
- 1 signer service with options.
- Seed changes.
- Compose, Helm and `.env.example` wiring.
- 1 Playwright project.
- No API contract changes.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | Database security for existing tenant data only | PASS |
| II. Portable baseline | PostgreSQL contrib `pgcrypto` only. Compose stays offline with default local configuration. Helm gains one secret key | PASS |
| III. Permanent identity | Organization and account IDs are unchanged. The token names the permanent opaque account ID | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | **This slice delivers it for every current tenant table.** Application authorization stays first, and effective RLS is forced on the owner too. `gozleme_app` is non-owner and `NOBYPASSRLS`. The context is transaction-scoped and is not treated as proof against SQL injection inside the request's own context; that limit is documented. Negative cross-tenant tests run against real PostgreSQL. The classification check guards future tables, and shared/public is reserved for a separately verified path (CR-02) | PASS |
| V. Contracts and bounded slices | The database contract is in `contracts/`, and the HTTP contract is unchanged. No roles, grants, public records or UI | PASS |
| VI. Demo-first | Cumulative `goz-0003` project with two tenants in the UI and API, sanitized direct-database evidence, real screenshots, Jaeger span evidence and a no-secret scan. Earlier suites are kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Through `drive-speckit` | PASS |
| IX/X. Progress and decisions | Four maintainer decisions are recorded in `progress.yaml` and in the spec's Clarifications | PASS |

Post-design re-check (after Phase 1): **PASS**, with no exceptions. The GOZ-0002 Complexity Tracking
exception (no RLS) is **closed** by this slice.

## Project Structure

### Documentation (this feature)

```text
docs/specs/003-tenant-rls/
├── spec.md
├── plan.md                 # this file
├── research.md             # R1–R14 decisions (R4 includes the empirical verification)
├── data-model.md
├── quickstart.md
├── contracts/
│   └── tenant-context.md   # token, functions, guarantees, non-guarantees, future-table rules, config, telemetry
├── checklists/requirements.md
├── progress.yaml
├── evidence/               # demo-run.json, rls-evidence.json, screenshots/ (created by the demo)
├── reviews/                # impact analysis (post-convergence)
└── tasks.md                # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── GozlemeApp.cs                              # + AddGozlemeTenancy(); startup self-check
├── Tenancy/                                   # new module
│   ├── TenancyOptions.cs                      # Gozleme:Rls:ContextKey (≥ 32 chars, validated on start)
│   ├── TenantContextSigner.cs                 # v1 token, HMACSHA256, 5-minute lifetime
│   ├── TenantTransaction.cs                   # endpoint filter: BEGIN, establish, commit/rollback, 42501 → 404
│   ├── TenantTelemetry.cs                     # span, events, gozleme.tenant.denials
│   └── TenancyServiceCollectionExtensions.cs
├── Organizations/
│   ├── OrganizationEndpoints.cs               # groups use TenantTransaction; RemoveMember/Redeem use the ambient transaction;
│   │                                          # redeem sets the capability and calls lock_invited_organization
│   └── OrganizationAccess.cs                  # unchanged check; doc comment notes the RLS second layer
├── Identity/
│   ├── SecurityAudit.cs                       # + tenancy events 1201–1203
│   └── OperatorCommands.cs                    # seed: + dave, + Seeded Partner Org; acts as seeded member
└── Persistence/
    ├── MigrationRunner.cs                     # provision gozleme_private.context_keys after migrate
    ├── TableClassification.cs                 # TenantOwned / GlobalIdentity / SharedPublic (empty)
    ├── RlsCatalog.cs                          # ViolationsAsync(connection)
    └── Migrations/20261117000000_TenantRls.cs (inline migration attributes, no Designer; snapshot unchanged)
tests/Gozleme.Api.IntegrationTests/
├── Infrastructure/{GozlemeDatabase,GozlemeApiFactory}.cs   # test key; OwnerLogin helper; TenantSql helper
├── RlsIsolationTests.cs  TenantContextBoundaryTests.cs  RlsCatalogTests.cs
├── TenantTransactionTests.cs  TenantTelemetryTests.cs  TenantSeedTests.cs (or OperatorCommandTests + seed)
└── MigrationAndRoleTests.cs  BootstrapBoundaryTests.cs  (narrowed where they assumed no RLS)
tests/Gozleme.Api.Tests/TenantContextSignerTests.cs
deploy/
├── helm/gozleme/{values.yaml,templates/api.yaml,templates/migrate-job.yaml,README.md}  # rls-context-key; release note closed
compose.yaml  .env.example  .gitleaks.toml                     # GOZLEME_CONTEXT_KEY for api, migrate, seed; allow-list dev/test keys
e2e/
├── playwright.config.ts                       # + goz-0003 (depends on goz-0002)
└── tests/{tenant-rls-demo.spec.ts, evidence.ts (+ DEMO.dave, PARTNER_ORG, specDirs), compose.ts (+ psql helper, forbidden key)}
```

**Structure Decision**: Add a new `Tenancy/` API module beside `Identity/` and `Organizations/`, as
GOZ-0002 did. The classification check lives under `Persistence/`, next to the migrations that future
specs edit. There are no web-tier changes, because the existing organization pages already show the
two-tenant outcome.

## Complexity Tracking

No constitution violations. Two deliberate design points:

| Design point | Why needed | Simpler alternative rejected because |
|---|---|---|
| `CASE WHEN rls_internal()` owner-in-definer guard on every policy | FORCE binds the owner and no role has BYPASSRLS, so membership helpers would otherwise recurse through their own policy | A mirror table duplicates data. A token organization claim delays removal. A BYPASSRLS role needs a new role and a volume reset |
| Code-hash bearer capability setting for redemption | The redeemer is not yet a member, and possession of the code is the proof (GOZ-0002) | An unguarded definer lookup that returns invitation rows would expose more than the policy needs, and would still need visibility for the `FOR UPDATE` |
