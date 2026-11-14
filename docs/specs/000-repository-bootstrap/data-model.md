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

## DemoRunRecord (`evidence/demo-run.json`)

`specId`, `commit`, `branch`, `environment` (os, docker, compose versions), `startedAt`, `finishedAt`, `commands[]`, `result` (`passed|failed|blocked`), `assertions[]` (`name`, `result`), `evidence[]` (paths).
