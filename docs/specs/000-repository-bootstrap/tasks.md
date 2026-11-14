---
description: "Task list for GOZ-0000 repository bootstrap"
---

# Tasks: Repository Bootstrap and Executable Engineering Foundation

**Input**: Design documents from `docs/specs/000-repository-bootstrap/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/status-api.openapi.yaml, quickstart.md

**Tests**: Required by spec (FR-012, FR-015, FR-018) and constitution VI.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: Setup (Shared Infrastructure)

- [X] T001 Create repository hygiene files `.gitignore`, `.gitattributes`, `.editorconfig` at repo root
- [X] T002 [P] Add `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `Gozleme.slnx`
- [X] T003 [P] Add root `package.json` (packageManager pnpm, workspace scripts), `pnpm-workspace.yaml`, `.nvmrc`
- [X] T004 [P] Add `.env.example` with non-secret local defaults and port overrides

---

## Phase 2: Foundational (Blocking Prerequisites)

- [X] T005 Create `src/Gozleme.Api/Gozleme.Api.csproj` minimal-API host with JSON console logging in `src/Gozleme.Api/Program.cs`
- [X] T006 Create `GozlemeDbContext` in `src/Gozleme.Api/Persistence/GozlemeDbContext.cs` and `Foundation` migration in `src/Gozleme.Api/Persistence/Migrations/` (schema `gozleme`, grants, default privileges; no tables)
- [X] T007 Implement `migrate` command mode in `src/Gozleme.Api/Program.cs` with retry-until-ready using migrator connection string
- [X] T008 [P] Create `deploy/postgres/init/01-roles.sh` creating `gozleme_migrator` and `gozleme_app` (NOSUPERUSER NOBYPASSRLS)
- [X] T009 [P] Create `src/Gozleme.Api/Dockerfile` (multi-stage, non-root runtime)
- [X] T010 Create `compose.yaml` with `postgres` (pgvector/pgvector:pg17, healthcheck), `migrate` (one-shot), `api`, `web` services
- [X] T011 [P] Create test projects `tests/Gozleme.Api.Tests` and `tests/Gozleme.Api.IntegrationTests` (xUnit v3, Testcontainers PostgreSQL fixture applying init roles + migration)

**Checkpoint**: `docker compose up postgres migrate` applies migration.

---

## Phase 3: User Story 1 — Live status dashboard (P1) 🎯 MVP

**Goal**: One command → dashboard showing real API + DB status.
**Independent Test**: `make up`; dashboard Healthy with schema version equal to `/api/v1/status`.

### Tests for User Story 1

- [X] T012 [P] [US1] Unit tests for status aggregation in `tests/Gozleme.Api.Tests/StatusAggregationTests.cs`
- [X] T013 [P] [US1] Integration tests: migration applied once and idempotent, app role flags, `/api/v1/status` healthy with schema version, `/health/live` + `/health/ready` in `tests/Gozleme.Api.IntegrationTests/`
- [X] T014 [P] [US1] Integration test on plain `postgres:17-alpine` (no pgvector) still healthy, vector `available=false`
- [X] T015 [P] [US1] Vitest component tests for healthy rendering in `web/src/components/StatusDashboard.test.tsx`

### Implementation for User Story 1

- [X] T016 [US1] Status contract records + `StatusService` + `DatabaseProbe` in `src/Gozleme.Api/Status/`
- [X] T017 [US1] Map `/health/live`, `/health/ready`, `/api/v1/status` (no-store) in `src/Gozleme.Api/Program.cs`
- [X] T018 [US1] Scaffold Next.js app in `web/` (App Router, TS, ESLint, Vitest config), `web/Dockerfile` (standalone output, non-root)
- [X] T019 [US1] Contract types + fetch-with-timeout in `web/src/lib/status.ts`; route `web/src/app/api/backend-status/route.ts`
- [X] T020 [US1] Accessible `StatusDashboard` client component with 5 s polling and "last checked" in `web/src/components/StatusDashboard.tsx`; page `web/src/app/page.tsx`

