# GOZ-0005 post-convergence spec impact analysis

**Source spec:** GOZ-0005, Publisher identities and canonical capability IDs
**Implementation revision:** `536faf3` on `feature/goz-0005-publisher-identities`, after convergence (T053–T060: claim handle bound to its publisher, `publisherId` → `canonicalId`, isolation, origin and operator-audit tests, living docs). drive-speckit reports the cumulative demo at 46/46 (goz-0000 to goz-0005) at `03ead47`, recorded in `evidence/demo-run.json` and `evidence/publisher-evidence.json`.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-17
**Structured findings:** `docs/specs/005-publisher-identities/reviews/impact-findings.json` (14 findings; validates against `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **Diff:** `git diff main...HEAD` (146 files; 55 under `src`, `web`, `e2e`, `deploy` and `Makefile`).
- **Migration:** `20261119000000_PublisherIdentities.cs`, read in full: `account_has_role`, `immutable_row_guard`, `registry_origin`, `publisher_registration_attempts`, `publishers`, `capabilities`, `handle_claims`, `alias_reservations`, every policy, the guards, `capabilities_reserve_alias`, and `Down`.
- **Application code:** `Publishers/` (`RegistryOrigin.cs`, `NamespaceRules.cs`, `PublisherEndpoints.cs`, `PublisherStore.cs`, `PublisherContracts.cs`, `PublisherTelemetry.cs`, `PublisherServiceCollectionExtensions.cs`), `Identity/HandleOperatorCommands.cs`, the diffs of `OperatorCommands.cs` (seed), `SecurityAudit.cs` (1401–1408), `OrganizationPermissions.cs`, `TableClassification.cs`, `RlsCatalog.cs`, `Program.cs` and `GozlemeApp.cs`.
- **Rate limiters:** `IdentityServiceCollectionExtensions.cs` and `OrganizationServiceCollectionExtensions.cs` (in-memory) against `PublisherRegistrationLimiter` (PostgreSQL).
- **Deployment and process:** Helm `values.yaml`, `api.yaml`, `migrate-job.yaml`, `Makefile`, `.github/workflows/ci.yml`, `compose.yaml`, `e2e/tests/evidence.ts`, `scripts/demo/run-demo.sh`.
- **Tests (pins only):** `BootstrapBoundaryTests`, `MigrationAndRoleTests`, `StatusEndpointTests`, `OrganizationPermissionContractTests`, `OrganizationBoundaryTests`, and the list of new suites (`HandleClaimTests`, `OperatorHandleCommandTests`, `PublisherEndpointTests`, `PublisherIsolationTests`, `PublisherRegistrationLimiterTests`, `PublisherTelemetryTests`, `RegistryOriginTests`).
- **Contracts:** `contracts/namespace.md`, `operator-commands.md`, `permissions-delta.md`, and the living `docs/architecture/contracts/organization-permissions.md`. Also the amended invariant 2 in `security-invariants.md`, and `progress.yaml` decisions Q1–Q11 and those of 2026-11-17.
- **Downstream prompts:** GOZ-0006 to GOZ-0041. GOZ-0006 to GOZ-0039 depend on GOZ-0005, directly or transitively. GOZ-0040 and GOZ-0041 do not, but they merge onto a `main` that runs GOZ-0005's pins.

## Verified implementation facts that downstream specs consume

| Area | Fact at `536faf3` |
|---|---|
| Identifiers | Only UUIDv7 is stored. `gz://<origin>/pub/<uuid>` and `gz://<origin>/cap/<uuid>` are rendered with the one verified local origin. Both API fields are named `canonicalId`, and `Capability.publisherId` is a UUID. |
| Alias | `<handle>/<name>` is reserved by trigger in the capability's own insert and kept forever. `gozleme_app` holds `INSERT` only; there is no read path. |
| Mutability | Only display names change. Guards refuse every `DELETE`, and `TRUNCATE` is refused for every login on all five new namespace tables. `registry_origin` is immutable. |
| Handles | `ux_handle_claims_live` is unique over blocked, earmarked and held claims. `publishers.handle` is **not** unique. A held claim's handle must equal its publisher's handle (policy `WITH CHECK`). |
| Suspension | Derived: a publisher with no held claim is suspended. Write policies require the held claim, and the API answers `409 suspended`. `reclaimed` is terminal, and `ux_handle_claims_publisher` covers every status, so suspension cannot be lifted. |
| Roles | 18 actions over 5 resources. Admin is an explicit list without capability writes, and `publisher` holds `capability.create` and `capability.rename`. `account_has_role` is a non-definer; `account_is_admin` is unchanged. |
| Classes | `InstanceReference` (`registry_origin`: `SELECT`). `InstanceClaims` (`handle_claims`: `SELECT, INSERT, UPDATE`; `alias_reservations`: `INSERT`), with RLS enabled but not forced. `RlsCatalogTests` pins the exact privilege sets. |
| Operator | `handle-reserve`, `handle-release` and `handle-reclaim` connect as `gozleme_migrator` with no account context, write audit 1406–1408, and have no HTTP surface. |
| Start-up | Origin is required in Compose and Helm. A mismatch refuses `migrate` and the API. When the API runs as PID 1 and start fails, it exits 1. |

## Findings

| ID | Sev | Finding | Affected specs | Disposition |
|---|---|---|---|---|
| GOZ-0005-IF-01 | high | Permanent identifiers, alias, mutability and derived suspension facts | 0006–0039 | applied |
| GOZ-0005-IF-02 | high | Alias resolution has no read path. A resolver changes the pinned `InstanceClaims` privileges and needs a security review | 0012, 0013, 0024, 0027, 0028 | **needs human** (decide before the GOZ-0012 plan) |
| GOZ-0005-IF-03 | medium | A handle resolves only through its held claim; `publishers.handle` is not unique | 0012, 0013, 0024, 0029, 0030, all | applied |
| GOZ-0005-IF-04 | high | Least-privilege operator role instead of the owner login (maintainer asked for this to be tracked) | 0039 (0029 if it adds an operator command) | **needs human** (decide before the GOZ-0039 clarification, or earlier) |
| GOZ-0005-IF-05 | medium | Rate limits from GOZ-0001 and GOZ-0002 are in-process, while publisher registration is counted in PostgreSQL. No spec owns the multi-replica posture | 0039; new limiters in 0009, 0013, 0015, 0038 | **needs human** (decide before the GOZ-0039 clarification); storage note applied |
| GOZ-0005-IF-06 | medium | Required origin, and exit 1 when PID 1 fails to start | all; Helm acceptance in 0039 | applied |
| GOZ-0005-IF-07 | low | `make test` and `make lint` skip `next build`; demo evidence redirection | all | applied (optional Makefile change left to the maintainer) |
| GOZ-0005-IF-08 | medium | Suspension is permanent and cannot be lifted without a schema change | 0007, 0012, 0019, 0021, 0033, 0039 | **needs human** (decide before the GOZ-0039 clarification); per-spec semantics routed to clarification |
| GOZ-0005-IF-09 | medium | Pins, table classes, audit range (next is 1501), seed, permanent demo data | all | applied |
| GOZ-0005-IF-10 | medium | Role table and database-enforcement pattern after GOZ-0005 | 0006–0039 | applied |
| GOZ-0005-IF-11 | medium | Canonical IDs are local-origin only. Federation needs storage qualified by origin, and a decision on whether imported publishers join the local namespace | 0024, 0028–0032, 0036 | applied (clarification question in 0030) |
| GOZ-0005-IF-12 | medium | The child ID form is a new permanent identifier, so it is a maintainer question | 0008 | applied (routed to clarification) |
| GOZ-0005-IF-13 | medium | Catalog and publish routes are still pinned at 404. Cross-tenant references to capabilities can leak existence | 0007, 0012, 0014, 0034 | applied |
| GOZ-0005-IF-14 | medium | Nothing is deletable; organization deletion, retention and restore must account for it | 0035, 0039 | applied |

No finding is critical. None changes the DAG, an outcome, a prerequisite or an invariant. No implementation defect needs a corrective iteration.

## Prompt edits applied (within authority)

- **GOZ-0006 to GOZ-0039:** a new section, `### Publisher identity conventions (GOZ-0005-IF-01, IF-03, IF-06, IF-07, IF-09, IF-10, 2026-11-17)`, with provenance.
  - It covers identifiers, handles, suspension, roles, table classes, pins, telemetry and audit, origin and start-up, seed and demo, and process.
  - It explicitly supersedes four pieces of GOZ-0004 wording: the IF-08 pins, IF-07's "from 1401", carol's seeded role in IF-09, and the IF-02 note that GOZ-0005 would add `account_has_role`.
- **GOZ-0040 and GOZ-0041:** a shorter section (IF-06, IF-07, IF-09). Its lead bullet says these specs do not depend on GOZ-0005.
- **Spec-specific `### Implementation alignment (GOZ-0005-…)` sections:**

| Prompt | Findings |
|---|---|
| 0006 | IF-01 |
| 0007 | IF-01, IF-08, IF-13 |
| 0008 | IF-12 |
| 0009 | IF-01, IF-05 |
| 0012 | IF-02, IF-03, IF-08, IF-13 |
| 0013 | IF-02, IF-05 |
| 0014 | IF-13 |
| 0015 | IF-05 |
| 0018 | IF-10 |
| 0019 | IF-01 |
| 0021 | IF-08, IF-10 |
| 0024 | IF-02, IF-03, IF-11 |
| 0027 | IF-02 |
| 0028 | IF-02, IF-11 |
| 0029 | IF-04, IF-11 |
| 0030 | IF-03, IF-11 |
| 0031, 0032, 0036 | IF-11 |
| 0033 | IF-08 |
| 0034 | IF-13 |
| 0035 | IF-14 |
| 0038 | IF-05 |
| 0039 | IF-04, IF-05, IF-06, IF-08, IF-14 |

- **Open decisions:** bullets for the open decisions are labelled "Open decision" and decide nothing.
- **How the edits were applied:** a script appended them idempotently, skipping any prompt that already contains `GOZ-0005-IF-`. `make validate` passes afterwards.
- **For the Roadmap Steward:**
  - Bump `promptRevision` 6 → 7 for GOZ-0006 to GOZ-0040, and 4 → 5 for GOZ-0041.
  - Record GOZ-0005-IF-01 to IF-14 on the GOZ-0005 entry, with the `impactAnalysis` gate and evidence paths.
  - Record IF-02, IF-04, IF-05 and IF-08 as open decisions, each with its `decideBefore`.

## Maintainer decisions requested

1. **IF-02, alias read path.** Decide before the GOZ-0012 plan.
   - **A.** A `SELECT` grant plus a policy joined to visibility.
   - **B.** A pinned `SECURITY DEFINER` resolver that returns only visible canonical IDs.
   - **C.** No alias resolution in MVP.
2. **IF-04, operator role.** Decide before the GOZ-0039 clarification.
   - **A.** A `gozleme_operator` role in GOZ-0039.
   - **B.** Keep the owner login for MVP, documented.
   - **C.** A new spec.
3. **IF-05, rate limits across replicas.** Decide before the GOZ-0039 clarification.
   - **A.** Single replica is the documented MVP posture.
   - **B.** Shared PostgreSQL limits, with more than one replica supported.
   - **C.** An external store, which is an architecture change.
4. **IF-08, lifting a suspension.** Decide before the GOZ-0039 clarification.
   - **A.** Suspension stays permanent for MVP.
   - **B.** An operator restore command in GOZ-0039 or a new spec.
   - **C.** A post-MVP moderation spec.

## Not asserted

- I did not run `make test`, `make demo` or database reproductions. The pass counts are drive-speckit's.
- The working tree had uncommitted changes to GOZ-0005 evidence screenshots while I worked, apparently from a concurrent demo run. I did not inspect or touch them.
- The existence-leak risk from cross-tenant foreign keys (IF-13) is general PostgreSQL behaviour: referential checks bypass RLS. It is not reproduced here.
- The restore behaviour in IF-14 is inferred from the guard code, as in GOZ-0004-IF-11.
