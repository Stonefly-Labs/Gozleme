# Research: GOZ-0000 Repository Bootstrap

All Technical Context items were resolvable; no open NEEDS CLARIFICATION.

## R1 — Who runs migrations

- **Decision**: A one-shot `migrate` Compose service runs the API image with `migrate` argument, connecting as owner role `gozleme_migrator`. The long-running `api` service connects as `gozleme_app` (`LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS`) and `depends_on` the migrator having `completed_successfully`.
- **Rationale**: Keeps DDL privileges out of the serving process; establishes the role separation later RLS specs (GOZ-0003) rely on (security invariant 2). Migration history is EF Core's `__EFMigrationsHistory`, which is idempotent by design.
- **Alternatives**: Migrate on API startup (gives the serving process owner credentials — rejected); external migration tool (extra dependency — rejected).

## R2 — Database roles creation

- **Decision**: Cluster roles are created by `deploy/postgres/init/01-roles.sh` (Postgres image init hook) from environment variables. The foundation migration creates schema `gozleme`, grants `USAGE` to `gozleme_app` and sets default privileges, and grants `SELECT` on `public."__EFMigrationsHistory"` so the app can report schema version. In Helm, roles are an operator prerequisite documented in chart README.
- **Rationale**: Roles are cluster-level and cannot be created reliably from an EF migration running as a non-superuser owner.

## R3 — Status contract and probes

- **Decision**: `GET /health/live` (always 200 while process runs), `GET /health/ready` (200/503 based on DB probe), `GET /api/v1/status` (always 200 with JSON body describing overall `healthy|degraded|unavailable`; `contractVersion: "1"`). The DB probe opens a connection with a 2 s timeout, runs `SELECT 1`, reads latest `MigrationId`, and checks `pg_available_extensions`/`pg_extension` for `vector`. Exceptions are logged by type only and mapped to a fixed message (`"Database unreachable"`); no exception messages, hosts or connection strings are serialized.
- **Rationale**: Separates liveness from readiness for orchestrators (FR-002); status body always renders on the dashboard (FR-005/FR-006).
- **Alternatives**: ASP.NET HealthChecks UI JSON writer (serializes exception descriptions — risk of leaking host details — rejected for status; built-in health checks still used for `/health/*` with minimal writer).

## R4 — Dashboard data flow

- **Decision**: Client component polls same-origin `GET /api/backend-status` every 5 s; that Next.js route handler fetches `${GOZLEME_API_URL}/api/v1/status` server-side with a 3 s `AbortSignal.timeout`, validates shape, and returns `{ reachable: true, status }` or `{ reachable: false, checkedAt }`. Responses are `no-store`.
- **Rationale**: No CORS, API need not be exposed to the browser, dashboard never shows stale status as current when the API is down (FR-005, US2-3).

## R5 — Optional pgvector

- **Decision**: Local Compose uses `pgvector/pgvector:pg17` (extension *available*, not created). Status reports `vector: { available, enabled }`. Integration tests run on `postgres:17-alpine` to prove the stack is healthy without it (FR-010).

## R6 — Tooling and pinning

- **Decision**: `global.json` (SDK 10.0.403, `rollForward: latestPatch`), `Directory.Packages.props` central package management, `packageManager: pnpm@11.1.2`, `.nvmrc` = 24, exact npm versions in `package.json`, `pnpm-lock.yaml` committed. TypeScript pinned to 5.9.x (7.x not yet validated against Next 16 tooling). xUnit v3 for tests.
- **Note**: NuGet now lists 11.0.0 packages (.NET 11); constitution mandates .NET 10, so all Microsoft.* packages are pinned to 10.0.x.

## R7 — CI

- **Decision**: One GitHub Actions workflow `ci.yml` on `pull_request` and `push` to non-main branches with jobs: `governance` (validate_docs, register check, branch name), `backend` (dotnet build/test incl. Testcontainers on ubuntu runner Docker), `frontend` (pnpm lint/typecheck/test/build), `helm` (lint + template via pinned `alpine/helm` container), `secrets` (gitleaks CLI pinned), `e2e-demo` (builds the api/web images via `docker compose up --build`, runs Playwright, uploads evidence artifact), plus dependency audits (`pnpm audit --prod`, NuGet audit as build error). There is no separate `containers` job: image builds are exercised inside `e2e-demo`. Actions are pinned by full commit SHA with the release tag in a comment. *(Corrected per GOZ-0000-IF-17.)*

## R8 — Protected main

- **Finding**: `gh api repos/Stonefly-Labs/gozleme/rulesets` and `/branches/main/protection` return HTTP 403 "Upgrade to GitHub Pro or make this repository public". Rules therefore cannot be applied or verified by the agent. **Decision**: document required ruleset in `docs/engineering/branching-and-pr-policy.md`-linked `docs/engineering/github-protection.md` with status `NOT VERIFIED — human action required`, and record a blocker for the human owner. Nothing claims enforcement.

## R9 — Observability (revised 2026-11-14, maintainer decision GOZ-0000-IF-11)

- **Decision**: OpenTelemetry is foundational. Backend and migrator use OpenTelemetry .NET (`OpenTelemetry.Extensions.Hosting`, ASP.NET Core/HttpClient/runtime instrumentation, `Npgsql.OpenTelemetry`) with OTLP export enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; a shared `GozlemeTelemetry` defines `ActivitySource`/`Meter` "Gozleme.Api" for domain spans and metrics. The web tier uses Next.js `instrumentation.ts` with `@vercel/otel` (server-side), propagating W3C `traceparent` to the API. Compose adds `otel-collector` (contrib, redaction processor) exporting traces to Jaeger v2 (local UI/query API) and metrics to a Prometheus scrape endpoint; logs go to the collector debug exporter. Health probe requests are excluded from traces to limit noise.
- **Rationale**: Maintainer requires observability threaded through every spec; OTLP keeps backends pluggable and the default stack offline.
- **Stored telemetry (GOZ-0000-IF-22, maintainer decision 2026-11-14)**: the local stack adds Loki 3.7 (OTLP logs via the collector, filesystem volume, 7-day retention with compactor), Prometheus 3.16 (scrapes the collector, 7-day retention) and Grafana 13.3 (provisioned Loki/Jaeger/Prometheus datasources; log `trace_id` → Jaeger, trace → logs). Jaeger stays in-memory locally. Helm ships none of these; operators bring OTLP backends. Alternatives: Grafana Tempo instead of Jaeger (rejected for now — Jaeger already integrated and lighter); OpenSearch for logs (heavier).
- **Superseded**: the earlier "JSON console logging only; defer OpenTelemetry" decision.

## R10 — Failure demo

- **Decision**: The Playwright demo stops the `postgres` service via `docker compose stop` to create the degraded state, then starts it again for recovery; later stops `api` for the unreachable state. No fault-injection code ships.
