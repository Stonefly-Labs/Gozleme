# Data Model: GOZ-0000

No product entities. The bootstrap defines a status contract (transient) and database foundations.

## ServiceStatus (API response, contract v1)

| Field | Type | Rules |
|---|---|---|
| `contractVersion` | string | Always `"1"` |
| `service` | string | `"gozleme-api"` |
| `version` | string | Assembly informational version / build id (`GOZLEME_BUILD` env override) |
| `status` | enum | `healthy` if all required dependencies healthy; `degraded` if API alive but a required dependency failed; `unavailable` reserved for startup/shutdown |
| `checkedAt` | string (RFC 3339 UTC) | Time of this check |
| `dependencies` | DependencyStatus[] | Currently exactly one: `database` |

## DependencyStatus

| Field | Type | Rules |
|---|---|---|
| `name` | string | `"database"` |
| `status` | enum | `healthy` / `unavailable` |
| `required` | bool | `true` for database |
| `latencyMs` | number \| null | Null when unavailable |
| `schemaVersion` | string \| null | Latest EF migration id (e.g. `20261114000000_Foundation`); null when unavailable or none applied |
| `message` | string \| null | Fixed, non-sensitive text (e.g. `"Database unreachable"`); never exception text |
| `capabilities.vector` | `{ available: bool, enabled: bool }` \| null | Optional pgvector; does not affect status |

State transitions: `database.status` flips `healthy ↔ unavailable` per probe; overall `status` derives from required dependencies.

## Database foundations

- Roles (cluster, created by init script/operator): `gozleme_migrator` (owner, DDL), `gozleme_app` (`NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE`).
- Migration `Foundation`: `CREATE SCHEMA gozleme AUTHORIZATION gozleme_migrator`; `GRANT USAGE ON SCHEMA gozleme TO gozleme_app`; default privileges (SELECT/INSERT/UPDATE/DELETE on tables, USAGE on sequences) for future tables; `GRANT SELECT ON public."__EFMigrationsHistory" TO gozleme_app`.
- **Boundary**: after migration, schema `gozleme` contains zero tables; no identity/org/catalog tables exist anywhere.

## Telemetry (OpenTelemetry, foundational — GOZ-0000-IF-11)

| Service (`service.name`) | Signals | Notes |
|---|---|---|
| `gozleme-web-browser` | traces | document load, fetch; same-origin `traceparent`; exported via `POST /api/otel/v1/traces` (≤256 KiB, JSON/protobuf only) |
| `gozleme-web` | traces | Next.js routes and outgoing fetch to API (context propagated only to `GOZLEME_API_URL`) |
| `gozleme-api` | traces, metrics, logs | ASP.NET Core (health probes excluded), HttpClient, Npgsql, runtime; source/meter `Gozleme.Api` |
| `gozleme-migrator` | traces, metrics, logs | `database.migrate` span per attempt |

Project instrumentation (extended by later specs): span `status.probe database` (attributes
`gozleme.dependency.name`, `gozleme.status.outcome`; error status when unavailable); counter
`gozleme.status.checks` `{check}` tagged by dependency and outcome. Npgsql data sources are named
(`gozleme-api`, `gozleme-migrator`) so pool labels never contain connection strings. The collector
deletes credential-like attribute keys and redacts credential-like values in all pipelines.

Local storage (GOZ-0000-IF-22): logs → Loki (labels `service_name` etc.; `trace_id`/`span_id` as
structured metadata; 7-day retention), metrics → Prometheus (7 days), traces → Jaeger (in-memory).
Grafana is the single local viewer. Telemetry may contain operational metadata and is treated as
sensitive (see GOZ-0000-IF-21).

## DemoRunRecord (`evidence/demo-run.json`)

`specId`, `commit`, `branch`, `environment` (os, docker, compose versions), `startedAt`, `finishedAt`, `commands[]`, `result` (`passed|failed|blocked`), `assertions[]` (`name`, `result`), `evidence[]` (paths).
