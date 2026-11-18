# Implementation Plan: Immutable Revisions and Signed Attestations

**Branch**: `feature/goz-0007-immutable-revisions` | **Date**: 2026-11-18 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/007-immutable-revisions/spec.md`

## Summary

A `publisher` member publishes a manifest that GOZ-0006 validation accepts. Gözleme stores an immutable **revision**
and an Ed25519 **attestation**, signed with a registry-held key for the publisher. A second version becomes the next
revision. Every member can list and read revisions, see a verification result computed fresh from the stored bytes,
and download everything needed to verify the signature independently.

- **Signing** (maintainer P1, P2; research R1–R5):
  - Ed25519 through `NSec.Cryptography` 26.4.0, because .NET 10 has no standalone Ed25519.
  - The signature is a raw Ed25519 signature over the RFC 8785 (JCS) canonical bytes of a closed statement v1. The
    statement covers the canonical IDs, origin, version, sequence, native digest and length, adapter, projection
    version and **projection digest**, which resolves GOZ-0006-IF-08.
  - Statement v1 has no delegation, chain or transparency field. The verifier refuses unknown members, versions,
    algorithms and keys.
- **Custody** (Q1, Q5, clarify Q3; R6, R7, R9):
  - **Lazy creation**: one active key per publisher, created inside its first publication transaction.
  - **Encryption**: private seeds are encrypted with AES-256-GCM under a key-encryption key (KEK) supplied by Secret
    reference, with associated data that binds each blob to its publisher and key. A missing or wrong KEK makes
    publication answer `503` while reads keep working.
  - **Succession**: an operator-only command, `signing-key-succession`, adds a successor and an append-only retirement
    (`lost` or `compromised`). Old keys still verify.
- **Storage** (FR-002–FR-005, FR-018; R8, R10, R11):
  - One migration adds four forced-RLS tenant tables (`manifest_revisions`, `revision_attestations`,
    `publisher_signing_keys`, `signing_key_retirements`) and one per-account budget table.
  - Immutability guard triggers bind every login, including the owner.
  - SemVer-precedence uniqueness uses `version_key`, and gap-free sequences are assigned under a per-capability
    advisory lock.
  - A deferred constraint trigger makes an unsigned revision impossible to commit.
  - Insert policies require the `publisher` role and a held claim, so suspension is enforced in the database too.
- **Authorization** (Q4; R12, R13):
  - The new `manifest` resource has `publish` (`publisher` only), `list` and `read` (member-level), and `validate`
    moves to it.
  - 9 new scoped endpoints, in the GOZ-0006 order: origin → membership → permission → target → suspension → budget →
    media type and size → **unchanged validation** → version → key → `201`.
- **Product** (R17): **Publish revision** on the validation page, a **Revisions** list and a revision detail with
  Native, Projection and Attestation views, a key view, and downloads. The fixed proof wording never implies a
  publisher-held key.
- **Demo**: Playwright project `goz-0007` covers all five stories, database refusals as both logins, superuser tamper,
  independent Node and openssl verification, rename and reclaim provenance, key succession, the boundary scan, roles,
  Jaeger spans and a no-secret scan, plus regression of all earlier suites ([quickstart.md](./quickstart.md)).

## Technical Context

**Language/Version**: C# on .NET 10; TypeScript (Next.js web tier, Node 24 e2e); SQL (PostgreSQL 17) in one
migration; Python 3 governance scripts.

**Primary Dependencies**:
- existing: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql, OpenTelemetry .NET, the GOZ-0006 manifest pipeline
  (Corvus, YamlDotNet), Next.js, Vitest, Playwright, xUnit v3 + Testcontainers;
- **new** (maintainer P1): `NSec.Cryptography` 26.4.0 (MIT), which brings `libsodium` 1.0.22 (ISC) and is pinned
  centrally;
- BCL `AesGcm` and `SHA256`;
- no new web dependency (Node `crypto` verifies in e2e).

**Storage**: PostgreSQL. The new migration `20261121000000_ManifestRevisions` adds:
- the tables `manifest_revisions`, `revision_attestations`, `publisher_signing_keys` and `signing_key_retirements`
  (all `TenantOwned`, forced RLS), and `manifest_publication_attempts` (`GlobalIdentity`);
- the function `gozleme.is_operator()` (not a definer);
- insert-guard, immutability and deferred-attestation triggers.

See [data-model.md](./data-model.md).

**Testing**:
- **New unit suites**: `CanonicalJsonTests`, `AttestationStatementTests`, `RevisionVerifierTests`, `SigningKeyVaultTests`,
  `VersionKeyTests`.
- **New integration suites**: `RevisionPublishTests`, `RevisionImmutabilityTests`, `RevisionAtomicityTests`,
  `RevisionAuthorizationTests`, `RevisionIsolationTests`, `RevisionProvenanceTests`, `SigningKeySuccessionTests`,
  `RevisionBoundaryTests`, `RevisionTelemetryTests`, `RevisionPerformanceTests`.
- **Updated**: `OrganizationPermissionContractTests` (22 actions, 27 scoped endpoints), `EndpointAuthorizationTests`,
  the migration pins, `BootstrapBoundaryTests` (the `(manifest|revision|projection)` set), the RLS catalog and isolation
  matrix, and `ManifestNoStorageTests` (validate still stores nothing).
- **Web (Vitest)**: the publish control, the revision list and detail, the verification badge and reasons, the retired
  and compromised banners, the suspended badge, the wording guard, and `isSameOrigin` on the publish route.
- **Playwright**: project `goz-0007`.

**Target Platform**: Linux containers (Compose; Kubernetes via Helm). The changes:
- **Compose**: `GOZLEME_SIGNING_KEK` for `api` and `migrate`.
- **Helm**: a `signing.existingSecret` value and its environment variable in the API Deployment and the migrate Job
  image.
- `.env.example` and the chart README.

**Project Type**: web application (API + Next.js BFF) in the existing monorepo.

**Performance Goals**:
- publishing a 512 KiB native document in under 2 s (SC-007): validation dominates, and Ed25519 signing is under 1 ms;
- verification in under 1 s (two SHA-256 passes and one Ed25519 verify);
- list pages are bounded (50 by default, at most 100), with one verification per row.

**Constraints**:
- **Order**: the manifest is never parsed before origin, membership, permission, target, suspension and budget.
- **Rejections**: answered byte-identically to validate.
- **Version conflicts**: content is never compared.
- **Key material**: no key material, statement, signature or digest in logs, spans or metrics.
- **Custody**: the KEK is never stored in the database or in Data Protection.
- **Boundary pins**: `POST /api/v1/publish` still answers `404`. Earlier demos and pins hold, except the deliberate
  pin updates listed above.

**Scale/Scope**:
- **API**: 9 endpoints, 3 new actions plus 1 moved, a `Revisions/` module (publisher, store, statement, canonical JSON,
  verifier, key vault, succession command, limiter, telemetry, contracts), 1 migration and 4 audit events.
- **Web**: 2 pages, about 7 route handlers (publish plus downloads and reads), about 6 components.
- **e2e**: 1 Playwright suite plus a `psql` and operator-command helper.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | It stores and signs manifest documents only. Nothing is fetched, connected to or executed | PASS |
| II. Portable baseline | One new MIT package with a native ISC dependency, chosen by the maintainer (P1) and verified on nuget.org on 2026-11-18 (R1). No new service. The KEK is a Secret reference in Compose and Helm, with no managed KMS required. The API boots without a KEK (publication is unavailable) | PASS |
| III. Permanent identity and provenance | Revisions are immutable once signed, for every login. Canonical IDs only, unaffected by renames and reclaims. Native, projection and adapter versions are kept distinct and never re-derived. Unknown statement versions, fields, algorithms and keys are rejected, and statement v1 has no delegation, transfer or chain fields (CR-01, CR-05). Key succession is add-only, local and never claims authority to peers | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | Four new forced-RLS tenant tables with member reads and `publisher`-plus-held-claim inserts. One decision point with identical empty `404`. Isolation matrix for both logins. The operator branch `is_operator()` is the one new policy predicate, and `gozleme_app` cannot satisfy it (R9; security-review item) | PASS |
| V. Contracts and bounded slices | Statement v1, canonical JSON, the downloads, the key contract, the OpenAPI and the permissions delta are versioned. Lifecycle events, rotation API, revocation, child IDs, artifacts, discovery and "latest" are deferred (GOZ-0008/0009/0012/0026/0033/0039) | PASS |
| VI. Demo-first | Cumulative `goz-0007` browser demo with screenshots, real database refusals, tamper, independent verification, succession and Jaeger evidence, with earlier suites kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Through `drive-speckit` | PASS |
| IX/X. Progress and decisions | Q1–Q5, the clarify answers and P1–P2 are recorded in `progress.yaml` and the spec. Agent choices A1–A7 are disclosed for veto, and none conflicts with the spec. Package facts are checked, not assumed, and libsodium loading is "verify at vendoring" | PASS |

**Post-design re-check (after Phase 1): PASS.**
- **New surface**: 4 tenant-owned tables, 1 `GlobalIdentity` table, 1 non-definer function, triggers and the audit
  range 1501–1504. `DefinerHelpers` is unchanged. No new role or grant beyond `SELECT, INSERT` on the new tables.
- **Security-review items for convergence**:
  - the `is_operator()` branch on forced-RLS tables;
  - KEK handling and associated-data binding;
  - the absence of a foreign key on `published_by`/`created_by` (R11);
  - the deferred attestation trigger;
  - the canonical JSON writer;
  - the no-secret scan coverage;
  - the proof wording.
- **Vendoring risk**: libsodium native load in the runtime image and CI. A first implementation task signs and
  verifies a test vector (RFC 8032 test 1) in the container, with an explicit stop for maintainer review if it fails.
  P1 is never silently overridden.

## Project Structure

### Documentation (this feature)

```text
docs/specs/007-immutable-revisions/
├── spec.md
├── plan.md                                   # this file
├── research.md                               # R1–R20
├── data-model.md                             # 4 tenant tables, 1 budget table, functions, triggers, configuration
├── quickstart.md
├── contracts/
│   ├── attestation-statement-v1.md           # statement, canonical form, signature, downloads, verification reasons
│   ├── signing-keys.md                       # custody, KEK, operator succession, key view
│   ├── revision-api.openapi.yaml
│   └── permissions-delta.md                  # applied to docs/architecture/contracts/organization-permissions.md
├── checklists/requirements.md
├── progress.yaml
├── evidence/                                 # demo-run.json, revision-evidence.json, screenshots/ (created by the demo)
├── reviews/                                  # impact analysis (post-convergence)
└── tasks.md                                  # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── Revisions/                                    # new module
│   ├── RevisionEndpoints.cs                      # publish, list, read, 4 downloads, keys, public key; order R12
│   ├── RevisionContracts.cs                      # detail, summary, list, verification, key view, error bodies
│   ├── RevisionPublisher.cs                      # transaction: lock, version, key get-or-create, sign, insert (R7, R8)
│   ├── RevisionStore.cs                          # EF/Npgsql reads and inserts under TenantTransaction
│   ├── VersionKey.cs                             # SemVer precedence key (strip +build)
│   ├── CanonicalJson.cs                          # RFC 8785 subset (R3)
│   ├── AttestationStatement.cs                   # statement v1 build/parse, closed member set (R4)
│   ├── RevisionVerifier.cs                       # pure verifier, reasons R14
│   ├── Signing/{SigningKeyVault,SigningOptions,Ed25519Signer}.cs   # NSec, AES-GCM, KEK (R6)
│   ├── SigningKeyOperatorCommands.cs             # signing-key-succession (R9)
│   ├── RevisionPublicationLimiter.cs             # Gozleme:Manifests:PublishPerMinute (R15)
│   ├── RevisionTelemetry.cs                      # spans + counters (R16)
│   └── RevisionServiceCollectionExtensions.cs
├── Manifests/{ManifestRules.cs,SchemaRegistry.cs}      # + CatalogueVersion / BundleVersion constants
├── Organizations/OrganizationPermissions.cs     # manifest resource; publish/list/read; validate moved
├── Identity/SecurityAudit.cs                    # events 1501–1504
├── Persistence/
│   ├── Migrations/20261121000000_ManifestRevisions.cs
│   ├── GozlemeDbContext.cs                      # entities + snapshot
│   └── TableClassification.cs                   # 4 TenantOwned + manifest_publication_attempts
├── Program.cs                                   # signing-key-succession dispatch
├── GozlemeApp.cs                                # module registration, MapRevisionEndpoints
└── Gozleme.Api.csproj
Directory.Packages.props                         # NSec.Cryptography 26.4.0
THIRD-PARTY-NOTICES.md                           # NSec (MIT), libsodium (ISC)
compose.yaml  .env.example                       # GOZLEME_SIGNING_KEK (throwaway default)
deploy/helm/gozleme/{values.yaml,templates/api.yaml,templates/migrate-job.yaml,README.md}   # signing.existingSecret
web/src/
├── app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/page.tsx
├── app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/[revisionId]/page.tsx
├── app/organizations/[organizationId]/publishers/[publisherId]/signing-keys/page.tsx
├── app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/route.ts             # POST (isSameOrigin), GET
├── app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/revisions/[revisionId]/[part]/route.ts   # GET downloads
├── app/api/organizations/[organizationId]/publishers/[publisherId]/signing-keys/[keyId]/public-key/route.ts
├── components/{PublishRevision,RevisionList,RevisionDetail,AttestationPanel,VerificationBadge,SigningKeyList}.tsx   # + tests
├── components/{ManifestValidator,CapabilityList}.tsx    # Publish control; Revisions link
└── lib/revisions.ts                             # types, messages, proof wording
tests/Gozleme.Api.Tests/Revisions/{CanonicalJsonTests,AttestationStatementTests,RevisionVerifierTests,SigningKeyVaultTests,VersionKeyTests}.cs
tests/Gozleme.Api.IntegrationTests/
├── Revision{Publish,Immutability,Atomicity,Authorization,Isolation,Provenance,Boundary,Telemetry,Performance}Tests.cs
├── SigningKeySuccessionTests.cs
└── OrganizationPermissionContractTests.cs EndpointAuthorizationTests.cs BootstrapBoundaryTests.cs (+ migration/RLS pins)
docs/architecture/contracts/organization-permissions.md     # permissions delta applied
e2e/
├── playwright.config.ts                         # + goz-0007 (depends on goz-0006)
└── tests/{revision-demo.spec.ts, db.ts (psql as app/owner/superuser), operator.ts, evidence.ts (+ specDirs)}
```

**Structure Decision**: a new `Revisions/` module beside `Manifests/` and `Publishers/`.
- **Reuse**: it reuses `ManifestValidator` unchanged (FR-001), `OrganizationAccess.AuthorizeAsync`, `PublisherStore`
  (target, active state), `RegistryOriginReader`, `CanonicalId`, `TenantTransaction` and the operator-command pattern
  of `HandleOperatorCommands`.
- **Pure core**: canonical JSON, the statement, the verifier and the version key are pure classes, unit-tested
  without a database. Only `RevisionPublisher`/`RevisionStore` touch the database, and only the endpoints touch
  `HttpContext`.
- **Key boundary**: the key vault is the single place private material exists, and it is injected only into the
  publisher path and the operator command.

## Complexity Tracking

No constitution violations. Deliberate design points:

| Design point | Why needed | Simpler alternative rejected because |
|---|---|---|
| New package NSec (+ native libsodium) | .NET 10 has no standalone Ed25519, and the spec expects deterministic Ed25519 (P1) | BouncyCastle is large for one algorithm. ECDSA P-256 is non-deterministic. ML-DSA lacks broad independent verifiers |
| Own RFC 8785 subset writer | Statement bytes and the projection digest must be reproducible by anyone (FR-009) | Serializer output depends on declaration order and options. jsonb text is not a contract |
| Separate attestation table + deferred constraint trigger | Later specs add more attestations or lifecycle events per revision (GOZ-0029/0030/0033), while FR-005 forbids an unsigned revision | One combined row cannot hold a second attestation. Application-only atomicity is bypassable by SQL |
| Advisory locks (per-capability sequence, per-publisher shared/exclusive for signing vs succession) | Gap-free sequence, and the succession race with no failed publication | `FOR UPDATE` needs `UPDATE` privilege on immutable or renamable rows and blocks renames. A PostgreSQL sequence leaves gaps |
| `is_operator()` policy branch on forced-RLS tables | Succession is an operator action on tenant-owned keys (Q5, FR-018) | Unforced RLS (like `handle_claims`) contradicts FR-018. A definer helper widens `DefinerHelpers` |
| No FK from `published_by`/`created_by` to `users` | An immutable row cannot take `SET NULL`, and `RESTRICT` would block future erasure (CR-07) | A FK with either action breaks immutability or erasure |
| Revisions list verifies each row | US1-AS3 shows the verification status per row, and the result is never stored | Storing a verified flag is forbidden (FR-010) |
