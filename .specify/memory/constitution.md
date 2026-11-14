# Gözleme Constitution

## Core Principles

### I. Capability Registry, Not a Gateway

Gözleme is an MIT-licensed, open-source, federated, multi-organization AI capability registry
covering MCP servers/tools/resources/prompts, A2A agents/skills, packaged skills, models, APIs
and AsyncAPI/event capabilities.

- Gözleme MUST NOT be built, marketed or specified as a production API gateway, AI request
  broker or production execution gateway.
- The sandboxed testing playground is the only execution surface; it exists for bounded
  evaluation, not production traffic.
- Any requirement that would make Gözleme proxy, broker or meter production AI/API traffic is
  out of scope and requires a human-approved product scope change.

**Rationale:** The confirmed architecture baseline names a production gateway as an explicit
non-goal; keeping scope tight protects the security model and the MVP boundary.

### II. Portable, Self-Hostable Technology Baseline

- Backend: .NET 10 ASP.NET Core, modular monolith first. Services MUST NOT be split out without
  a human-approved architecture change.
- Frontend: Next.js with TypeScript and accessible UI; pnpm.
- Data: PostgreSQL via EF Core/Npgsql. pgvector is OPTIONAL and MUST NOT prevent base stack
  startup when absent or disabled.
- Embeddings: operator-supplied local/offline or remote provider behind a pluggable contract.
  No embedding model or external LLM provider may be required to boot or run lexical search.
- Artifact storage: pluggable local filesystem, S3-compatible and Azure Blob providers.
- Deployment: Docker Compose for local/offline use and Kubernetes/Helm; no managed cloud
  service, hosted identity provider or hosted secret store may be mandatory.
- Identity: local accounts and external OIDC; secrets are provider references only.
- Dependency choices MUST be verified for current security and compatibility at implementation
  time; empty frameworks MUST NOT be pre-created.

**Rationale:** Operators must be able to run the full registry offline and on any cloud
(CR-14); portability is an acceptance criterion, not a later port.

### III. Permanent Identity and Verifiable Provenance

- Registry and capability identities are permanent and origin-qualified; canonical IDs MUST
  NOT be reassigned or reused.
- Publisher-authored manifest revisions are immutable once verified. Any authorized change
  produces a new revision or an independently signed lifecycle event.
- Typed child capability IDs and normalized projection generations are versioned independently
  of the parent revision; object lifecycles MUST NOT be collapsed into one status field.
- Original signatures and provenance MUST survive legitimate ownership changes.
- Authority and revocation state is tracked as distinct, durable, typed security state
  (catalog cursor, transparency checkpoint, trust-anchor set version, publication authority
  epoch, revocation checkpoint, private grant epoch). One timestamp or counter MUST NOT act as
  universal authority (CR-11).
- v1 MUST reserve immutable authority/delegation fields and MUST reject unsupported delegated
  chains, transfer chains, unknown signature algorithms and untrusted key replacement rather
  than trust them (CR-01, CR-05). Peer profile negotiation fails closed.
- Mirrors transport signed origin evidence, not independent publishing authority; conflicting
  claims are quarantined with preserved evidence.

**Rationale:** Federation is only trustworthy if identity is stable and every claim can be
traced to verified origin authority; silently accepting unsupported trust would overstate v1
guarantees.

### IV. Tenant Isolation and Fail-Closed Authorization (NON-NEGOTIABLE)

- Every tenant-owned row is protected by application RBAC/ABAC authorization AND effective
  PostgreSQL row-level security. Ordinary application database roles MUST NOT bypass RLS;
  transaction-scoped tenant context is not treated as proof against arbitrary SQL.
- Shared canonical tables use explicit canonical-public or recipient-private grant-based
  visibility paths; RLS on org overlays alone is insufficient (CR-02).
- Publication integrity, sharing grants, retention and approved-use eligibility are separate
  mechanisms (CR-03).
- Discovery is never permission to resolve, download or execute. One central, authority-aware
  approved-use gate evaluates a bounded, typed, provenance-aware trusted-fact snapshot;
  missing, stale or conflicting critical facts deny (CR-04).
- Cached permits are scoped to principal/action/tenant/resource/policy/fact versions and
  validated against authoritative PostgreSQL security epochs. TTL-only authorization is
  prohibited.
- Known revocations, compromised authorities and withdrawn private rights MUST NOT be
  bypassable through stale caches or preserved lockfiles. Downloads and sandbox tests
  authorize independently.
- Search results, counts, facets, suggestions, vector search and relationship traversal MUST
  NOT reveal unauthorized existence. Public and org-private indexing domains stay separated.
- Private content MUST NOT be sent to a remote embedding provider without explicitly
  configured tenant-authorized egress; embeddings are themselves sensitive (CR-06).
