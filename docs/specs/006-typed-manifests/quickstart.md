# Quickstart: Typed Manifests for Capability Families

## Prerequisites

- Docker with Compose, .NET 10 SDK and Node 24, the same as GOZ-0005. No new service, configuration or database change.
- New NuGet packages: `Corvus.Text.Json.Validator` and `YamlDotNet`, pinned in `Directory.Packages.props` (research R17). The bundled schemas need no network at run time.
- An existing local volume needs no `make reset`. Run `make up`, then the seed (`docker compose --profile demo run --rm seed`), which adds the fixed demo publisher `seeded-demo` and capability `manifest-demo` (research R14).

## Run the automated checks

```bash
make test       # .NET unit + Testcontainers integration (PostgreSQL 17) and Vitest
make lint
make build      # includes next build (GOZ-0005-IF-07)
make validate
make demo       # cumulative Playwright run; goz-0006 is now the highest project
```

Key suites:

| Suite | Checks |
|---|---|
| `ManifestSampleTests` | every entry of `web/src/samples/manifests/index.json` yields exactly its expected outcome, category, rule and location (SC-001) |
| `ManifestParserTests` | JSON duplicate keys; YAML anchors, aliases, tags, merge keys and multi-document input; core-schema scalars (`yes` stays a string); BOM; lone surrogate; line and column |
| `ManifestBoundsTests` | each bound in [contracts/manifest-validation.md](./contracts/manifest-validation.md); `413` for over 1 MiB |
| `SecretPatternTests` | pattern list v1 positives, near-miss negatives and accepted forms |
| `ReservedClaimTests` | the vocabulary in extension positions; `components.schemas.*.properties.owner` and OpenAPI `deprecated` are not flagged |
| `ProjectionBoundaryTests` | per family: byte-identical native, digest, extension and key-order change leaves the projection unchanged, closed projection schema (User Story 3, SC-003) |
| `McpManifestTests` | 2026-07-28 versus 2025-11-25 typing, mode derivation, `_meta` key rules, consistency rules |
| `ManifestEndpointTests` | role × action matrix, non-member and cross-organization `404` identical, suspended `409` before parse, `429` after authorization, `415`, determinism |
| `ManifestNoStorageTests` | row counts and schema unchanged after validations (SC-007) |
| `ManifestTelemetryTests` | span, counters with bounded labels, no sample strings in spans or logs |
| `ManifestPerformanceTests` | a 512 KiB OpenAPI sample in under 2 s (SC-006) |
| `ManifestSchemaPinTests` | bundled schema digests match `SOURCES.md`; an unbundled `$ref` fails without fetching |

Pins and contract tests that change: `OrganizationPermissionContractTests` (19 actions, 18 scoped endpoints)
and `EndpointAuthorizationTests`. Migration pins do **not** change.

## Demo walkthrough (what `make demo` drives)

After the seed, carol holds `{member, publisher}` and alice holds `{admin, member}` in **Seeded Demo Org**.

1. **Validate every family** (User Story 1, SC-002). carol opens *Seeded Demo Publisher* → *Manifest Demo* → **Validate manifest**. For each family she picks the family, loads the valid sample and presses **Validate**. The page shows three separate panels: *Envelope*, *Native document* (unchanged, with `sha256:` digest) and *Projection* (version 1). Screenshots are taken for all six families and for both MCP samples, so the MCP projection shows `mode: stateless` and `mode: stateful`.
2. **Rejections** (User Story 2). Every invalid sample is loaded and validated. The problem list shows category, rule, location and message, and screenshots are taken. A DOM scan confirms that no synthetic secret value appears on the page.
3. **Boundary** (User Story 3). Through the API, for each family, the test checks:
   - the returned `native.document` equals the submitted string;
   - the digest matches a local SHA-256;
   - a copy with an added extension member gives a different digest and a deep-equal projection.
4. **Roles** (User Story 4, SC-005).
   - alice sees no **Validate manifest** control, and a direct `POST` answers `403`.
   - Every built-in role is checked against the contract row.
   - dave (Seeded Partner Org) gets the identical empty `404` with Seeded Demo Org's IDs.
   - A capability of a publisher that the demo suspends with `handle-reclaim` answers `409`.
5. **Rate limit**. 31 validations inside a minute give `429` with `Retry-After`. The span outcome is `rate_limited`, and no `gozleme.auth` failure is recorded.
6. **Nothing stored** (SC-007). As `postgres`, row counts of every `gozleme` table and `__EFMigrationsHistory` are the same before and after the suite.
7. **Telemetry**. Jaeger shows `gozleme.manifest.validate` spans with family and outcome, and the metrics show `gozleme.manifest.validations` and `gozleme.manifest.problems`. A no-secret scan of captured spans and logs finds no sample document text, synthetic secret, capability name, alias or native name.
8. **Regression**: the `goz-0000` … `goz-0005` suites, with screenshots under `evidence/screenshots/regression-goz-NNNN/`. `GET /api/v1/capabilities` and `POST /api/v1/publish` still answer `404`.

Evidence is written to `docs/specs/006-typed-manifests/evidence/`: `demo-run.json`, `manifest-evidence.json` and `screenshots/`.

## Validate by hand

```bash
# Inside the web container network, as a signed-in session (see e2e/tests/auth.setup.ts for the cookie):
curl -s -X POST \
  -H 'content-type: application/json' -H 'x-gozleme-client: web' -b "$COOKIE" \
  --data @web/src/samples/manifests/api/valid-openapi-3.1.json \
  http://api:8080/api/v1/organizations/0193a000-0000-7000-8000-000000000002/publishers/0193a000-0000-7000-8000-000000000010/capabilities/0193a000-0000-7000-8000-000000000011/manifests/validate
```

Replace `{{capability}}` in the sample with the capability's canonical ID first (the UI does this for you).
