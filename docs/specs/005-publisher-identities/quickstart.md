# Quickstart: Publisher Identities and Canonical Capability IDs

## Prerequisites

- Docker with Compose, .NET 10 SDK and Node 24, the same as GOZ-0004. No new service, package, extension or database role.
- **New configuration**: `Gozleme:Registry:Origin`.
  - Compose supplies the throwaway default `registry.demo.gozleme.test` through `GOZLEME_REGISTRY_ORIGIN`. Helm requires `registry.origin`.
  - An existing local volume needs **no** `make reset`. `make up` runs the migrator, which applies `20261119000000_PublisherIdentities` and records the configured origin.
- Once an origin is recorded, changing `GOZLEME_REGISTRY_ORIGIN` makes `migrate` and `api` refuse to start until it is restored ([contracts/namespace.md](./contracts/namespace.md)).

## Run the automated checks

```bash
make test       # .NET unit + Testcontainers integration (real PostgreSQL 17) and Vitest
make lint
make validate
make demo       # cumulative Playwright run; goz-0005 is now the highest project
```

Key suites:
- **`PublisherIsolationTests`**: the research R2–R5 database matrices, run as `gozleme_app` and the owner login, plus concurrency.
- **`HandleClaimTests`**: the claim state machine, earmark consumption, reclaim, the retired alias, and the claim tables invisible across organizations.
- **`PublisherEndpointTests`**: API role × action matrix, `handle_unavailable` body equality across callers and owners, non-member and cross-organization `404`s, suspension `409`s, rate limit.
- **`RegistryOriginTests`**: record once, migrator and API refusal on a mismatch, outage start, rendering.
- **`OperatorHandleCommandTests`**: exit codes, no-HTTP-route check, audit fields.
- **`NamespaceRulesTests`**: the valid and invalid table under invariant globalization.
- **`PublisherTelemetryTests`**: spans, counter labels, audit 1401–1408, no private values.
- **Contract and boundary pins**: updated `OrganizationPermissionContractTests` and `RlsCatalogTests` (new classes and their negative tests), plus the pins in research R12.

## Demo walkthrough (what `make demo` drives)

The demo runs `docker compose --profile demo run --rm seed`. Afterwards, in **Seeded Demo Org**, alice holds `{admin, member}` and carol holds `{member, publisher}`. dave is the admin of **Seeded Partner Org**.

1. **Register** (US1). alice opens *Publishers* and registers `demo-NNNNNN` ("Demo Tools").
   - The confirmation shows the handle, the organization's opaque ID and `gz://registry.demo.gozleme.test/pub/…`.
   - Screenshots are taken. alice sees no *Create capability* form.
2. **Create** (US1). carol opens the publisher and creates `weather-lookup` ("Weather Lookup"). The page shows the canonical ID `gz://registry.demo.gozleme.test/cap/…` and the alias `demo-NNNNNN/weather-lookup`.
3. **Rename twice** (SC-001). carol renames it to "Weather Lookup v2", then "Forecasts". The canonical ID and alias are byte-identical to step 2, checked in the UI and through the API.
4. **Unavailable** (US2). dave registers `demo-NNNNNN` in Seeded Partner Org and gets `409 {"error":"handle_unavailable"}`. The body is byte-identical to alice's own retry.
5. **Alias conflict** (US2). carol creates `weather-lookup` again under the same publisher and gets `409 conflict`.
6. **Role matrix** (US3, SC-005). Every built-in role × new action is checked through the API against the contract table, and a non-member gets `404`.
7. **Database refusals** (US2.6, SC-002). As `gozleme_app` (pattern `asApp`) and as the owner, every immutable-field change and delete is refused, and the rows are unchanged.
8. **Origin mismatch** (US2.1, SC-003). The API is recreated with `GOZLEME_REGISTRY_ORIGIN=other.demo.gozleme.test`. It exits non-zero with "configured origin differs from the recorded origin". It is restored with `composeWithEnv` and the IDs from step 2 are unchanged.
9. **Reserve and reclaim** (US5, SC-010).
   - `docker compose run --rm migrate handle-reserve --handle acme-NNNNNN --organization <Seeded Demo Org>` and `… handle-reserve --handle blocked-NNNNNN` run first.
   - dave gets `handle_unavailable` for both handles. alice registers `acme-NNNNNN`.
   - dave registers `squat-NNNNNN` and creates `forecast` under it.
   - `handle-reclaim --handle squat-NNNNNN --organization <Seeded Demo Org>` runs. dave's page shows the publisher and capability **Suspended**, with the same IDs, and no forms. Screenshots are taken.
   - alice registers `squat-NNNNNN`. carol's attempt to create `forecast` under it gets `409 conflict`, and `radar` succeeds.
10. **Boundary**. `GET /api/v1/capabilities` and `POST /api/v1/publish` still answer `404`, and no `/handles`, `/reclaim` or `/reservations` route exists.
11. **Telemetry**. Jaeger shows `gozleme.publisher.register` and `gozleme.capability.create`, and the metrics scrape shows `gozleme.publisher.operations`. A no-secret scan of captured spans and logs finds no handle, name, email, `v1.` context or key beyond the synthetic values shown in the UI.
12. **Regression**: the `goz-0000` … `goz-0004` suites, with screenshots under `evidence/screenshots/regression-goz-NNNN/`.

Evidence is written to `docs/specs/005-publisher-identities/evidence/`: `demo-run.json`, `publisher-evidence.json` and `screenshots/`.

## Operator commands by hand

```bash
docker compose run --rm migrate handle-reserve --handle acme --organization 0193a000-0000-7000-8000-000000000002
docker compose run --rm migrate handle-reserve --handle gozleme-official
docker compose run --rm migrate handle-release --handle gozleme-official
docker compose run --rm migrate handle-reclaim --handle acme-labs --organization 0193a000-0000-7000-8000-000000000002
```

Exit codes and messages are in [contracts/operator-commands.md](./contracts/operator-commands.md).
