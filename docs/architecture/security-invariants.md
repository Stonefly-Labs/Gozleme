# Non-negotiable security invariants

1. Every tenant-owned row is protected by application authorization and effective PostgreSQL RLS; public/shared records and private visibility grants require separately verified access paths.
2. Ordinary application database roles must not bypass RLS; tenant context is transaction-scoped and is not itself proof against arbitrary SQL injection. The owner login is the installation authority over installation-claim tables (row security enabled, not forced; GOZ-0005): guard triggers bind every login, the owner included, to their one-way transitions and nothing is ever deleted, but those guards bind code, not whoever holds the owner credential. That credential must therefore be restricted to operators and its use audited (maintainer decision 2026-11-17, GOZ-0005 Q11).
3. Cross-tenant search results, counts, facets, suggestion endpoints, vector search and relationship traversal must not reveal unauthorized existence.
4. Discovery is never permission to resolve, download or execute. Approved-use eligibility fails closed on missing/stale/conflicting material facts.
5. Known revocations, compromised authorities and withdrawn private rights cannot be bypassed through stale caches or purported lockfile permission.
6. Preserve immutable signed publications; an authorized change produces a new revision or independently signed lifecycle event.
7. Mirrors transport signed origin evidence, not independent publishing authority; conflicting claims quarantined with preserved evidence.
8. Secret values never enter manifests, search indexes, screenshots, traces, public artifacts or catalog fields.
9. Remote embedding data egress for private content requires explicitly configured permission. Embeddings are themselves sensitive.
10. Authentication to administrative recovery is not federation cryptographic authority recovery.
11. Playground artifacts may be signed yet malicious: run them only in a supported bounded disposable sandbox or reject execution.
12. Agents cannot push to `main`, bypass branch rules, enable auto-merge or merge PRs; protected-main enforcement must be configured and verified on GitHub.
