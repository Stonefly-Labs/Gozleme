# Quickstart: Tenant RLS and Visibility Scaffolding

## Prerequisites

Docker, .NET 10 SDK, Node 24 with pnpm (`make bootstrap`). Copy `.env.example` to `.env` or rely on
the Compose defaults. `GOZLEME_CONTEXT_KEY` defaults to a throwaway local key.

## Run the automated checks

```bash
make test       # .NET unit + Testcontainers integration (RLS matrix, catalog check, boundary tests) and Vitest
make lint
make validate
```

The key suites are:
- `RlsIsolationTests`: the cross-tenant matrix (SC-001).
- `TenantContextBoundaryTests`: forged, tampered, expired and stale-connection contexts (User Story 3).
- `RlsCatalogTests`: the classification check and its three negative cases (SC-002).
- `TenantTelemetryTests`: spans, the counter and the no-secret check.

## Run the cumulative demo

```bash
make demo SPEC=GOZ-0003
```

Expected:
- The GOZ-0000, GOZ-0001 and GOZ-0002 suites pass. Their screenshots go under `evidence/screenshots/regression-goz-000N/`.
- The `goz-0003` suite signs in as **alice** (Seeded Demo Org) and **dave** (Seeded Partner Org). Each sees only their own organization. Opening the other organization's page shows "Organization not found", and the API answers it with the same empty `404` as an unknown ID.
- The suite's database step (`docker compose exec postgres psql` as `gozleme_app` and as `gozleme_migrator`) records `evidence/rls-evidence.json`. It shows zero rows with no context, a forged setting, a tampered token and an expired token, only own rows with a valid token, and zero rows for the owner login without a context.
- Jaeger (`http://localhost:16686`) shows `gozleme.tenant.transaction` spans with their established and cleared events. The `gozleme.tenant.denials` counter appears in the metrics scrape.
- The evidence scan finds no names, emails, codes, tokens or key material in captured telemetry.

## Manual spot check (optional)

```bash
docker compose exec postgres psql -U gozleme_app -d gozleme -c "SELECT count(*) FROM gozleme.organizations"   # → 0
```

## Key rotation

1. Set a new `GOZLEME_CONTEXT_KEY` (Compose) or `rls-context-key` (Helm secret).
2. Re-run the migrator (`docker compose up -d migrate`, or the Helm pre-upgrade job).
3. Restart the API.

Between steps 2 and 3, organization data is unreachable (fail closed), and `gozleme.tenant.denials{reason="invalid"}` rises.
