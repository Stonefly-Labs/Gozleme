# Gözleme — Spec Kit constitution starting prompt

Create the project's Spec Kit constitution using these rules as binding. Preserve original confirmed decisions and note unresolved historical provenance rather than inventing sources.

1. **Product:** MIT open-source federated multi-org AI capability registry, not a production API/AI gateway.
2. **Technology:** .NET 10 modular-monolith-first API, Next.js TypeScript frontend, PostgreSQL, EF Core/Npgsql, optional pgvector and operator-supplied local/remote embeddings; pluggable filesystem/S3-compatible/Azure Blob storage; Compose/Helm, no required managed cloud.
3. **Identity and provenance:** Permanent origin-qualified canonical IDs, immutable verified revisions, independent child/projection generations, safe authority and revocation tracking. Do not accept unsupported delegated chains in v1.
4. **Security:** App RBAC/ABAC plus PostgreSQL RLS, canonical public/private grant visibility, trusted facts, fail-closed approved-use gate, security epochs, secrets references, tenant-safe search and external embedding egress.
5. **Design:** Portable, versioned contracts. Minimal bounded vertical slices. Spec Kit's native templates and lifecycle are authoritative; no competing custom `spec-template.md`.
6. **Demo-first:** Every spec yields an integrated locally runnable stack and autonomous seeded demo where possible; actual sanitized screenshots/CI evidence in PR. Existing demos must keep working.
7. **Branching:** All work on `chore/*`, `feature/*`, `bugfix/*`; GitHub `main` protected, PR-only. Agents NEVER merge, enable auto-merge or bypass rules. Final merge by authorized human only.
8. **Agent workflow:** `drive-speckit` handles current stages with bounded repairs, convergence, required **post-convergence Spec Impact Analyst**, future living prompt evolution within authority, Roadmap Steward registry synchronization and PR preparation. Human approval for architectural, security or material product scope changes.
9. **Progress:** `.gozleme/spec-registry.yaml` is authoritative summary, generated `docs/roadmap/spec-register.md` is human view; each active spec has detailed YAML progress and evidence.
10. **Review:** Confirmed architecture decisions/CR-01 through CR-14 are binding. Trace requirements to spec or post-MVP roadmap. No unsupported functionality or fabricated evidence.
