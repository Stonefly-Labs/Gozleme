# Approved architecture consistency review — CR-01 through CR-14

**Status:** All fourteen resolution approaches approved by the user. These are implementation constraints, not claims of completed work.

| ID | Priority | Required resolution | Earliest owning area |
|---|---|---|---|
| CR-01 | High | Define explicit federation v1 security profile; reject unsupported advanced trust claims | 0029 |
| CR-02 | High | Define canonical public/private visibility and overlay RLS paths | 0003, 0012 |
| CR-03 | High | Separate publication integrity, sharing grants, retention and eligibility | 0012, 0034–0035 |
| CR-04 | High | One central approved-use gate, security epochs and fail-closed facts | 0019, 0023 |
| CR-05 | High | Reserve immutable authority/delegation fields, reject unsupported transfer chains | 0007, 0029 |
| CR-06 | High | Tenant-safe embeddings, opt-in private remote egress, indexing domains | 0015–0017 |
| CR-07 | Medium | Separate historical proofs from erasable data; include vector/cache/backup deletion | 0009, 0035, 0039 |
| CR-08 | Medium | Dependency completeness is separate from graph resolution | 0025–0028 |
| CR-09 | Medium | Canonical policy IR with evaluator conformance suite | 0018–0020 |
| CR-10 | Medium | Signed artifacts still require isolation/quotas/egress sandbox enforcement | 0037–0038 |
| CR-11 | Medium | Keep distinct linked catalog cursor, revocation, authority and log checkpoint state | 0031–0033 |
| CR-12 | Delivery | Runnable vertical slice after every spec, early tenant security | all |
| CR-13 | Delivery | Bound scope, smallest secure usable workflow, post-v1 advanced deferrals | all |
| CR-14 | Delivery | Compose/offline, Helm, S3, Azure and embedding portability acceptance | 0000, 0010–0011, 0039 |

**Tracking:** Each spec prompt references this review. Traceability is additionally mapped in `docs/decisions/decision-traceability.md`. Findings may be marked implemented only with evidence and PR merge verification.
