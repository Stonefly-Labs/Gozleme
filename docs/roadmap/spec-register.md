# Gözleme — Specification Register

**Generated from `.gozleme/spec-registry.yaml`; do not edit directly.**

**Specs:** 42 · **Complete on main:** 7 · **Blocked:** 0 · **Ready for human merge:** 0

| Spec | Phase | Status | Stage | Dependencies | Next action |
|---|---|---|---|---|---|
| [GOZ-0000](../prompts/spec-0000.md) — Repository bootstrap and executable engineering foundation | foundation | complete | merged | — | Complete — merged to main in 8b25d12382a0741eff17ae334a8948122eeb1860 (PR #4, verified 2026-11-14). Open item: protected-main ruleset (GOZ-0000-B1/IF-12). |
| [GOZ-0001](../prompts/spec-0001.md) — Local identity and session security | foundation | complete | merged | GOZ-0000 | Complete — merged to main in cf006f28a959ab8982ff251005f76fd41275e3d2 (PR #6, verified 2026-11-15). |
| [GOZ-0002](../prompts/spec-0002.md) — Organizations and membership | foundation | complete | merged | GOZ-0001 | Complete — merged to main in 6f658c2e52aca3542d7ad23d0d437b883279195d (PR #8, verified 2026-11-15). |
| [GOZ-0003](../prompts/spec-0003.md) — Tenant RLS and visibility scaffolding | foundation | complete | merged | GOZ-0002 | Complete — merged to main in 0fc67d21f0771a3793248c48a9aff0043038af25 (PR #10, verified 2026-11-16). |
| [GOZ-0004](../prompts/spec-0004.md) — Scoped RBAC permission foundation | foundation | complete | merged | GOZ-0003 | Complete — merged to main in b48a2af2bcadb8073eb076750a17bf435fb83da5 (PR #17, verified 2026-11-16). |
| [GOZ-0005](../prompts/spec-0005.md) — Publisher identities and canonical capability IDs | publishing | complete | merged | GOZ-0004 | Complete — merged to main in 2f63e016d5676734ff88ca6cf9b0917db4528d60 (PR #19, verified 2026-11-17); 4 open impact decisions (IF-02, IF-04, IF-05, IF-08) carry decideBefore dates. |
| [GOZ-0006](../prompts/spec-0006.md) — Typed manifests for capability families | publishing | complete | merged | GOZ-0005 | Complete — merged to main in ffc4112c2f5fc1694f5f6d2a94a07583f985434f (PR #21, verified 2026-11-18); no open GOZ-0006 impact decisions (IF-05 and IF-13 decided 2026-11-18). |
| [GOZ-0007](../prompts/spec-0007.md) — Immutable revisions and signed attestations | publishing | in-progress | tasks | GOZ-0006 | Tasks generated 2026-11-18 (74 tasks in docs/specs/007-immutable-revisions/tasks.md; vendoring stop rule T001-T002); A1-A7 ruled on by the maintainer 2026-11-18 (A1-A6 ratified, A7 demo uses the seeded capability); next /speckit-implement. |
| [GOZ-0008](../prompts/spec-0008.md) — Child capability identities and projections | publishing | not-started | — | GOZ-0007 | Await prerequisites and create allowed working branch |
| [GOZ-0009](../prompts/spec-0009.md) — Filesystem artifact publication | publishing | not-started | — | GOZ-0007 | Await prerequisites and create allowed working branch |
| [GOZ-0010](../prompts/spec-0010.md) — S3-compatible artifact adapter | publishing | not-started | — | GOZ-0009 | Await prerequisites and create allowed working branch |
| [GOZ-0011](../prompts/spec-0011.md) — Azure Blob artifact adapter | publishing | not-started | — | GOZ-0009 | Await prerequisites and create allowed working branch |
| [GOZ-0012](../prompts/spec-0012.md) — Canonical visibility and private discovery | discovery | not-started | — | GOZ-0003, GOZ-0008 | Await prerequisites and create allowed working branch |
| [GOZ-0013](../prompts/spec-0013.md) — PostgreSQL lexical search | discovery | not-started | — | GOZ-0012 | Await prerequisites and create allowed working branch |
| [GOZ-0014](../prompts/spec-0014.md) — Organization-specific catalog overlays | discovery | not-started | — | GOZ-0012 | Await prerequisites and create allowed working branch |
| [GOZ-0015](../prompts/spec-0015.md) — Opt-in local pgvector semantic search | discovery | not-started | — | GOZ-0013, GOZ-0014 | Await prerequisites and create allowed working branch |
| [GOZ-0016](../prompts/spec-0016.md) — Hosted and pluggable embedding providers | discovery | not-started | — | GOZ-0015 | Await prerequisites and create allowed working branch |
| [GOZ-0017](../prompts/spec-0017.md) — Hybrid search and embedding lifecycle | discovery | not-started | — | GOZ-0015, GOZ-0016 | Await prerequisites and create allowed working branch |
| [GOZ-0018](../prompts/spec-0018.md) — Typed declarative policy evaluator | governance | not-started | — | GOZ-0004, GOZ-0012 | Await prerequisites and create allowed working branch |
| [GOZ-0019](../prompts/spec-0019.md) — Trusted facts and approved-use gate | governance | not-started | — | GOZ-0018, GOZ-0007 | Await prerequisites and create allowed working branch |
| [GOZ-0020](../prompts/spec-0020.md) — Policy activation lifecycle and inheritance | governance | not-started | — | GOZ-0018, GOZ-0019 | Await prerequisites and create allowed working branch |
| [GOZ-0021](../prompts/spec-0021.md) — Revision and child approval workflow | governance | not-started | — | GOZ-0019, GOZ-0008 | Await prerequisites and create allowed working branch |
| [GOZ-0022](../prompts/spec-0022.md) — Risk dimensions and reassessment | governance | not-started | — | GOZ-0021, GOZ-0007 | Await prerequisites and create allowed working branch |
| [GOZ-0023](../prompts/spec-0023.md) — Security epochs and authorization receipts | governance | not-started | — | GOZ-0019, GOZ-0020, GOZ-0021 | Await prerequisites and create allowed working branch |
| [GOZ-0024](../prompts/spec-0024.md) — Typed relationship and dependency graph | resolution | not-started | — | GOZ-0008, GOZ-0014 | Await prerequisites and create allowed working branch |
| [GOZ-0025](../prompts/spec-0025.md) — Dependency completeness evidence | resolution | not-started | — | GOZ-0024, GOZ-0022 | Await prerequisites and create allowed working branch |
| [GOZ-0026](../prompts/spec-0026.md) — Compatibility and version constraints | resolution | not-started | — | GOZ-0024, GOZ-0025 | Await prerequisites and create allowed working branch |
| [GOZ-0027](../prompts/spec-0027.md) — Deterministic transitive dependency solving | resolution | not-started | — | GOZ-0026, GOZ-0019 | Await prerequisites and create allowed working branch |
| [GOZ-0028](../prompts/spec-0028.md) — Portable signed-resolution-plan lockfiles | resolution | not-started | — | GOZ-0027, GOZ-0023 | Await prerequisites and create allowed working branch |
| [GOZ-0029](../prompts/spec-0029.md) — Federation source enrollment and pinning | federation | not-started | — | GOZ-0007, GOZ-0023 | Await prerequisites and create allowed working branch |
| [GOZ-0030](../prompts/spec-0030.md) — Verified canonical import and quarantine | federation | not-started | — | GOZ-0029, GOZ-0008 | Await prerequisites and create allowed working branch |
| [GOZ-0031](../prompts/spec-0031.md) — Checkpoint-bound snapshot bootstrap | federation | not-started | — | GOZ-0030, GOZ-0012 | Await prerequisites and create allowed working branch |
| [GOZ-0032](../prompts/spec-0032.md) — Ordered incremental synchronization | federation | not-started | — | GOZ-0031 | Await prerequisites and create allowed working branch |
| [GOZ-0033](../prompts/spec-0033.md) — Revocation freshness and eligibility | federation | not-started | — | GOZ-0032, GOZ-0023 | Await prerequisites and create allowed working branch |
| [GOZ-0034](../prompts/spec-0034.md) — Scoped private sharing grants | federation | not-started | — | GOZ-0033, GOZ-0012 | Await prerequisites and create allowed working branch |
| [GOZ-0035](../prompts/spec-0035.md) — Sharing withdrawal and retention | federation | not-started | — | GOZ-0034 | Await prerequisites and create allowed working branch |
| [GOZ-0036](../prompts/spec-0036.md) — Authority-based conflict reconciliation | federation | not-started | — | GOZ-0032, GOZ-0033 | Await prerequisites and create allowed working branch |
| [GOZ-0037](../prompts/spec-0037.md) — Sandbox isolation and resource boundaries | playground | not-started | — | GOZ-0019, GOZ-0009 | Await prerequisites and create allowed working branch |
| [GOZ-0038](../prompts/spec-0038.md) — Capability testing playground | playground | not-started | — | GOZ-0037, GOZ-0021 | Await prerequisites and create allowed working branch |
| [GOZ-0039](../prompts/spec-0039.md) — Recovery validation and release gates | operations | not-started | — | GOZ-0038, GOZ-0036, GOZ-0017, GOZ-0028 | Await prerequisites and create allowed working branch |
| [GOZ-0040](../prompts/spec-0040.md) — Email delivery, verification and self-service password reset | foundation | not-started | — | GOZ-0001, GOZ-0002 | Approved by maintainer 2026-11-15 (GOZ-0001-IF-06); await prerequisites and create allowed working branch |
| [GOZ-0041](../prompts/spec-0041.md) — External OpenID Connect sign-in and account linking | foundation | not-started | — | GOZ-0001, GOZ-0040 | Approved by maintainer 2026-11-15 with prerequisites GOZ-0001 and GOZ-0040 (GOZ-0001-IF-07); await prerequisites and create allowed working branch |

## Progress and governance

- All branch and gate state is provisional until reviewed; `complete` only after verified **human** PR merge on protected `main`.
- Source documentation: `docs/decisions/decision-register.md`; per-position historical Round 2 verification remains pending.
- Post-convergence Spec Impact Analyst can update future unimplemented prompts in branch within approved architecture; Roadmap Steward syncs this report.
- Every spec includes demo and PR screenshot evidence where visually meaningful.
