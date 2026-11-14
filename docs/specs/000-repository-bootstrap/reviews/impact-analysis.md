# GOZ-0000 post-convergence spec impact analysis

**Source spec:** GOZ-0000 — Repository bootstrap and executable engineering foundation
**Implementation revision:** `e7904ad` on `chore/goz-0000-bootstrap` (demo recorded at `01fc90b`; `e7904ad` changes only regenerated evidence files)
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
| GOZ-0000-IF-07 | low | Web validator is stricter than OpenAPI for `capabilities` | 0000 (+0009–0011, 0015) | corrective implementation recommended |
| GOZ-0000-IF-08 | medium | Demo runner and evidence layout are hard-coded to GOZ-0000; no seed path | 0001, 0039 | applied to prompts |
| GOZ-0000-IF-09 | medium | Fixture `ScalarAsync` defaults to superuser and would bypass RLS; shared fixture | 0003, 0012, 0013, 0015, 0017 | applied to prompts |
| GOZ-0000-IF-10 | medium | Migration mechanism, pre-upgrade compatibility, out-of-band role creation | 0001, 0003, 0004 | applied to prompts |
| GOZ-0000-IF-11 | medium | OpenTelemetry has no owning spec | 0023, 0039 | **requires human approval** |
| GOZ-0000-IF-12 | high | Protected `main` not applied or verified (HTTP 403), invariant 12 | 0000, all | **requires human action** |
| GOZ-0000-IF-13 | medium | Secret-reference, read-only rootfs and Compose conventions; Data Protection keys; artifact volume; live Helm validation | 0001, 0007, 0009, 0010, 0011, 0015, 0016, 0039 | applied to prompts |
| GOZ-0000-IF-14 | medium | No two-instance local harness for federation | 0029, 0031, 0032, 0033, 0036 | applied to prompts |
| GOZ-0000-IF-15 | low | Backup/restore must recreate out-of-band roles | 0039 | applied to prompts |
| GOZ-0000-IF-16 | low | `InvariantGlobalization=true` affects ID normalization work | 0005 | applied to prompts |
| GOZ-0000-IF-17 | low | `research.md` R7 describes CI that does not match `ci.yml` | 0000 | corrective (docs) recommended |
| GOZ-0000-IF-18 | low | `scripts/__pycache__/*.pyc` is tracked despite `.gitignore` | 0000 | corrective recommended |
| GOZ-0000-IF-19 | low | No JS dependency vulnerability check; NuGet audit is only the SDK default | 0000 | corrective recommended (non-blocking) |

The evidence, recommendation and file and line references for each finding are in `impact-findings.json`.

## Prompt edits applied (within authority)

Each edited prompt has an appended section, `### Implementation alignment (GOZ-0000-IF-…, 2026-11-14)`, with provenance. The section only adds implemented facts and the obligations that follow from them. It changes no outcome, scope, boundary, prerequisite, security invariant or MVP commitment.

GOZ-0001, 0002, 0003, 0004, 0005, 0007, 0009, 0010, 0011, 0012, 0013, 0014, 0015, 0016, 0017, 0023, 0029, 0031, 0032, 0033, 0036, 0039 (22 prompts). The Roadmap Steward must bump `promptRevision` from 1 to 2 for each of them. No dependency changes are proposed. The DAG is unchanged.

Not edited, because no implementation fact changes their starting prompt: GOZ-0006, 0008, 0018, 0019, 0020, 0021, 0022, 0024, 0025, 0026, 0027, 0028, 0030, 0034, 0035, 0037, 0038.

## Items requiring human decision or action

1. **GOZ-0000-IF-11:** Assign OpenTelemetry to an MVP spec (for example GOZ-0039), or record it as post-MVP. This is an MVP-scope change, so it was not edited.
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
