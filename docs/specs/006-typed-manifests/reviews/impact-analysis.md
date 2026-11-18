# GOZ-0006 post-convergence spec impact analysis

**Source spec:** GOZ-0006, Typed manifests for capability families
**Implementation revision:** `b492064` on `feature/goz-0006-typed-manifests`, after the Phase 10 confirmation-review fixes (T092–T097); **re-analysed 2026-11-18 at `df7916f`** after the Phase 11 maintainer rulings (T098 status codes, T099 shared rate limit; the missing-member contract line of T100 was uncommitted). drive-speckit reports the cumulative demo (goz-0000 to goz-0006) at 51 tests and 81 screenshots in `evidence/demo-run.json` and `evidence/manifest-evidence.json`; the analyst did not re-run tests.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-17; updated 2026-11-18
**Structured findings:** `docs/specs/006-typed-manifests/reviews/impact-findings.json` (15 findings, shape of `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **Diff:** `git diff main...HEAD`. One migration, `20261120000000_ManifestValidationAttempts` (T099), adds `gozleme.manifest_validation_attempts` (`GlobalIdentity`; `account_id`, `window_start`, `attempts`; no manifest data).
- **Code:** `src/Gozleme.Api/Manifests/` (`ManifestEndpoints`, `ManifestContracts`, `ManifestRules`, `ManifestTelemetry`, `ManifestValidationLimiter`, the adapters, `Rules/ReservedClaims`, `SecretPatterns`, `SecretFields`), the diffs of `OperatorCommands.cs` (seed), `PublisherStore.cs`, `OrganizationPermissions.cs`, and `organization-permissions.md`.
- **Tests (pins):** `BootstrapBoundaryTests` (`Manifest_validation_adds_only_its_rate_limit_counter`, renamed in T099), `MigrationAndRoleTests` (7 migrations), `LATEST_MIGRATION` `/_ManifestValidationAttempts$/`, `StatusEndpointTests`, `ManifestValidationLimiterTests`, `OrganizationPermissionContractTests` (18), `TenantSeedTests`, the list of manifest suites, and the e2e project `goz-0006`.
- **Contracts and decisions:** every file in `contracts/`, `progress.yaml` decisions and agent choices, and spec FR-001 to FR-027.
- **Downstream prompts:** GOZ-0007 to GOZ-0041. GOZ-0007 depends on GOZ-0006 directly, and GOZ-0008 to GOZ-0039 depend on it transitively. GOZ-0040 and GOZ-0041 do not depend on it, but they merge onto a `main` that runs its pins.

## Findings

| ID | Sev | Finding | Affected | Disposition |
|---|---|---|---|---|
| GOZ-0006-IF-01 | high | Validation contract v1: endpoint, check order, closed envelope, adapters, result, projection, 11 categories, 40 rules, bounds | 0007–0039 | applied |
| GOZ-0006-IF-02 | high | Pins: one migration and the counter table, the renamed boundary test, 7 migrations, 18 endpoints, fixed seeded publisher and capability, `goz-0006` project | 0007–0041 | applied (updated 2026-11-18) |
| GOZ-0006-IF-03 | medium | Span, counters, outcomes; no audit (1501 still free); validation limit shared through PostgreSQL (shared-rate-limit) | 0007–0041 | applied (updated 2026-11-18; GOZ-0005-IF-05 stays open for the sign-in and invitation-redeem limits only) |
| GOZ-0006-IF-04 | high | Reserved vocabulary: trust, approval, visibility, lifecycle and signature claims are registry-side only | 0007–0039 (specific: 0007, 0012, 0013, 0019, 0021, 0029, 0030, 0033) | applied |
| GOZ-0006-IF-05 | high | Secret detection is best-effort; a split `Bearer`/`Basic`/`Digest` header token is undetected. Persisting, indexing, embedding and export inherit this | 0007, 0013, 0015, 0016, 0028, 0031, 0032 | **decided** 2026-11-18 (stored-text-projection-only: search and embed projection fields only; raw text stored and exportable as sent; raw indexing or embedding needs its own decision); applied |
| GOZ-0006-IF-06 | high | GOZ-0007 starts storage: reuse the validator, store bytes and digest, record adapter and projection versions, narrow the pins | 0007 | applied |
| GOZ-0006-IF-07 | medium | Entry uniqueness is enforced only by `mcp/1` (A2A skill IDs, OpenAPI operation IDs) and affects child keys | 0008, 0021 | applied (routed to clarification) |
| GOZ-0006-IF-08 | medium | The projection has no digest, and an extension-only edit changes only the native digest | 0007, 0022, 0028, 0036 | applied (routed to clarification) |
| GOZ-0006-IF-09 | medium | Envelope v1 has no dependency field, and extension members are never projected | 0024–0027 | applied (routed to clarification) |
| GOZ-0006-IF-10 | medium | A foreign-origin claim is `identity_mismatch`, so import needs an origin-qualified target | 0030–0032, 0036 | applied |
| GOZ-0006-IF-11 | low | Reusable restricted parsers and catalogue pattern; the skill directory rule is not enforced | 0009, 0018 | applied |
| GOZ-0006-IF-12 | medium | Declared lists and the provider are advisory, and nothing is executed | 0008, 0019, 0037, 0038 | applied |
| GOZ-0006-IF-13 | high | Agent choices disclosed for veto become stored data in GOZ-0007 | 0007, 0016 | **decided** 2026-11-18: 10 ratified as built (agent-choices-ratified); A3 replaced by result-status-codes (200 / 400 / 413 / 422); in-memory limit replaced by shared-rate-limit; `manifest.validate` stays on `capability`, and GOZ-0007 introduces a `manifest` resource type. Applied |
| GOZ-0006-IF-14 | low | The Corvus analyzer loads only in the container restore, so build the images before each demo; schema digests are pinned | all | applied |
| GOZ-0006-IF-15 | medium | Index and embed projection fields only | 0013, 0015–0017, 0031, 0032 | applied (maintainer decision stored-text-projection-only) |

No finding is critical. None changes the DAG, an outcome, a prerequisite or an invariant. No implementation defect needs a corrective iteration. The known split-header limit was decided by the maintainer and is recorded, not reopened. Its downstream consequences (IF-05) were decided by the maintainer on 2026-11-18. No finding still needs a human decision.

## Prompt edits applied (within authority)

- **GOZ-0007 to GOZ-0039:** a new section, `### Typed manifest conventions (GOZ-0006-IF-01, IF-02, IF-03, IF-04, IF-05, IF-14, 2026-11-17)`. It supersedes the GOZ-0005 endpoint count (17 → 18) and extends the pin list.
- **GOZ-0040 and GOZ-0041:** a short conventions section (IF-02, IF-03, IF-14).
- **Spec-specific `### Implementation alignment (GOZ-0006-…)` sections:** 0007, 0008, 0009, 0012, 0013, 0015, 0016, 0018, 0019, 0021, 0022, 0024, 0028, 0029, 0030, 0033, 0037, 0038 and 0039.
- **2026-11-18 update:**
  - rejected answers `400`, `413` or `422`, not `200`;
  - FR-017 clarified, with the counter table and the new pins;
  - the shared limit;
  - the projection-only ruling (0007, 0013, 0015, 0016, 0028, and new sections in 0031 and 0032);
  - the ratified choices and the `manifest` resource type in 0007.

  The update is part of the same unmerged prompt revision.
- **Roadmap Steward:** bump `promptRevision` 7 → 8 for GOZ-0007 to GOZ-0039 and 5 → 6 for GOZ-0040 and GOZ-0041, list these findings on GOZ-0006, and add this review to its evidence.
