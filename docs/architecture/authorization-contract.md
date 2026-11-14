# Approved-use and visibility contract

**Boundary 1: Discoverability.** Apply canonical-public or recipient-private visibility plus organization-scoped overlays using application authorization and database RLS. Stale records may remain visible only when present access rights permit it, labeled accurately.

**Boundary 2: Approved-use eligibility.** Single authority-aware evaluation contract over principal, org, action, exact capability/child revision, projection generation, policies, approval evidence, sharing grants, dependency and lockfile state, trust/key/revocation freshness. Evaluate bounded typed provenance-aware fact snapshot; missing/conflicting/stale critical facts deny. Cached permits are scoped to principal/action/tenant/resource/policy/fact versions and validate authoritative PostgreSQL security epochs. No TTL-only authorization.

**Boundary 3: Artifact and playground.** Downloads and sandbox tests independently authorize operation and grant scope, never infer authorization from discovery result or preserved lockfile.

**Implementation caution:** RLS on org overlays alone is insufficient; shared canonical tables need explicit public/private grant-based visibility. DB session variables are untrusted if an attacker can run arbitrary SQL with application privileges. Narrow privileged service roles and audit exceptional access.
