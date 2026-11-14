# Implementation Plan: Repository Bootstrap and Executable Engineering Foundation

**Branch**: `chore/goz-0000-bootstrap` | **Date**: 2026-11-14 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/000-repository-bootstrap/spec.md`

## Summary

Create the runnable cumulative stack every later Gözleme spec builds on: a .NET 10 ASP.NET Core
API (`Gozleme.Api`) exposing liveness, readiness and a versioned `/api/v1/status` document; a
Next.js dashboard that polls that status through a same-origin server route and renders
healthy/degraded/unavailable/unreachable states; PostgreSQL with a foundation EF Core migration
applied by a one-shot `migrate` container using a separate owner role, while the API runs as a
`NOSUPERUSER NOBYPASSRLS` application role. Docker Compose runs everything offline; a Helm chart
skeleton renders; GitHub Actions CI runs governance validation, backend unit + Testcontainers
integration tests, frontend lint/typecheck/unit tests, chart render, image builds, secret scan
and the Playwright demo. No identity, organization or catalog features are built.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned via `global.json`, 10.0.403, `latestPatch`); TypeScript 5.9 on Node 24 LTS; Python 3 for existing governance scripts

**Primary Dependencies**: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql.EntityFrameworkCore.PostgreSQL 10.0.x; Next.js 16 (App Router) + React 19; pnpm 11 (pinned via `packageManager`)

**Storage**: PostgreSQL 17 (`pgvector/pgvector:pg17` image locally so the optional extension is *available*; it is never created or required). CI also proves startup on plain `postgres:17-alpine`.

**Testing**: xUnit v3 + `Microsoft.AspNetCore.Mvc.Testing` + Testcontainers.PostgreSql (backend); Vitest + Testing Library (frontend); Playwright (browser demo/e2e); existing `scripts/validate_docs.py`

**Target Platform**: Linux containers (Docker Compose locally, Kubernetes via Helm)

**Project Type**: Web application (API + web frontend) in a monorepo

**Performance Goals**: Status check completes < 2 s with DB healthy; DB probe timeout 2 s; dashboard poll interval 5 s; web→API fetch timeout 3 s

**Constraints**: Offline-capable; no cloud/hosted IdP/LLM/embedding dependency; no secrets in status, UI, logs or screenshots; app DB role cannot bypass RLS

**Scale/Scope**: Single API instance + single web instance; one foundation migration; one dashboard page

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Gate | Pre-design | Post-design |
|---|---|---|---|
| I. Registry, not gateway | No request brokering/proxying of AI traffic | PASS — status only | PASS — web route only proxies Gözleme's own status endpoint |
| II. Portable stack | .NET 10, Next.js/TS/pnpm, PostgreSQL/EF Core, optional pgvector, Compose + Helm, no mandatory cloud | PASS | PASS — pgvector reported, never required (tested on plain postgres) |
| III. Identity & provenance | Nothing premature | PASS — N/A this slice | PASS — no identity fields created |
| IV. Tenant isolation | App role NOSUPERUSER/NOBYPASSRLS; no secrets exposed | PASS | PASS — role separation tested; secret-leak tests on status/log/evidence |
| V. Bounded slices | No speculative frameworks; versioned contracts | PASS | PASS — `/api/v1/status` contract v1; no empty module scaffolding, OpenTelemetry deferred |
| VI. Demo-first | Compose, autonomous Playwright demo, real screenshots, run metadata | PASS | PASS — `make demo` produces evidence under `docs/specs/000-repository-bootstrap/evidence/` |
| VII. Protected main | Document rules; never claim enforcement | PASS | PASS — GitHub API returns 403 for rulesets/branch protection on this private repo plan; recorded as human blocker, not claimed |
| VIII. Converge & impact | Workflow followed | PASS | PASS |
| IX. Progress tracking | `progress.yaml` + registry + generated register | PASS | PASS |
| X. No fabrication | Evidence real | PASS | PASS |

No violations; Complexity Tracking not required.

## Project Structure

### Documentation (this feature)

```text
docs/specs/000-repository-bootstrap/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── status-api.openapi.yaml
├── checklists/requirements.md
├── progress.yaml
├── evidence/            # demo run metadata, assertion results, screenshots
└── tasks.md             # /speckit-tasks output
```

### Source Code (repository root)

```text
Gozleme.slnx
global.json
Directory.Build.props
Directory.Packages.props          # central NuGet version pinning
src/Gozleme.Api/
├── Program.cs                    # host, endpoints, "migrate" command mode
├── Status/                       # status contract records + StatusService + DB probe
├── Persistence/                  # GozlemeDbContext, Migrations/
└── Dockerfile
tests/Gozleme.Api.Tests/          # unit tests (status aggregation, redaction)
tests/Gozleme.Api.IntegrationTests/  # Testcontainers: migration, roles, endpoints, boundary, DB-down
web/                              # Next.js app (pnpm)
├── src/app/page.tsx              # dashboard
├── src/app/api/backend-status/route.ts  # same-origin server route → API
├── src/components/StatusDashboard.tsx
├── src/lib/status.ts             # contract types + fetch-with-timeout
├── src/**/*.test.tsx             # Vitest
└── Dockerfile
e2e/                              # Playwright demo (pnpm workspace member)
deploy/postgres/init/             # role bootstrap script for Compose
deploy/helm/gozleme/              # Chart.yaml, values.yaml, templates/
compose.yaml
Makefile
scripts/                          # existing governance scripts + demo/check helpers
.github/workflows/ci.yml, pull_request_template.md, CODEOWNERS
```

**Structure Decision**: Monorepo web application. Backend is a single modular-monolith host
project (`Gozleme.Api`); modules are added by later specs as real features arrive rather than
pre-created. `web/` and `e2e/` form a pnpm workspace rooted at the repository.

## Complexity Tracking

Not required — no constitution violations.