**Checkpoint**: US1 independently demonstrable.

---

## Phase 4: User Story 2 — Honest degraded state (P1)

**Goal**: DB down → Degraded; API down → Unreachable; auto recovery; no secrets.
**Independent Test**: stop/start postgres and api containers while watching dashboard.

### Tests for User Story 2

- [X] T021 [P] [US2] Integration test: stopped DB container → status `degraded`, ready 503, live 200; restart → healthy, in `tests/Gozleme.Api.IntegrationTests/DatabaseOutageTests.cs`
- [X] T022 [P] [US2] Integration/unit test: status JSON and captured logs contain no password, connection string or exception text, in `tests/Gozleme.Api.IntegrationTests/SecretLeakTests.cs`
- [X] T023 [P] [US2] Vitest tests for degraded, unreachable and malformed responses; route handler timeout test

### Implementation for User Story 2

- [X] T024 [US2] Probe timeout + fixed non-sensitive messages + exception-type-only logging in `src/Gozleme.Api/Status/DatabaseProbe.cs`
- [X] T025 [US2] Unreachable / degraded / unknown rendering and stale-state suppression in `web/src/components/StatusDashboard.tsx`

---

## Phase 5: Boundary (FR-012) — supports US1/US2

- [X] T026 [P] Integration test: `/api/v1/catalog`, `/api/v1/organizations`, `/api/v1/auth/login`, `/api/v1/users` → 404; schema `gozleme` has zero tables; no tables matching identity/org/catalog names, in `tests/Gozleme.Api.IntegrationTests/BootstrapBoundaryTests.cs`

---

## Phase 6: User Story 3 — Governed repository (P2)

- [X] T027 [P] [US3] `LICENSE` (MIT), rewrite `README.md` (quick start, commands), `CONTRIBUTING.md`, `SECURITY.md`
- [X] T028 [P] [US3] `.github/pull_request_template.md`, `.github/CODEOWNERS` (placeholder instructions only)
- [X] T029 [US3] `Makefile` targets: build, test, lint, up, down, logs, reset, validate, demo
- [X] T030 [US3] `.github/workflows/ci.yml`: governance, branch-name, backend, frontend, helm, containers, secrets, e2e-demo jobs
- [X] T031 [P] [US3] `scripts/check_branch_name.sh` with negative self-test
- [X] T032 [P] [US3] `docs/engineering/github-protection.md` with required ruleset and truthful `NOT VERIFIED` status; update `docs/engineering/local-development.md`
- [X] T033 [P] [US3] Helm chart `deploy/helm/gozleme/` (Chart.yaml, values.yaml, templates for api, web, migrate Job, services, configmap; secrets via existingSecret)

---

## Phase 7: User Story 4 — Agent workflows & registry tooling (P3)

- [X] T034 [US4] Add `requirements.txt` for governance scripts; ensure `make validate` runs `validate_docs.py` and `generate_register.py --check`; verify skills/agents files reference existing paths
- [X] T035 [US4] Create `docs/specs/000-repository-bootstrap/progress.yaml` and update `.gozleme/spec-registry.yaml` stage/gates; regenerate `docs/roadmap/spec-register.md`

---

## Phase 8: Autonomous demo & polish

- [X] T036 Playwright project in `e2e/` with `e2e/tests/bootstrap-demo.spec.ts` (healthy assert vs API, screenshot; stop DB → degraded, screenshot; start DB → healthy; stop API → unreachable, screenshot; boundary 404s)
- [X] T037 `scripts/demo/run-demo.sh`: compose up --wait, run Playwright, write `evidence/demo-run.json`, scan evidence for secrets, restore stack
- [X] T038 Run `make test`, `make validate`, `make demo`; record results in progress.yaml

---

## Dependencies & Execution Order

