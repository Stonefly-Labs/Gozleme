# GOZ-0007 post-convergence spec impact analysis

**Source spec:** GOZ-0007, Immutable revisions and signed attestations
**Implementation revision:** `78f4cfc` on `feature/goz-0007-immutable-revisions` (the uncommitted working-tree changes are demo evidence only). The analyst did not re-run tests or the demo, because drive-speckit has queued a cumulative demo against the local stack. `make validate` passed after the prompt edits.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-18
**Structured findings:** `docs/specs/007-immutable-revisions/reviews/impact-findings.json` (15 findings, shape of `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **Diff:** `git diff main...HEAD` (132 files). One migration, `20261121000000_ManifestRevisions`. It adds five tables, `gozleme.is_operator()`, insert guards, immutability and truncate guards for every login, a deferred attestation constraint trigger and forced-RLS policies.
- **Code:** `src/Gozleme.Api/Revisions/**`, including the signing vault, signer, verifier, statement, canonical JSON, endpoints, telemetry, limiter and the succession command. Also the `OrganizationPermissions.cs` diff, `SecurityAudit.cs` 1501–1504, `compose.yaml`, the Helm `values.yaml` signing section, `web/src/telemetry/ui.ts` and `web/src/components/Revision*`.
- **Tests (pins):** `BootstrapBoundaryTests`, `MigrationAndRoleTests` (8), `OrganizationPermissionContractTests` (27), `LATEST_MIGRATION` `/_ManifestRevisions$/` and the Playwright project `goz-0007`.
- **Decisions:** the `progress.yaml` decisions (Q1–Q5, the clarify rulings, P1, P2), agent choices A1–A10, the disclosures and security review SEC-1..5. Also the spec's out-of-scope list and the contracts.
- **Downstream prompts:** GOZ-0008 to GOZ-0039, which depend on GOZ-0007 directly (0008, 0009, 0019, 0022, 0029) or transitively. GOZ-0040 and GOZ-0041 do not depend on it, but they merge onto a `main` that runs its pins.

No downstream prompt assumes publisher-held signing, a delegation chain, mutable revisions, a "latest" revision, signed metadata fields, other permission names or deferred OTel. What was missing was the implemented facts and the obligations they create. The findings below add them.

## Findings

| ID | Sev | Finding | Affected | Disposition |
|---|---|---|---|---|
| GOZ-0007-IF-01 | high | Revision contract v1: routes, check order, immutable for every login, any-order versions unique by precedence, no "latest", closed statement v1 (RFC 8785 + raw Ed25519), digests, verification computed on read, no update/delete or verify endpoint | 0008–0039 | applied |
| GOZ-0007-IF-02 | high | Pins: migration, 5 tables, `is_operator()`, 8 migrations, 27 endpoints / 22 actions / 6 resources, `manifest.validate` moved to `manifest`, boundary test allows 4 tables, `goz-0007` project, demo revisions are permanent | 0008–0041 | applied |
| GOZ-0007-IF-03 | medium | Spans, counters, UI flow, audit 1501–1504 (range held to 1510; next module 1601), publication budget of 10 per minute in PostgreSQL, succession span | 0008–0041 | applied |
| GOZ-0007-IF-04 | high | `publishedBy`, rule and schema versions, family and title/summary are recorded but not attested (SEC-1) | 0008–0039 (specific: 0013, 0019, 0021, 0030) | applied |
| GOZ-0007-IF-05 | high | Registry-held per-publisher keys under a KEK. The attestation proves origin acceptance, not a publisher-held key. Fixed proof wording | 0008–0039 (specific: 0019, 0029, 0030, 0039) | applied |
| GOZ-0007-IF-06 | high | Succession is operator-only and add-only. Retired keys still verify, and the database refuses new attestations by them. No rotation API, revocation or peer trust | 0029, 0030, 0033, 0039 (+ all) | applied |
| GOZ-0007-IF-07 | high | Insert guards bind superusers and re-time rows with `now()`. A restore with triggers enabled would break every statement (`statement_mismatch`) and refuse successor keys. Imports cannot use the local revision tables | 0030, 0031, 0032, 0036, 0039 | applied (routed to 0039 clarification) |
| GOZ-0007-IF-08 | medium | A revision's portable identity is origin + capability + version. The UUID is local and not attested. There is no "latest" | 0008, 0012, 0013, 0021, 0022, 0026–0028, 0030–0032, 0036 | applied (routed to clarification in 0012, 0013, 0022) |
| GOZ-0007-IF-09 | medium | The projection digest is now defined (SHA-256 over the RFC 8785 projection bytes). This resolves the GOZ-0007 part of GOZ-0006-IF-08 | 0008, 0022, 0028, 0036 | applied (supersedes the 0028 bullet) |
| GOZ-0007-IF-10 | medium | Revisions, downloads and keys are organization-private member reads. No public or peer route exists | 0012, 0029, 0030 | applied |
| GOZ-0007-IF-11 | medium | Suspension blocks publishing. Revisions stay readable. There is no lifecycle state and no delete for any login | 0012, 0033, 0035 (+ all) | applied |
| GOZ-0007-IF-12 | low | GOZ-0005-IF-08 can now be decided with known semantics | 0039 | applied (decision stays open) |
| GOZ-0007-IF-13 | medium | Which key signs lockfiles and grants is a custody decision. Publisher revision keys sign `gozleme.revision-attestation` only | 0009, 0028, 0034 | applied (routed to maintainer decision at clarify) |
| GOZ-0007-IF-14 | high | **The GOZ-0005-IF-04 trigger has fired.** `signing-key-succession` is a second owner-login operator command, and `is_operator()` hard-codes `gozleme_migrator`. The GOZ-0007 workspace never references IF-04 | 0007, 0039 | **escalated** |
| GOZ-0007-IF-15 | medium | The architecture baseline (MVP "authenticated publisher identity/signatures") and the decision register do not record Q1 custody and its stated limit, Q5, P1/P2 or SEC-1 | 0029, 0030, 0034 | **escalated** |

No finding is critical. None changes the DAG, an outcome, a prerequisite or an invariant. No implementation defect needs a corrective iteration. IF-07 is a design consequence for GOZ-0039 and GOZ-0030. It is not a GOZ-0007 defect: FR-004 requires the guards to bind every login. It was inferred from the migration code and not reproduced.

## Prompt edits applied (within authority)

- **GOZ-0008 to GOZ-0039:** a new section, `### Immutable revision conventions (GOZ-0007-IF-01, IF-02, IF-03, IF-04, IF-05, IF-06, IF-08, IF-11, 2026-11-18)`. It supersedes the GOZ-0006 permission counts, migration pins, boundary-test wording and the "1501–1510 still free" audit note.
- **GOZ-0040 and GOZ-0041:** a short conventions section (IF-02, IF-03, IF-14).
- **Spec-specific `### Implementation alignment (GOZ-0007-…)` sections:** 0008, 0009, 0012, 0013, 0019, 0021, 0022, 0026, 0027, 0028, 0029, 0030, 0031, 0032, 0033, 0034, 0035, 0036 and 0039.
- **Roadmap Steward:**
  - bump `promptRevision` 8 → 9 for GOZ-0008 to GOZ-0039, and 6 → 7 for GOZ-0040 and GOZ-0041;
  - list IF-01..IF-15 on GOZ-0007;
  - record IF-14 and IF-15 as resolved by maintainer ruling (below), not as pending decisions;
  - add this review to its evidence.

## Escalated for maintainer decision (not edited by the analyst)

**Resolved 2026-11-18 by maintainer ruling:**
- **IF-14:** accepted for MVP. The second owner-login command stays as it is, and GOZ-0005-IF-04 stays open until GOZ-0039 clarification.
- **IF-15:** amendment approved. It was applied in `docs/decisions/decision-register.md` ("Maintainer rulings from spec delivery": IF-15 custody and limit, SEC-1, IF-14) and in the MVP line of `docs/architecture/architecture-baseline.md`.

Original escalations:

1. **GOZ-0007-IF-14 (GOZ-0005-IF-04):** decide now whether to introduce a least-privilege operator role, which would change `is_operator()` and both operator commands. The alternative is to explicitly accept the second owner-login command and defer the decision again to GOZ-0039 clarification.
2. **GOZ-0007-IF-15:** approve an additive amendment to `docs/decisions/decision-register.md`, plus a note in `docs/architecture/architecture-baseline.md`. It would record that MVP publisher signatures are registry-held per-publisher Ed25519 keys under a KEK, with the stated limit (no proof that the publisher held a key; a KEK holder could forge). It would also record add-only operator succession, the RFC 8785 + raw Ed25519 signed bytes, and the SEC-1 ruling that statement v1 does not attest the recorded metadata.

Still open and not reopened here: SEC-3 (KEK on the Helm migrate Job, accepted per A9; the maintainer may reopen it at the GOZ-0039 release review), and GOZ-0005-IF-02, IF-05 and IF-08 with their existing decide-before dates.