- Secret values MUST NOT enter manifests, catalog fields, search indexes, screenshots, traces
  or public artifacts; only secret references are stored.
- Signed playground artifacts may still be malicious; they run only in a supported bounded,
  disposable sandbox with isolation, quotas and egress controls, or execution is rejected
  (CR-10).

**Rationale:** A multi-org registry fails catastrophically on a single tenant leak; every
security boundary must deny by default and be verifiable by negative tests.

### V. Portable Contracts and Bounded Vertical Slices

- Public APIs, manifests, policy IR, lockfiles, federation messages and storage/embedding
  provider interfaces are portable, explicitly versioned contracts. Breaking changes require a
  new contract version.
- Policy uses a canonical typed IR with a conformance suite for pluggable evaluators (CR-09).
- Dependency completeness is a separate concern from graph resolution; resolution is
  deterministic and bounded, and use-time permission is always revalidated (CR-08).
- Each spec delivers the smallest secure, usable vertical slice; advanced capabilities are
  deferred to the post-MVP roadmap rather than partially implemented (CR-13).
- Spec Kit's installed native templates and lifecycle are authoritative. A competing custom
  `spec-template.md` or invented stage MUST NOT be introduced, and installed Spec Kit MUST NOT
  be reinstalled.

**Rationale:** Versioned contracts let federated peers and pluggable providers interoperate
safely; small slices keep each spec reviewable and demonstrable.

### VI. Demo-First, Evidence-Backed Delivery (NON-NEGOTIABLE)

- Every completed spec leaves the cumulative stack runnable locally with Docker Compose,
  documented commands, deterministic seeded synthetic data and no cloud requirement (CR-12).
- The demo is driven autonomously where feasible: start stack, wait for health, migrate, seed,
  drive the real UI via Playwright, run UI/API assertions including negative tenancy/security
  tests, and record run metadata (commit, branch, environment, timestamps, commands, result,
  evidence paths).
- PRs for visual work MUST embed actual, sanitized screenshots; headless specs attach recorded
  API/log/test evidence instead. Stock, recycled or fabricated imagery is prohibited.
- Screenshots MUST NOT contain credentials, keys, tokens or real customer/private data, and
  supplement assertions rather than replace them.
- If a browser or the stack is unavailable, the demo is marked blocked with reproducible manual
  steps; success MUST NOT be falsely recorded.
- Existing demos MUST keep working; a regression in a prior demo blocks the PR.

**Rationale:** Runnable, evidenced increments are how a human reviewer verifies that security
and product claims are real.

### VII. Protected Main and Human-Only Merge (NON-NEGOTIABLE)

- All work occurs on `chore/*`, `feature/*` or `bugfix/*` branches and reaches `main` only
  through a pull request.
- `main` is protected by actual GitHub rulesets or branch protection (required PRs, human
  review, passing checks, resolved threads, no force push/deletion, restricted bypass). Repo
  files cannot prove hosted settings; a human owner verifies enforcement.
- Agents MUST NEVER push to `main`, merge any PR, enable auto-merge, bypass protections,
  dismiss reviews, override checks or claim human approval.
- Only an authorized human merges. A spec is marked complete only after the merged commit on
  `main` is independently verified.

**Rationale:** Human merge authority is the final control over agent-produced changes.

### VIII. Converge, Analyze Impact, Then Hand Off

- `drive-speckit` orchestrates each spec: verify prerequisites → specify → plan → tasks →
  implement → tests → autonomous demo and screenshots → converge → Spec Impact Analyst →
  downstream prompt propagation → Roadmap Steward sync → PR preparation → stop for human merge.
- Repairs are bounded to three meaningful attempts per distinct failure, then a blocker is
  recorded with evidence, owner and exact resume action. Convergence MUST NOT recurse
  indefinitely, and failing security checks MUST NOT be weakened.
- The post-convergence Spec Impact Analyst is REQUIRED for every spec, even when no impact is
  expected, and must inspect actual code, contracts and evidence.
- Future unimplemented prompts are living documents that agents may edit within the approved
  baseline, recording finding IDs and rationale, in a PR.
- Changes to confirmed architecture, security invariants, committed MVP scope or approved
  completed specs require a change proposal and explicit human approval. Merged specs are
  historical; amend additively via corrective PR.

**Rationale:** Implementation reality must flow forward into later specs without letting
agents silently redefine governed decisions.

### IX. Authoritative Progress Tracking

- `.gozleme/spec-registry.yaml` is the authoritative machine-readable summary of spec status,
  gates, blockers, impact findings and evidence.
- `docs/roadmap/spec-register.md` is a generated human view and MUST NOT be hand-edited out of
  sync; CI checks that it matches the registry.
- Each active spec maintains detailed YAML progress and evidence conforming to
  `.gozleme/schemas/`.
- Stage is determined from durable progress records, never from assumption.

