# Gözleme architecture baseline — v1.0-draft

**Status:** Confirmed architectural direction, implementation contracts to be verified per spec. **Interview:** 95 confirmed decisions; **consistency review:** CR-01 through CR-14 approved. **Non-goal:** production AI request broker/gateway.

## Product and stack

MIT-licensed, offline/cloud-agnostic, federated multi-organization registry for MCP servers/tools/resources/prompts, A2A agents/skills, packaged skills, models, APIs and AsyncAPI/event capabilities. .NET 10 modular monolith, Next.js/TypeScript frontend, PostgreSQL with optional pgvector, Npgsql/EF Core. Compose for local development, Kubernetes/Helm deployment; no mandatory cloud or external LLM/embedding provider. Artifact storage pluggable local filesystem, S3-compatible, Azure Blob. Local accounts and external OIDC; provider-referenced secrets, no credentials in catalog. Sandboxed testing playground; not a production execution gateway.

## Core invariants

Origin-qualified permanent identities, publisher-authored immutable manifest revisions, typed child IDs and separately versioned normalized projections; original signatures and provenance survive legitimate ownership changes. Canonical shared catalog, org-scoped private overlays and explicit access relationships; application authorization plus PostgreSQL RLS. Hierarchical deny-overrides policy, versioned activated policy sets, trusted typed facts, security epoch invalidation. Imported capabilities discoverable but **not eligible for approved-use resolution** without local approval and fresh authoritative checks. Dependency completeness is separate from successful resolution of declared dependencies; deterministic bounded dependency solving and portable lockfiles, always revalidate current permission. Federation origin integrity, freshness, scoped private grants, revocation anti-rollback and verified sync are distinct mechanisms. No unsupported v1 succession/recovery behavior may be silently accepted.

## Search

PostgreSQL full-text search, trigram, JSONB metadata, taxonomy and relationship graph search. Opt-in pgvector-based hybrid semantic discovery is MVP-required; operator supplies local/offline or external embedding source. Asynchronous index with provider/model identity and dimensions, new index generation for incompatible changes, access-filtered results, explicit authorization for private metadata sent to remote service. Lexical search works with embeddings off.

## Supported MVP vs future protocol

MVP: authenticated publisher identity/signatures, revision integrity, source trust enrollment, verifiable snapshot and incremental sync with cursor continuity, revocation/security freshness, private grant withdrawal and quarantine, policy and approved-use decisions, sandbox execution, hybrid optional semantic search. Complex cross-registry authority transfer and disaster recovery ceremonies, global transparency witnesses/gossip, rich cross-provider engines and advanced policy conveniences are architecturally specified but post-MVP unless a security review establishes them as a prerequisite for an advertised claim. The v1 security profile explicitly rejects unsupported delegation claims rather than trusting them.

## Human and agent delivery boundary

Every spec produces cumulative runnable demo + evidence, automated test and screenshot where feasible. Work on `chore/*`, `feature/*`, or `bugfix/*`, PR into protected `main`; agents never merge or enable auto-merge. Post-convergence impact review updates future unimplemented prompts within approved scope and syncs the authoritative registry. Changes to confirmed architecture, security invariants or MVP scope need human approval.
