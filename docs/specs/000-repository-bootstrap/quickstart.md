# Quickstart & Validation: GOZ-0000

## Prerequisites

Docker with Compose v2+. For tests outside containers: .NET SDK per `global.json`, Node 24, pnpm (via `corepack enable`), Python 3 with PyYAML + jsonschema.

## Run

```bash
make up          # docker compose up -d --build --wait
open http://localhost:3000
curl -s http://localhost:8080/api/v1/status | jq
make down        # stop; `make reset` also deletes the database volume
```

Ports override: `GOZLEME_WEB_PORT`, `GOZLEME_API_PORT`, `GOZLEME_DB_PORT` (see `.env.example`).

## Validation scenarios

| # | Scenario | Command | Expected |
|---|---|---|---|
| V1 | Healthy stack | `make up`, open dashboard | Overall **Healthy**; Database **Healthy** with schema version `…_Foundation`; vector "available, not enabled" |
| V2 | Status contract | `curl /api/v1/status` | Matches [contract](./contracts/status-api.openapi.yaml); values equal dashboard |
| V3 | DB failure | `docker compose stop postgres` | ≤15 s: dashboard **Degraded**, Database **Unavailable**; `/health/ready` 503, `/health/live` 200 |
| V4 | Recovery | `docker compose start postgres` | ≤30 s: **Healthy** without restarting api/web |
| V5 | API unreachable | `docker compose stop api` | Dashboard shows **API unreachable**, no stale DB status |
| V6 | Boundary | `curl -i /api/v1/catalog`, `/api/v1/organizations`, `/api/v1/auth/login` | 404; DB has no tables in schema `gozleme` |
| V7 | Tests | `make test` | Backend unit+integration and frontend unit tests pass |
| V8 | Governance | `make validate` | `validate_docs.py` PASS; register in sync |
| V9 | Autonomous demo | `make demo` | Runs V1–V6 in Playwright, writes `docs/specs/000-repository-bootstrap/evidence/` (demo-run.json, screenshots) |
