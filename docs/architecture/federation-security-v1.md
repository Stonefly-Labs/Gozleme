# Federation Security Profile v1

**Scope:** bounded production-capable MVP, not complete long-term federation cryptographic assurance.

## Implemented-contract requirements

Registry identities are explicitly enrolled and verified/pinned. Publisher-signed immutable revisions are verified against authorized origin signing keys. Reject unknown signature algorithms, unsupported delegation chains and untrusted key replacement. Bootstrap from checkpoint-bound stable snapshot; incrementals use scoped opaque cursors, ordered sequence continuity, idempotent atomic application and authenticated checkpoint verification. Revocation and private grant freshness have independent durable security state; fail-closed eligibility. Untrusted, contradictory or rollback observations are quarantined with evidence. Previously authentic stale public metadata may remain discoverable with appropriate status; permission to use requires current authorization. Private metadata visibility must not outlive permissions. Push only hints to reconcile through pull.

## Explicit deferrals and limitations

Cross-registry publishing transfer, exceptional identity recovery, and advanced transparency witness/gossip are post-v1 until implemented, reviewed and tested. Reserve versioned provenance fields; **do not** claim full split-view/equivocation detection without independent checkpoint comparison. A correctly signed source is not sufficient for approved-use eligibility. Profile negotiation must fail closed if a peer requests security semantics not supported by this version.

## Linked distinct security state

Keep catalog change cursor, transparency checkpoint, trust-anchor set version, publication authority epoch, signed revocation checkpoint and private grant epoch logically separate with typed references. Never use one timestamp or counter as universal authority.
