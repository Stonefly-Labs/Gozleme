# Implementation Plan: Child Capability Identities and Projections

**Branch**: `feature/goz-0008-child-identities` | **Date**: 2026-11-19 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/008-child-identities/spec.md`

## Summary

Every projection entry of every published revision becomes a **child identity**, with the permanent ID
`gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>` (maintainer Q1). Members browse a capability's children,
each child's history across revisions, and each revision's children and **projection generations**. Child IDs and
generation IDs are different identifiers of different records, and neither is derived from the other.

- **Assignment** (Q2; research R1–R4):
  - A pure `ChildAssigner` matches entries by exact (`kind`, `nativeName`), in publication sequence, inside the GOZ-0007
    publication transaction and under its per-capability lock.
  - A rename is a new child. A dropped entry's child is kept and shown as absent.
  - Publication refuses duplicate keys with `422 publish.duplicate_entry` (a separate publish rule catalogue). Validate
    is unchanged, which is the documented contract delta.
  - The database checks every assignment:
    - a partial unique index gives one matchable child per key;
    - a statement-level trigger checks each child record against its projection entry, parsing the projection once;
    - deferred constraint triggers make a revision without generation 1 or a complete set of child records impossible
      to commit.
- **Generations** (Q3, clarify Q2; R5, R7):
  - Generation 1 references the stored, attested projection (no copy).
  - The operator command `projection-regenerate --revision <uuid>` (owner login, no API route) adds an unattested
    generation N + 1 that keeps every child. It refuses if the entry keys change, and records a byte-identical
    generation with "digest equals generation N: yes".
- **Existing revisions** (FR-016, clarify Q1; R6): the last step of `migrate` assigns children and generation 1 to
  older revisions, idempotently and in sequence order. A duplicate key there is recorded as a duplicate-marked child
  rather than refused.
- **Storage**: one migration adds three forced-RLS tenant tables. They are write-once for every login, writable by
  `publisher` holders with a held claim or by the operator, and readable by members. The migration also adds an
  operator read branch to `manifest_revisions_select` (a security-review item).
- **API and product**:
  - five read endpoints on the existing `manifest.list` and `manifest.read` actions (27 → 32 scoped endpoints, no new
    action);
  - **Children** and **Child** pages, plus **Children** and **Projection generations** sections on the revision page;
  - the fixed notice "declared by the publisher, not verified behaviour".
- **Demo**: Playwright project `goz-0008` covers the five stories, the command, database refusals, isolation, Jaeger
  spans and a no-secret scan, plus regression of all earlier suites ([quickstart.md](./quickstart.md)).

## Technical Context

**Language/Version**: C# on .NET 10; TypeScript (Next.js web tier, Node 24 e2e); SQL (PostgreSQL 17) in one
migration; Python 3 governance scripts.

**Primary Dependencies**:
- existing: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql, OpenTelemetry .NET, the GOZ-0006 adapters and
  `CanonicalJson`/`RevisionPublisher`/`RevisionStore` of GOZ-0007, Next.js, Vitest, Playwright, xUnit v3 +
  Testcontainers;
- **no new package** (backend, web or e2e).

**Storage**: PostgreSQL. The new migration `20261122000000_ChildIdentities` adds:
- the tables `capability_children`, `revision_children` and `projection_generations` (all `TenantOwned`, forced RLS);
- insert guards, a statement-level entry check, deferred constraint triggers (`capability_children_recorded`,
  `manifest_revisions_children`) and immutability triggers;
- the `is_operator()` branch on `manifest_revisions_select`.

There is no data migration; the install step runs in `migrate` after the migrations. See
[data-model.md](./data-model.md).

**Testing**:
- **New unit suites**: `ChildAssignerTests`, `ChildEntriesTests`, `ProjectionRegeneratorTests`.
- **New integration suites**: `ChildPublishTests`, `ChildIntegrityTests`, `ChildImmutabilityTests`,
  `ChildAuthorizationTests`, `ChildIsolationTests`, `ChildInstallTests`, `ProjectionRegenerateTests`,
  `ChildTelemetryTests`, `RevisionChildrenPerformanceTests`.
- **Updated**:
  - `OrganizationPermissionContractTests` (32 scoped endpoints), `EndpointAuthorizationTests`;
  - the migration pins, `BootstrapBoundaryTests` (table list and the `(manifest|revision|projection)` set);
  - the RLS catalog and isolation matrix;
  - GOZ-0007 suites that insert revisions by SQL (the new deferred check);
  - `ManifestNoStorageTests` (validate still stores nothing).
- **Web (Vitest)**: child list, child detail with Present/Absent history, revision children, generation list with
  attested and not-attested labels, the fixed notice and the absence of badges.
- **Playwright**: project `goz-0008` (`e2e/tests/children-demo.spec.ts`).

**Target Platform**: Linux containers (Compose; Kubernetes via Helm). No Compose, Helm or environment change; the
install step runs in the existing `migrate` service and Job.

**Project Type**: web application (API + Next.js BFF) in the existing monorepo.

**Performance Goals**:
- publishing a 1,000-entry revision within the GOZ-0007 budget (2 s) plus 1 s (SC-006): one statement-level parse per
  projection and one key lookup query;
- list pages are bounded (50 by default, at most 100).

**Constraints**:
- **Never recorded**: names, titles, native names, digests and documents never appear in URLs, spans, metrics or logs
  (FR-019, SC-007).
- **Publication unchanged except**: the duplicate refusal and the rollout-gap `503` (R6); the `201` body, statement,
  signature and verification are unchanged (FR-021).
- **Validation unchanged**: GOZ-0006 validation, envelope v1, projection v1, the adapters and the GOZ-0006 rule
  catalogue are unchanged.
- **Boundary pins**: `POST /api/v1/publish`, `GET /api/v1/capabilities`, `/catalog` and `/search` still answer `404`.

**Scale/Scope**:
- **API**: 5 endpoints, no new action, a `Children/` module (assigner, duplicate rule, store, endpoints, contracts,
  install step, regenerate command, telemetry), 1 migration and 3 audit events (1601–1603).
- **Web**: 2 pages, 2 sections on the revision page, about 4 route handlers (GET only), about 4 components.
- **e2e**: 1 Playwright suite plus an operator helper entry.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | Children are recorded from stored manifest projections. Nothing is fetched, connected to or executed; live discovery of a server's tools is out of scope | PASS |
| II. Portable baseline | No new package, service, configuration or secret. Compose and Helm are unchanged | PASS |
| III. Permanent identity and provenance | Child IDs are permanent, origin-qualified and anchored on the canonical capability ID, in the form the maintainer decided (Q1). Typed child IDs and projection generations are versioned independently of the revision, as Principle III requires. Generation 1 is the attested projection, and later generations are additive and labelled not attested. Revisions and attestations are unchanged | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | Three forced-RLS tenant tables with member reads and `publisher`-plus-held-claim inserts mirrored in the database (FR-018). One decision point with an identical empty `404`. Isolation matrix for both logins. The `is_operator()` read branch on `manifest_revisions_select` is the one policy widening; `gozleme_app` cannot satisfy it (R6; security-review item) | PASS |
| V. Contracts and bounded slices | The child identity contract, generation contract, publish delta, OpenAPI and permissions delta are versioned and documented. Child lifecycle, approvals, visibility, search and portable child references are deferred (GOZ-0012/0013/0021/0024/0028/0030) | PASS |
| VI. Demo-first | Cumulative `goz-0008` browser demo with screenshots, the operator command, database refusals, isolation and Jaeger evidence, with earlier suites kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Through `drive-speckit` | PASS |
| IX/X. Progress and decisions | Q1–Q3 and the two clarify answers are recorded in `progress.yaml` and the spec. Agent choices A1–A8 (research) are disclosed for veto, and none conflicts with the spec | PASS |

**Post-design re-check (after Phase 1): PASS.**
- **New surface**:
  - 3 tenant-owned tables;
  - trigger functions only (no callable function, no definer; `DefinerHelpers` unchanged);
  - one widened policy (`manifest_revisions_select` for the operator);
  - audit range 1601–1603.
  - No new role or grant beyond `SELECT, INSERT` on the new tables.
- **Security-review items for convergence**:
  - the operator read branch on `manifest_revisions_select`;
  - the statement-level entry check and the partial unique index as the real guarantee of matching (a bypass would
    allow a wrong child reuse);
  - the duplicate-child insert being operator-only;
  - the rollout gap (R6);
  - the no-name scan coverage of spans, logs and the command output;
  - `projection-regenerate` not re-running validation rules.

## Project Structure

### Documentation (this feature)

```text
docs/specs/008-child-identities/
├── spec.md
├── plan.md                                   # this file
├── research.md                               # R1–R14, agent choices A1–A8
├── data-model.md                             # 3 tenant tables, triggers, one changed policy
├── quickstart.md
├── contracts/
│   ├── child-identity.md                     # ID form, match key, duplicates (namespace contract change)
│   ├── projection-generations.md             # generations, projection-regenerate, install step
│   ├── publish-delta.md                      # publish.duplicate_entry, validate/publish difference
│   ├── children-api.openapi.yaml
│   └── permissions-delta.md                  # applied to docs/architecture/contracts/organization-permissions.md
├── checklists/requirements.md
├── progress.yaml
├── evidence/                                 # demo-run.json, children-evidence.json, screenshots/ (created by the demo)
├── reviews/                                  # impact analysis (post-convergence)
└── tasks.md                                  # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── Children/                                     # new module
│   ├── ChildEndpoints.cs                         # 5 GET endpoints; order R9 (reuses the RevisionEndpoints resolution)
│   ├── ChildContracts.cs                         # ChildSummary, ChildDetail, RevisionChild, Generation, lists
│   ├── ChildAssigner.cs                          # pure matching, publish | install mode (R2)
│   ├── ChildEntries.cs                           # duplicate groups → publish.duplicate_entry problems (R3)
│   ├── PublishRules.cs                           # publish rule catalogue v1
│   ├── ChildStore.cs                             # reads and inserts under TenantTransaction
│   ├── ChildInstaller.cs                         # migrate install step, operator connection (R6)
│   ├── ProjectionRegenerator.cs                  # pure re-projection core (R7)
│   ├── ProjectionOperatorCommands.cs             # projection-regenerate (R7)
│   ├── ChildTelemetry.cs                         # spans + counters (R11)
│   └── ChildServiceCollectionExtensions.cs
├── Revisions/RevisionPublisher.cs               # duplicate check, rollout-gap check, assign + generation 1 in the transaction
├── Revisions/RevisionEndpoints.cs               # 422 duplicate refusal in the publish order
├── Publishers/RegistryOrigin.cs                 # CanonicalId.Child
├── Identity/SecurityAudit.cs                    # events 1601–1603
├── Persistence/
│   ├── Migrations/20261122000000_ChildIdentities.cs
│   ├── MigrationRunner.cs                       # install step after origin verification
│   ├── GozlemeDbContext.cs                      # entities + snapshot
│   └── TableClassification.cs                   # 3 TenantOwned
├── Program.cs                                   # projection-regenerate dispatch
└── GozlemeApp.cs                                # module registration, MapChildEndpoints
docs/specs/005-publisher-identities/contracts/namespace.md   # additive "Child IDs (GOZ-0008)" section
docs/architecture/contracts/organization-permissions.md     # permissions delta applied
web/src/
├── app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/children/page.tsx
├── app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/children/[childId]/page.tsx
├── app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/[revisionId]/page.tsx   # + sections
├── app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/children/…            # GET handlers
├── app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/[revisionId]/projections/[generationId]/route.ts
├── components/{ChildList,ChildDetail,RevisionChildren,ProjectionGenerations}.tsx   # + tests
├── components/{CapabilityList,RevisionList}.tsx # Children links
└── lib/children.ts (+ children-server.ts)       # types, notice text, labels
tests/Gozleme.Api.Tests/Children/{ChildAssignerTests,ChildEntriesTests,ProjectionRegeneratorTests}.cs
tests/Gozleme.Api.IntegrationTests/
├── Child{Publish,Integrity,Immutability,Authorization,Isolation,Install,Telemetry}Tests.cs
├── ProjectionRegenerateTests.cs  RevisionChildrenPerformanceTests.cs
└── OrganizationPermissionContractTests.cs EndpointAuthorizationTests.cs BootstrapBoundaryTests.cs (+ migration/RLS pins)
e2e/
├── playwright.config.ts                         # + goz-0008 (depends on goz-0007)
└── tests/{children-demo.spec.ts, operator.ts (+ regenerate), evidence.ts (+ specDirs)}
```

**Structure Decision**: a new `Children/` module beside `Revisions/`.
- **Reuse**: it reuses `RevisionPublisher`'s transaction and lock, `RevisionStore`/`RevisionEndpoints` target
  resolution, `CanonicalJson`, the adapters, `RegistryOriginReader`, `TenantTransaction`, `OrganizationAccess` and the
  operator-command pattern of `SigningKeyOperatorCommands`.
- **Pure core**: matching, the duplicate rule and re-projection are pure classes, unit-tested without a database. Only
  the store, installer and command touch the database, and only the endpoints touch `HttpContext`.

## Complexity Tracking

No constitution violations. Deliberate design points:

| Design point | Why needed | Simpler alternative rejected because |
|---|---|---|
| Database checks of a C# assignment (partial unique index, statement-level entry check, deferred completeness) | FR-018 mirrors writes in the database; a wrong reuse would silently corrupt identities that approvals and search key on | Application-only checks are bypassable by SQL. A `plpgsql` matcher duplicates the logic in the least testable layer (R1) |
| Install step in `migrate`, not a data migration | Older revisions need the same matcher, including FR-016a duplicates | A SQL data migration needs a second matcher and an owner-binding lift (GOZ-0003-IF-03) |
| Operator read branch on `manifest_revisions_select` | The install step and `projection-regenerate` must read projections and native documents as the operator | Unforced RLS or a definer helper widens more; lifting the owner binding is reserved for migrations |
| Separate publish rule catalogue | FR-005a requires validate to stay unchanged | Adding the rule to the GOZ-0006 catalogue changes validate and the stored catalogue version |
| Generation 1 without bytes | FR-009 forbids a copy that could diverge | Copying the bytes needs a consistency check forever |
| Per-capability `ordinal` | A stable, name-free list order for paging (FR-013) | UUIDv7 order is not creation order inside one millisecond, and name order is forbidden |
