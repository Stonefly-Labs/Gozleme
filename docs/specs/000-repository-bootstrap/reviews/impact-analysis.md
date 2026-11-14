# GOZ-0000 post-convergence spec impact analysis

**Source spec:** GOZ-0000 — Repository bootstrap and executable engineering foundation
**Implementation revision:** current `8ce080c` (stored telemetry `9c6d32d`, fixes `511b904`); before that `33b4cef` (foundational OpenTelemetry `ba6f28c`); earlier passes at `e7904ad` and `930128e` on `chore/goz-0000-bootstrap` (corrective commit `3273724`; demo re-run on a clean tree recorded at `b4d82e1`, and `930128e` adds only evidence)
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-14
**Structured findings:** `docs/specs/000-repository-bootstrap/reviews/impact-findings.json` (validates against `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- Code: `src/Gozleme.Api` (host composition, status probe/contract, `migrate` and `healthcheck` commands, `GozlemeDbContext`, migration `20261114000000_Foundation`), `web/` (dashboard, `/api/backend-status` proxy, `lib/status.ts` validator).
- Database and deployment: `deploy/postgres/init/01-roles.sh`, `compose.yaml`, `.env.example`, `deploy/helm/gozleme` (values, api/web/migrate-job templates, README), `Makefile`, `.github/workflows/ci.yml`.
- Tests and evidence: `tests/Gozleme.Api.IntegrationTests` (fixture, boundary, roles, outage, optional vector), `tests/Gozleme.Api.Tests`, `e2e/` Playwright suite, `scripts/demo/*`, `evidence/demo-run.json` (result `passed`, 6 assertions, 4 screenshots).
- Contract: `contracts/status-api.openapi.yaml` (v1).
- Live, read-only checks on the running Compose stack, plus DDL attempts inside transactions that were rolled back: extension control files, `pg_default_acl`, schema ownership, `CREATE EXTENSION` as `gozleme_migrator`, `SET LOCAL` of a custom GUC as `gozleme_app`.
- Downstream: every prompt GOZ-0001 … GOZ-0039. All of them depend on GOZ-0000, directly or transitively. I read each prompt's specific outcome, scope and boundary against the implemented foundation.

## Verified implementation facts that downstream specs consume

| Area | Fact at `e7904ad` |
|---|---|
| Roles | `gozleme_migrator` is `NOSUPERUSER NOBYPASSRLS` and owns the DB, schema `gozleme` and every object it creates. `gozleme_app` is `NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT`, with `CONNECT, TEMPORARY` and no DDL. Both roles are created only by the init script, on a fresh volume. |
| Schema | Empty schema `gozleme`. Default privileges give `gozleme_app` `arwd` on future tables and `rU` on future sequences. `gozleme_app` has `SELECT` on `public."__EFMigrationsHistory"`. |
| Migrations | EF Core. The one-shot `migrate` command runs as the Compose `migrate` service and as the Helm `pre-install,pre-upgrade` Job. The API never holds DDL rights. |
| RLS / tenancy | None. `gozleme_app` can `SET LOCAL app.tenant_id` freely (verified). |
| Extensions | `vector` 0.8.6 is available but **untrusted**, so the migrator gets "Must be superuser". `pg_trgm` is trusted, so the migrator can create it. |
| Status | `/health/live`, `/health/ready` and `/api/v1/status` (v1, strict schema) are anonymous. Overall status is `degraded` only when a required dependency is unhealthy. |
| Web | The browser reaches the API only through Next.js route handlers (`GOZLEME_API_URL`, server side). |
| Tests | `GozlemeDatabase` fixture (roles script plus real migrator; connection strings for 3 roles; `ScalarAsync` defaults to superuser), `GozlemeApiFactory`, and a shared assembly fixture. |
| Demo | `scripts/demo/run-demo.sh` and the e2e suite are hard-coded to GOZ-0000 paths. CI `e2e-demo` runs them. No seed mechanism exists. |
| Deploy | Helm uses `database.existingSecret` (`app-connection-string`, `migrator-connection-string`) and `readOnlyRootFilesystem` with only `/tmp` writable. It has been linted and rendered, never installed on a live cluster. |
| Observability | JSON console logs only. OpenTelemetry was deferred, and no spec owns it. |

## Findings

| ID | Sev | Finding | Affected specs | Disposition |
|---|---|---|---|---|
| GOZ-0000-IF-01 | high | GOZ-0000 boundary tests (404s, empty schema) will fail by design as identity, organization, catalog, search and publish surfaces arrive | 0001, 0002, 0005, 0007, 0012, 0013 | applied to prompts |
| GOZ-0000-IF-02 | high | Anonymous status, health and dashboard surfaces, plus Compose/Helm healthchecks, vs GOZ-0001 "protected dashboard" | 0001 | applied to prompts |
| GOZ-0000-IF-03 | high | Role, ownership and default-privilege facts; no RLS; GUCs settable by the app role; FORCE RLS decision; same-migration RLS | 0001, 0002, 0003, 0012, 0014 | applied to prompts |
| GOZ-0000-IF-04 | medium | Default `UPDATE/DELETE` grants vs immutable or append-only tables | 0007, 0009, 0023, 0032 | applied to prompts |
| GOZ-0000-IF-05 | high | `vector` cannot be created by the migrator; `pg_trgm` can; `vector.enabled` semantics | 0013, 0015, 0017, 0039 | applied to prompts |
| GOZ-0000-IF-06 | medium | Status contract v1 rules for adding dependencies | 0009, 0010, 0011, 0015, 0016 | applied to prompts |
| GOZ-0000-IF-07 | low | Web validator is stricter than OpenAPI for `capabilities` | 0000 (+0009–0011, 0015) | corrected in `3273724` |
| GOZ-0000-IF-08 | medium | Demo runner and evidence layout are hard-coded to GOZ-0000; no seed path | 0001, 0039 | applied to prompts |
| GOZ-0000-IF-09 | medium | Fixture `ScalarAsync` defaults to superuser and would bypass RLS; shared fixture | 0003, 0012, 0013, 0015, 0017 | applied to prompts |
| GOZ-0000-IF-10 | medium | Migration mechanism, pre-upgrade compatibility, out-of-band role creation | 0001, 0003, 0004 | applied to prompts |
| GOZ-0000-IF-11 | medium | OpenTelemetry has no owning spec | all (0001–0039) | decided by maintainer 2026-11-14: foundational; implemented in `ba6f28c`; applied to all prompts |
| GOZ-0000-IF-12 | high | Protected `main` not applied or verified (HTTP 403), invariant 12 | 0000, all | **requires human action** |
| GOZ-0000-IF-13 | medium | Secret-reference, read-only rootfs and Compose conventions; Data Protection keys; artifact volume; live Helm validation | 0001, 0007, 0009, 0010, 0011, 0015, 0016, 0039 | applied to prompts |
| GOZ-0000-IF-14 | medium | No two-instance local harness for federation | 0029, 0031, 0032, 0033, 0036 | applied to prompts |
| GOZ-0000-IF-15 | low | Backup/restore must recreate out-of-band roles | 0039 | applied to prompts |
| GOZ-0000-IF-16 | low | `InvariantGlobalization=true` affects ID normalization work | 0005 | applied to prompts |
| GOZ-0000-IF-17 | low | `research.md` R7 describes CI that does not match `ci.yml` | 0000 | corrected in `3273724` |
| GOZ-0000-IF-18 | low | `scripts/__pycache__/*.pyc` is tracked despite `.gitignore` | 0000 | corrected in `3273724` |
| GOZ-0000-IF-19 | low | No JS dependency vulnerability check; NuGet audit is only the SDK default | 0000 | corrected in `3273724` |
| GOZ-0000-IF-20 | high | The browser telemetry ingestion route is unauthenticated, so spans can be spoofed (forged `service.name`) and the route abused | 0001, 0039 | applied to prompts |
| GOZ-0000-IF-21 | high | Telemetry is sensitive, cross-tenant and visible to operators. `db.query.text`, `http.url`/`http.target` and `user_agent.original` are captured, and redaction covers only credential-like data | 0001–0003, 0012–0017, 0034 | applied to prompts |
| GOZ-0000-IF-22 | medium | Whether to ship stored or searchable logs and telemetry, and how long to keep them | 0023, 0035, 0039 | decided by maintainer 2026-11-14 (local stack at least); implemented in `9c6d32d`; applied to prompts |
| GOZ-0000-IF-23 | low | Collector config is not hot-reloaded (already documented). Helm ships no collector, so operators must supply their own redaction | 0039 | corrected in `511b904` |
| GOZ-0000-IF-24 | low | Browser ingestion route drops any path prefix of `OTEL_EXPORTER_OTLP_ENDPOINT` | 0000, 0039 | corrected in `511b904` |
| GOZ-0000-IF-25 | medium | Anonymous Grafana, Loki with auth disabled, and Prometheus and Jaeger with no authentication are local-only and must never become Helm or production defaults | 0039 | applied to prompts |
| GOZ-0000-IF-26 | low | Jaeger traces are in-memory, so log-to-trace links dangle after a restart; `make reset` help text understates what it deletes | 0039 (+0000 docs) | applied to prompts; minor docs fix recommended |

The evidence, recommendation and file and line references for each finding are in `impact-findings.json`.

## Prompt edits applied (within authority)

Each edited prompt has an appended section, `### Implementation alignment (GOZ-0000-IF-…, 2026-11-14)`, with provenance. The section only adds implemented facts and the obligations that follow from them. It changes no outcome, scope, boundary, prerequisite, security invariant or MVP commitment.

GOZ-0001, 0002, 0003, 0004, 0005, 0007, 0009, 0010, 0011, 0012, 0013, 0014, 0015, 0016, 0017, 0023, 0029, 0031, 0032, 0033, 0036, 0039 (22 prompts). The Roadmap Steward must bump `promptRevision` from 1 to 2 for each of them. No dependency changes are proposed. The DAG is unchanged.

Not edited, because no implementation fact changes their starting prompt: GOZ-0006, 0008, 0018, 0019, 0020, 0021, 0022, 0024, 0025, 0026, 0027, 0028, 0030, 0034, 0035, 0037, 0038.

## Items requiring human decision or action

1. **GOZ-0000-IF-11:** The maintainer decided on 2026-11-14 that OpenTelemetry is foundational and includes the browser. It is implemented and applied, so this item is closed.
2. **GOZ-0000-IF-12:** Enable, apply and verify the protected-`main` ruleset (blocker GOZ-0000-B1). Do not claim enforcement until it is verified.
3. **Guardrail noted in GOZ-0015 (IF-05):** Granting superuser to `gozleme_migrator` or `gozleme_app` so that `vector` can be created would weaken the role separation behind invariant 2. It requires human approval and is not recommended.

## Corrective GOZ-0000 items for `drive-speckit` (bounded)

- **IF-07:** Align `web/src/lib/status.ts` `isDependency` with the OpenAPI (accept a `capabilities` object whose `vector` is absent or valid), or make `vector` required in the OpenAPI. Add a Vitest case either way.
- **IF-17:** Correct `research.md` R7. There is no `containers` job: images are built in `e2e-demo`, and actions are SHA-pinned.
- **IF-18:** `git rm --cached -r scripts/__pycache__`.
- **IF-19 (optional):** Add `pnpm audit --prod` (or dependency review) and set `NuGetAudit`/`NuGetAuditMode` explicitly.

None of these is a security or architecture violation. If any code change is made, run the impact analysis again on the updated head, and re-run the demo if `web/` changes.

## Not asserted

- Helm was not installed on a cluster, so no runtime claim is made.
- This analysis performed no merge, push, branch switch or registry edit.

## Re-analysis at `930128e` (2026-11-14)

The corrective commit `3273724` resolves all four GOZ-0000 corrective items. I checked each one in the code:

- **IF-07, corrected.** `web/src/lib/status.ts` now accepts `capabilities` as `null` or an object whose `vector` is absent or valid. `status.test.ts` adds a case that accepts `capabilities: {}` and a case that rejects a malformed `vector`. The dashboard already reads `capabilities?.vector`. `pnpm typecheck` and the Vitest suite (18 tests) pass at HEAD.
- **IF-17, corrected.** `research.md` R7 now matches `ci.yml`: there is no `containers` job, images are built in `e2e-demo`, and actions are SHA-pinned.
- **IF-18, corrected.** `git ls-files scripts/__pycache__` returns nothing.
- **IF-19, corrected.** CI runs `pnpm audit --prod --audit-level low`. `Directory.Build.props` sets `NuGetAudit`, `NuGetAuditMode=all`, `NuGetAuditLevel=low`, and treats NU1901–NU1904 as errors.

**Downstream impact.** The only new downstream effect comes from IF-19. Any later spec that adds a NuGet or production npm package now fails CI on any published advisory, even a low-severity one. Such a spec must resolve the advisory or suppress it explicitly with documentation. It must never disable the audit. This is recorded in the IF-19 evidence. It changes no architecture or scope, so I did not edit any prompt for it.

**Prompt corrections.** The IF-06 sentence in GOZ-0009, 0010, 0011, 0015 and 0016 said the web validator rejects `capabilities` without `vector`. That is no longer true, and the sentence now describes the corrected tolerance. Those prompts are already in the revision 1 → 2 bump list.

**Other code.** Between `3273724` and HEAD, nothing changed outside evidence, prompts and reviews. At the time of that pass, the human items IF-11 and IF-12 were still open. IF-11 has since been decided, as described in the next section.

## Re-analysis at `33b4cef`: foundational observability (2026-11-14)

**Maintainer decision on IF-11.** Quoted verbatim: "otel should be foundational, we want that thread throughout all we do". On browser telemetry: "We prolly want stuff from the client/browser". GOZ-0000 implemented this in `ba6f28c`, and the demo passed 7 of 7 in `33b4cef`. I checked the following in the code:

- `src/Gozleme.Api/Telemetry/GozlemeTelemetry.cs` defines the `Gozleme.Api` ActivitySource and Meter. OTLP export is enabled only when an endpoint is set, and `/health` requests are filtered out of tracing.
- The API and the migrator use named Npgsql data sources.
- The web tier uses `web/src/instrumentation.ts` and propagates trace context only to the API URL.
- Browser tracing lives in `web/src/telemetry/*`, with an ingestion route at `web/src/app/api/otel/v1/traces/route.ts`.
- Compose runs the collector and Jaeger, and neither is a health dependency. Helm wires `telemetry.*` into the api, web and migrate Job.
- `TelemetryTests.cs` and the e2e observability tests cover this.

**Prompt edits.** Every future prompt from GOZ-0001 to GOZ-0039 now ends with the same section, `### Observability (foundational, GOZ-0000-IF-11, 2026-11-14)`. It requires each spec to:

- use the existing pipeline and its naming,
- preserve the browser → web → API → PostgreSQL trace,
- keep secrets and confidential metadata out of telemetry,
- assert its spans and metrics in tests and in the demo.

Each prompt also has one tailored "Spec focus" line. GOZ-0001 decides how the ingestion route is protected (IF-20). GOZ-0003 decides whether an opaque tenant ID may appear in telemetry (IF-21). GOZ-0039 adds a telemetry release gate. No scope, boundary, dependency or invariant changed.

**Live evidence for IF-21.** In local Jaeger, `gozleme-api` spans carry `db.query.text` (the full SQL), `http.url`, `http.target`, `url.path` and `user_agent.original`.

**Human decisions outstanding.**
- IF-22: whether to ship stored or searchable logs and telemetry, and how long to keep them. Prompts were deliberately not edited for this.
- IF-12: protected `main`.

**Corrective GOZ-0000 item.** IF-24: in `route.ts:26`, `new URL("/v1/traces", endpoint)` discards any path prefix of the endpoint. Fix it by appending `/v1/traces` to the base endpoint, and add a unit test with a path-prefixed endpoint. Optional, from IF-23: add a note to the Helm README that operators must apply equivalent redaction in their own collector.

## Re-pass at `8ce080c`: stored telemetry (2026-11-14)

**Maintainer decision on IF-22.** Quoted verbatim: "yes for local stack at the veryleast we want that stuff". `9c6d32d` implements it for the local stack:

- **Logs:** Loki, fed OTLP logs by the collector, stores `trace_id` as structured metadata, with a 7-day retention on the `lokidata` volume.
- **Metrics:** Prometheus scrapes the collector and keeps 7 days on the `promdata` volume.
- **Viewer:** Grafana is provisioned with Loki, Jaeger and Prometheus, with links from logs to traces, on `127.0.0.1:3001`.

Helm still ships no telemetry backends. The demo passed 8 of 8, including the new stored-logs scenario.

**Corrections verified.** Both landed in `511b904`:
- IF-24: `route.ts:27` now keeps the endpoint's path prefix, and a unit test covers it.
- IF-23: the Helm README now requires operator collectors to redact credential-like data.

**Prompt edits.** I added a "Stored telemetry" section with provenance to three prompts:
- **GOZ-0023:** receipts and epochs must never rely on telemetry stores, which are bounded by retention and wiped by `make reset`.
- **GOZ-0035:** CR-07 retention covers the telemetry stores. Private data never enters them, and the retention story states the telemetry retention bound.
- **GOZ-0039:** the release gate covers retention, whether backups include the telemetry volumes, the need for operator-provided backends in Helm, no unauthenticated telemetry in Helm defaults (IF-25), and the in-memory trace limitation (IF-26).

No prompt said that logs go only to the console, so nothing needed correcting there.

**Still open:** IF-12, protected `main`, which is a human action.