- Setup (T001–T004) → Foundational (T005–T011) → US1 (T012–T020) → US2 (T021–T025) and Boundary (T026) → US3/US4 (T027–T035, mostly parallel) → Demo (T036–T038).
- US2 depends on US1's status endpoint and dashboard. US3/US4 are independent of US1/US2 except CI e2e (T030) referencing demo (T036–T037).

### Parallel Opportunities

- T002/T003/T004; T008/T009/T011; T012–T015; T021–T023; T027/T028/T031/T032/T033.

## Implementation Strategy

MVP = Phases 1–3 (healthy dashboard). Then US2 failure states + boundary, then governance/CI/Helm, then autonomous demo evidence. Commit at each phase checkpoint.

---

## Phase 9: Convergence

<!-- /speckit-converge 2026-11-14: assessed spec.md FR-001–FR-019, SC-001–SC-007, edge cases and plan decisions against the code. -->

- [X] T039 [US1] Add automated accessibility assertion (axe-core WCAG 2.1 A/AA: contrast, landmarks, labels) for healthy, degraded and unreachable dashboard states in `e2e/tests/bootstrap-demo.spec.ts` — gap: FR-006 contrast/semantics not verified by any test
- [X] T040 [US1] Record stack start-up duration (`docker compose up --wait`) in `evidence/demo-run.json` via `scripts/demo/run-demo.sh` and `scripts/demo/write_demo_record.py` — gap: SC-001 timing not measured

---

## Phase 10: Foundational observability (maintainer decision GOZ-0000-IF-11, 2026-11-14)

**Goal**: US5 — OpenTelemetry threaded through web → API → database, offline by default.
**Independent Test**: dashboard refresh produces one Jaeger trace with web, API and Npgsql spans; metrics endpoint shows API request and status-check metrics.

- [X] T041 [US5] `src/Gozleme.Api/Telemetry/GozlemeTelemetry.cs`: resource (service name/version/environment), tracing (ASP.NET Core minus health probes, HttpClient, Npgsql, `Gozleme.Api` source), metrics (ASP.NET Core, HttpClient, runtime, Npgsql, `Gozleme.Api` meter), OTel logging; OTLP only when endpoint configured; used by API and migrator
- [X] T042 [US5] Status probe span + `gozleme.status.checks` counter tagged by outcome in `src/Gozleme.Api/Status/`
- [X] T043 [P] [US5] Integration test with in-memory exporter: server span → probe span → Npgsql span in one trace; error status on outage; counter outcomes; no secrets in attributes, in `tests/Gozleme.Api.IntegrationTests/TelemetryTests.cs`
- [X] T044 [US5] Web `instrumentation.ts` with `@vercel/otel`, context propagation to API
- [X] T045 [US5] `deploy/otel/collector.yaml` + Compose `otel-collector` and `jaeger` services, OTLP env for api/migrate/web
- [X] T046 [US5] Helm: `telemetry.otlpEndpoint` value wired to all workloads
- [X] T047 [US5] Demo: assert cross-service trace and metrics via Jaeger query API and Prometheus endpoint, secret-free; screenshot of the trace in Jaeger UI; collector-down resilience check
- [X] T048 [US5] Docs: README, local-development, quickstart, data-model telemetry section, PR evidence

---

## Phase 11: Stored telemetry (maintainer decision GOZ-0000-IF-22, 2026-11-14)

- [X] T049 [US5] Loki (`deploy/observability/loki.yaml`), Prometheus (`deploy/observability/prometheus.yaml`), Grafana provisioning with log↔trace links; collector logs pipeline → Loki; Compose services and volumes
- [X] T050 [US5] Demo: outage warnings stored in Loki with `trace_id` resolving to the Jaeger trace, metrics stored in Prometheus, no secrets in stored logs; Grafana Explore screenshot
- [X] T051 [US5] Docs: spec FR-022a, research R9, data model, quickstart V13, local development, README
