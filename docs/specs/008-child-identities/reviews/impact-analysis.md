# GOZ-0008 post-convergence spec impact analysis

**Source spec:** GOZ-0008, Child capability identities and projections
**Implementation revision:** `a1b3e66` on `feature/goz-0008-child-identities` (convergence pass 4: converged, 0 findings; security review items 1–9 pass). The analyst did not re-run the tests or the demo; the T096 evidence in `progress.yaml` (1050 backend tests, 284 web tests, `make demo` 65/65) is at `a98b4f6`/`e21d833`, and `a1b3e66` changes only docs on top of it. `make validate` passed after the prompt edits.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-19
**Structured findings:** `docs/specs/008-child-identities/reviews/impact-findings.json` (16 findings, shape of `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **Diff:** `git diff main...HEAD` (225 files). One migration, `20261122000000_ChildIdentities`. It adds `capability_children`, `revision_children` and `projection_generations`, with insert guards, a statement-level entry check, two deferred completeness constraint triggers, immutability and truncate guards for every login, and forced-RLS policies. It also replaces `manifest_revisions_select` with an `is_operator()` branch.
- **Code:** `src/Gozleme.Api/Children/**` (assigner, store, endpoints, contracts, entries and publish rules, installer, regenerator, operator command, telemetry). Also the `RevisionPublisher`/`RevisionEndpoints` publication changes, `MigrationRunner` (the install step), `SecurityAudit` 1601–1604, `CanonicalId.Child`, `TableClassification`, `appsettings.json` (`ChildPageSize`), the web child pages and components, and `e2e/tests/children-demo.spec.ts`.
- **Tests (pins):** `MigrationAndRoleTests` (9), `BootstrapBoundaryTests`, `RlsCatalogTests`, `OrganizationPermissionContractTests` (32), `StatusEndpointTests`, `EndpointAuthorizationTests`, `LATEST_MIGRATION` `/_ChildIdentities$/`, the Playwright project `goz-0008`, and the `Child*`, `ProjectionRegenerate*` and `RevisionChildrenPerformance` suites.
- **Decisions:** the `progress.yaml` decisions (Q1–Q3, the two clarify rulings, A2, A4, A7, A8, native-name-bound, F2, F3, F4, F14, nul-pointer, envelope-nul), agent choices A1–A8, and the convergence and security notes.
- **Contracts:** `contracts/*` in the GOZ-0008 workspace, the "Child IDs (GOZ-0008)" section of `docs/specs/005-publisher-identities/contracts/namespace.md`, and `docs/architecture/contracts/organization-permissions.md` (32 endpoints, the publish order and the write table).
- **Downstream prompts:** the registry gives GOZ-0012, 0021, 0024 and 0030 as direct dependants, and GOZ-0012 to GOZ-0039 as the transitive set. Also GOZ-0009 to GOZ-0011, which depend on GOZ-0007 and publish onto the changed publication path, and GOZ-0040 and GOZ-0041, which merge onto a `main` that runs the new pins. All are `not-started`.

The architecture baseline already names "typed child IDs and separately versioned normalized projections". No downstream prompt contradicts the child ID form, the match key, the generation model or the publish delta. No prompt assumes child aliases, mutable children, child lifecycle state, attested child IDs or a re-projection API. The gaps were the implemented facts and the obligations they create, mostly about portability, visibility granularity and approval state. The findings below add them.

## Findings

| ID | Sev | Finding | Affected | Disposition |
|---|---|---|---|---|
| GOZ-0008-IF-01 | high | Child identity contract v1: ID form, no aliases, exact kind + native name match in publication sequence, install-only duplicate-marked children never matched, present/absent only, FR-014 notice and no badges, gap-free ordinal, random UUIDs (matching is deterministic, IDs are not reproducible), not attested | 0009–0041 | applied |
| GOZ-0008-IF-02 | high | Projection generations: generation 1 is the attested projection (no copy). Later generations are unattested and come only from `projection-regenerate` (exit 0/1/2/3/64) with the same key multiset. Generation 1 titles are used (A8). Generation and child IDs are never interchangeable | 0009–0039 | applied |
| GOZ-0008-IF-03 | high | Publish delta: publish rule catalogue v1 (`422` for `publish.duplicate_entry` and `publish.nul_character`, validate unchanged), rollout-gap and child-refusal `503`. Deferred completeness triggers refuse to commit any revision insert without generation 1 and full child records | 0009–0041 (specific: 0009) | applied |
| GOZ-0008-IF-04 | high | Pins: migration, 3 tables, `is_operator()` read branch, 9 migrations, 32 endpoints (11 `manifest.read`, 2 `manifest.list`), 12 tenant-owned tables, `goz-0008` project, `ChildPageSize`, install step in `migrate`; `manifest-demo` accumulates children and generations | 0009–0041 | applied |
| GOZ-0008-IF-05 | medium | Spans, counters (recorded after commit), audit 1601–1604 (range 1601–1610 held, next module 1701), and no names or digests anywhere | 0009–0041 | applied |
| GOZ-0008-IF-06 | medium | Org-private reads on `manifest.list`/`manifest.read` with no new action; `405` on writes. The `is_operator()` branches must never back a public path. `presentIn`, history, `ordinal`, `latest` and absent state are computed over every revision | 0012–0039 (specific: 0012, 0034) | applied (clarification routed to 0012, 0034) |
| GOZ-0008-IF-07 | high | Child UUIDs are local, random and unattested, so they are not portable. The contract defers portability | 0021, 0024, 0028, 0030, 0031, 0032, 0036 | applied (clarification routed to 0021, 0024, 0028, 0030) |
| GOZ-0008-IF-08 | medium | Approval and materiality boundary: no approval state or badges in contract v1. Entries carry only kind, native name and title. A later generation is not a material change | 0019, 0021, 0022, 0038 | applied |
| GOZ-0008-IF-09 | medium | Search and embeddings index from generation 1 child records; which generation to index is open. Projection generations are unrelated to embedding index generations | 0013, 0015, 0017 | applied (clarification routed to 0013) |
| GOZ-0008-IF-10 | medium | Import, snapshot, feed and conflict: local tables are closed to imports; source duplicate keys and U+0000 need an import outcome; later generations raise a feed-event question; child-UUID divergence is not a conflict | 0030, 0031, 0032, 0036 | applied (clarifications routed) |
| GOZ-0008-IF-11 | high | Restore, upgrade and operator surface: guards and deferred triggers bind restores. The install step runs on every `migrate`. A capability with a skipped U+0000 revision answers `503` to publication permanently. The third owner-login command and the read-all operator branch are noted | 0039, 0040, 0041 | applied (routed to 0039) |
| GOZ-0008-IF-12 | medium | Relationship edges and overlays reference children by ID and revision and never write child tables; absent children still resolve | 0014, 0024 | applied |
| GOZ-0008-IF-13 | low | **Question:** the child summary member `latest` (highest-sequence appearance) may read as a latest-revision designation, which the GOZ-0007 ruling excludes | 0008, 0012, 0026, 0027 | **needs-human** (guard note applied) |
| GOZ-0008-IF-14 | medium | The decision register does not record the GOZ-0008 rulings that change contracts or the security posture (Q1, Q2 publish catalogue, A2, A4, F3, F4) | 0008, 0012, 0021, 0028, 0030, 0039 | **needs-human** |
| GOZ-0008-IF-15 | low | The completed GOZ-0007 contract files predate the `422` publish rules and the two new `503` causes | 0007, 0009 | **needs-human** (optional) |
| GOZ-0008-IF-16 | low | The branch changes the evidence of completed specs GOZ-0006 and GOZ-0007: the performance tests rewrite their evidence files (commit `a98b4f6`). The GOZ-0007 publish worst time goes from 478 ms to 1,463 ms (limit 2,000 ms) | 0006, 0007, 0008, 0009, 0039 | **needs-human** |

No finding is critical. None changes the DAG, an outcome, a prerequisite or an invariant. **No GOZ-0008 defect requires code repair.** IF-13 is a naming question; if the maintainer chooses a rename, that becomes a bounded GOZ-0008 code change through `drive-speckit`. The IF-11 restore behaviour was inferred from the migration code and not reproduced.

### Verification notes

- The ID form is checked in `CanonicalId.Child`. Matching is checked in `ChildAssigner`: `ChildKey` uses ordinal compare, and `ExistingChildren.Matchable` excludes duplicates. The class comment states "only the child UUIDs differ", which is the basis of IF-07.
- `manifest_revisions_children` and `capability_children_recorded` are `DEFERRABLE INITIALLY DEFERRED` row triggers on every insert. `RevisionPublisher` runs them before returning (`CheckCompleteAsync`), and `Classify` maps their `23000` errors to `503`.
- `projection_generations_insert_guard` admits generation N > 1 only when `is_operator()` holds and the `(kind, nativeName)` multiset equals the stored projection's. `ck_projection_generations_first` makes generation 1 hold no bytes.
- The endpoints are five `MapGet` calls on `OrgAction.ManifestList`/`ManifestRead`. `PresenceAsync` computes `latest` and `presentIn` over every revision of the capability (IF-06, IF-13).
- The U+0000 skip leaves a revision without generation 1, and `HasUninstalledRevisionAsync` then refuses every publication to its capability. No command adds generation 1 for that revision: `projection-regenerate` answers `not_installed`. This consequence is documented in `contracts/projection-generations.md` and follows from ruling F3.

## Prompt edits applied (within authority)

- **GOZ-0012 to GOZ-0039:** a new section, `### Child identity conventions (GOZ-0008-IF-01, IF-02, IF-03, IF-04, IF-05, IF-06, 2026-11-19)`. It supersedes the GOZ-0007 endpoint count (27 → 32), the migration pins (8 → 9, `/_ChildIdentities$/`), the boundary-test table list, the publish order and the "next module takes 1601" audit note (now 1701).
- **GOZ-0009 to GOZ-0011:** a section with IF-01, IF-03, IF-04 and IF-05, because they publish onto the changed path.
- **GOZ-0040 and GOZ-0041:** a section with IF-04, IF-05 and IF-11 (pins and the operator-command note).
- **Spec-specific `### Implementation alignment (GOZ-0008-…)` sections:** 0009, 0012, 0013, 0014, 0015, 0017, 0019, 0021, 0022, 0024, 0026, 0027, 0028, 0030, 0031, 0032, 0034, 0036, 0038 and 0039. Every clarification question is marked as such and left undecided.
- **Roadmap Steward:**
  - bump `promptRevision` 9 → 10 for GOZ-0009 to GOZ-0039, and 7 → 8 for GOZ-0040 and GOZ-0041;
  - list GOZ-0008-IF-01..IF-16 in GOZ-0008 `impactFindings`;
  - add this review and `impact-findings.json` to GOZ-0008 `evidence.reviews`;
  - record IF-13, IF-14, IF-15 and IF-16 as open pending maintainer decisions, not as resolved.

Nothing was edited in completed specs, `docs/architecture/**`, `docs/decisions/**`, `.gozleme/spec-registry.yaml`, the generated register or any GOZ-0008 code.

## Needs maintainer decision (not edited by the analyst)

**Resolved 2026-11-19 by maintainer ruling:**
- **IF-13:** option (a). `latest` stays, and `contracts/child-identity.md` now says it means "last declared in", not a latest-revision designation. No code change.
- **IF-14:** approved as proposed. Four rows added to `docs/decisions/decision-register.md` under "Maintainer rulings from spec delivery".
- **IF-15:** approved. A one-line pointer to `publish-delta.md` added to the GOZ-0007 `revision-api.openapi.yaml` and `permissions-delta.md`.
- **IF-16:** revert + fix the harness. Both completed-spec evidence files are restored to their `main` versions. The new `Infrastructure/SpecEvidence.cs` skips writing evidence for a spec the registry marks `complete`, while its tests still assert their limits; `SpecEvidenceTests` pins this. A targeted run of the three performance tests left the GOZ-0006 and GOZ-0007 files byte-identical to `main`.

Original escalations:

1. **GOZ-0008-IF-13 (question).** The `ChildSummary.latest` member is described in the API contract as "the latest revision (highest sequence) in which the child is present", and the child list links it as `child-latest`. GOZ-0007's ruling is that there is "no latest designation" (GOZ-0026 and GOZ-0027 own that). Options:
   - **(a)** Accept it as a per-child "last declared in" display and add one sentence saying so to `contracts/child-identity.md`. No code change.
   - **(b)** Rename it before merge, for example to `lastDeclaredIn`. This changes the API, web and tests, as a bounded GOZ-0008 iteration.

   The analyst does not choose. The downstream guard note in 0012, 0026 and 0027 holds under either option.
2. **GOZ-0008-IF-14.** Approve additive rows in `docs/decisions/decision-register.md` under "Maintainer rulings from spec delivery". Proposed:
   - *GOZ-0008 Q1 (child IDs):* `gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>`, with no aliases, recorded in the namespace contract (GOZ-0005-IF-12). Child UUIDs are local and unattested. Specs: GOZ-0008; 0012, 0021, 0024, 0028, 0030. Source: maintainer 2026-11-19.
   - *GOZ-0008 Q2 (publish rule catalogue v1):* publish can refuse what validate accepts (`publish.duplicate_entry`, `publish.nul_character`, `422`). The GOZ-0006 catalogue is unchanged. Specs: GOZ-0008, 0009, 0030.
   - *GOZ-0008 A2/A4 (operator surface):* `is_operator()` reads every tenant's revisions and child rows, and `projection-regenerate` is a third owner-login command. This extends the GOZ-0007-IF-14 acceptance, and GOZ-0005-IF-04 stays open to GOZ-0039. Specs: GOZ-0008, 0039.
   - *GOZ-0008 F3/F4:* a pre-GOZ-0008 revision holding U+0000 is skipped at install, and its capability answers `503` to publication permanently. Child-table refusals answer `503`. Specs: GOZ-0008, 0039.
3. **GOZ-0008-IF-15 (optional).** Approve a one-line pointer from `docs/specs/007-immutable-revisions/contracts/revision-api.openapi.yaml` (publish description) and `permissions-delta.md` to `docs/specs/008-child-identities/contracts/publish-delta.md`. The living architecture contract is already current.

4. **GOZ-0008-IF-16 (completed-spec evidence).** Commit `a98b4f6` rewrites the `performance` blocks of `docs/specs/006-typed-manifests/evidence/manifest-evidence.json` and `docs/specs/007-immutable-revisions/evidence/revision-evidence.json`. The cause is that `ManifestPerformanceTests.cs:82` and `RevisionPerformanceTests.cs:57` write to those completed-spec files on every `make test`. For GOZ-0007, the publish median goes from 200 to 350 ms and the worst time from 478 to 1,463 ms, against a 2,000 ms limit. The analyst did not measure whether child assignment or host load (noted in `progress.yaml`) caused this. GOZ-0008 SC-006 passed (worst 392 ms for 1,000 entries). Options:
   - **(A)** Revert the two files to their `main` versions on this branch.
   - **(B)** Accept them as regression evidence and say so in the PR.
   - **(C)** Also schedule a bounded test-harness change so that the performance tests stop writing to completed specs' evidence.

Still open and not reopened here: GOZ-0005-IF-04 (least-privilege operator role, at GOZ-0039 clarification), GOZ-0007 SEC-3, and the optional `log_error_verbosity=terse` hardening from the pass-4 security review (routed to GOZ-0039 as maintainer judgement).