**Rationale:** A single source of truth keeps human and agent views of the roadmap
consistent and auditable.

### X. Confirmed Decisions Bind; Evidence Is Never Fabricated

- The confirmed architecture baseline, security invariants, the 95 confirmed interview
  decisions and approved consistency resolutions CR-01 through CR-14 are binding constraints.
- Every requirement traces to a spec or to the post-MVP roadmap; unsupported functionality
  MUST NOT be added or advertised.
- Historical provenance gaps are recorded, not filled: missing original decision text or IDs
  MUST NOT be invented, and synthetic ADR identifiers MUST NOT be created.
- Test results, demo runs, screenshots, review outcomes and merge status MUST be real and
  verifiable. A finding is marked implemented only with evidence and verified merge.

**Rationale:** Governance is only meaningful if its sources and evidence are honest.

## Security and Architecture Constraints

The following documents define binding detail for Principles III–V and are authoritative
alongside this constitution:

- `docs/architecture/architecture-baseline.md` — product, stack and core invariants.
- `docs/architecture/security-invariants.md` — twelve non-negotiable security invariants.
- `docs/architecture/authorization-contract.md` — discoverability, approved-use and
  artifact/playground boundaries.
- `docs/architecture/federation-security-v1.md` — federation v1 profile and explicit
  deferrals.
- `docs/architecture/semantic-search.md` — opt-in hybrid semantic search contract.
- `docs/decisions/consistency-review.md` — CR-01 through CR-14.
- `docs/roadmap/mvp-vs-post-mvp.md` — release boundary.

MVP scope includes multi-org local/OIDC identity, public/private RLS and overlays, typed
immutable publishing, the three artifact providers, lexical plus optional pgvector hybrid
search, approvals and typed policies, evidence/facts/epochs, signed origin import, verifiable
snapshot and incremental sync, scoped private grants and withdrawal, revocation/freshness and
quarantine, deterministic bounded resolution and lockfiles, sandbox playground, audit and
operational validation.

Post-MVP unless a security-profile review requires otherwise: cross-registry publishing
authority delegation, orphan cryptographic recovery ceremonies, transparency witnesses/gossip,
federated policy packs, rich policy simulation, elaborate delegated admin recovery and
external vector engines beyond pgvector. Deferral never permits claiming the deferred
guarantee; unsupported trust chains are rejected.

Historical data handling separates erasable data from historical proofs; lawful deletion
covers vectors, caches and backups (CR-07). Administrative recovery authentication is not
federation cryptographic authority recovery.

## Development Workflow and Quality Gates

- Read `CLAUDE.md`, `.gozleme/manifest.yaml`, the architecture baseline, security invariants
  and the current registry before acting; follow `docs/engineering/agent-workflow.md`.
- Each spec's plan MUST pass a Constitution Check against Principles I–X before design and
  again after design; violations require documented justification or a human-approved change.
- CI gates (`docs/engineering/ci-quality-gates.md`): YAML/JSON Schema validation, prompt path
  existence and count, unique IDs, dependency acyclicity, generated register in sync, branch
  naming, backend/frontend/container tests and runtime health, demo evidence status,
  architecture/security/impact review references, screenshot links where relevant, secret
  scanning and dependency checks. Tests MUST NOT be weakened to pass.
- Integration tests use Testcontainers against real PostgreSQL; tenant isolation features
  require negative cross-tenant tests.
- Every PR lists scope, spec IDs, contracts/migrations, tests, autonomous demo execution,
  embedded sanitized screenshots, downstream impact report, prompt mutations, human decisions
  and merge blockers (`docs/engineering/branching-and-pr-policy.md`).

## Governance

- This constitution supersedes other project practices. Where a referenced document conflicts
  with it, this constitution prevails until the conflict is resolved by amendment.
- Amendments are made on an allowed branch via PR, with a Sync Impact Report, and take effect
  only on human merge. Amendments touching architecture, security invariants, MVP scope or
  Principle VII require explicit human approval recorded in the PR.
- Versioning follows semantic versioning: MAJOR for removing or redefining a principle or
  governance rule; MINOR for a new principle/section or materially expanded guidance; PATCH for
  clarifications and wording.
- Every PR review verifies compliance with this constitution; added complexity MUST be
  justified in the plan's Complexity Tracking.
- Provenance: the 62 Round 2 decision positions (R2-01–R2-62) lack verified original text and
  IDs; `docs/decisions/decision-register.md` records them as pending. Their thematic content is
  binding through the architecture baseline, but no row may be marked source-verified without
  an original source reference.
- Runtime agent guidance: `AGENTS.md` (tool-neutral entry point), `CLAUDE.md`,
  `docs/engineering/agent-workflow.md`, and `.claude/skills/` / `.claude/agents/`.

**Version**: 1.0.0 | **Ratified**: 2026-11-14 | **Last Amended**: 2026-11-14
