# Implementation Plan: Typed Manifests for Capability Families

**Branch**: `feature/goz-0006-typed-manifests` | **Date**: 2026-11-17 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/006-typed-manifests/spec.md`

## Summary

A `publisher` member validates a manifest against one of their organization's capabilities. Validation is
stateless and synchronous, and nothing is stored (Q1). An accepted manifest returns three separate parts:
- the typed **envelope** (v1);
- the **native document**, unchanged, with a SHA-256 digest of its UTF-8 bytes;
- a separately versioned **normalized projection** (v1).

A rejected manifest returns categorized problems with locations and never echoes values.

- **Formats** (Q3, P3, P4; research R1):
  - **MCP**: a Gözleme-versioned MCP server manifest. Its primitives are typed by the official core schema of the declared revision, `2026-07-28` (stateless) or `2025-11-25` (stateful).
  - **A2A**: Agent Card 1.0 (v1.0.1).
  - **API**: OpenAPI 3.1 and 3.0.
  - **Event**: AsyncAPI 3.0.0.
  - **Skill**: Agent Skills `SKILL.md`, snapshot 2026-11-17.
  - **Model**: Gözleme model card v1.

  All schemas are bundled, digest-pinned and resolved offline.
- **Engine** (P1, P2; research R2, R3):
  - `Corvus.Json.Validator` validates against the official schemas, behind Gözleme strict overlays.
  - The YamlDotNet *event* parser reads restricted YAML.
  - Everything is parsed into one positioned tree, so locations are RFC 6901 pointers for JSON, YAML and front matter alike.
- **Strictness** (Q4; research R6–R10): unknown fields, reserved security claims (a vocabulary applied to extension points and to the fields each adapter lists), inline secrets (secret-bearing fields plus pattern list v1, with no entropy heuristic), identity mismatch and external references are all rejected, under a closed set of 11 categories and a fixed rule catalogue.
- **Authorization** (Q2; research R11): the new action `manifest.validate` on `capability` is held by `publisher` only. Order: membership (404) → permission (403) → target (404) → suspension (409) → per-account in-memory rate limit (429) → media type and size (415/413) → validate. No audit event is written (FR-023).
- **Product**: a **Validate manifest** page under the capability, with family picker, sample loader, three result panels and a problem list; a same-origin route handler; and a seeded demo publisher and capability (R14).
- **Demo**: Playwright project `goz-0006` covers all six families (both MCP modes), every rejection category, the boundary test, the role matrix, the no-storage check and the telemetry no-secret scan, plus regression of all earlier suites ([quickstart.md](./quickstart.md)).

## Technical Context

**Language/Version**: C# on .NET 10; TypeScript (Next.js web tier, Node 24 e2e); Python 3 governance scripts. No SQL changes.

**Primary Dependencies**:
- existing: ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql, OpenTelemetry .NET, Next.js, Vitest, Playwright, xUnit v3 + Testcontainers;
- **new** (maintainer P1/P2): `Corvus.Json.Validator` 5.8.11 (Apache-2.0) and `YamlDotNet` 18.1.0 (MIT), pinned centrally;
- no web dependency.

**Storage**: none. There is no migration, table, column, function or grant (FR-027, research R13). The slice reads the existing GOZ-0005 rows under existing policies.

**Testing**:
- **New integration suites**: `ManifestEndpointTests`, `ManifestNoStorageTests`, `ManifestTelemetryTests`, `ManifestSampleTests`, `ManifestPerformanceTests`.
- **New unit suites**: `ManifestParserTests`, `ManifestBoundsTests`, `SecretPatternTests`, `ReservedClaimTests`, `ProjectionBoundaryTests`, `ProjectionSchemaTests`, `McpManifestTests`, `ManifestRuleCatalogueTests`, `ManifestSchemaPinTests`, and an `OrganizationPermissionsTests` addition.
- **Updated**: `OrganizationPermissionContractTests` (19 actions, 18 scoped endpoints), `EndpointAuthorizationTests`, `TenantSeedTests` (the demo publisher and capability), `BootstrapBoundaryTests` (the product endpoint pins re-asserted, unchanged).
- **Web (Vitest)**: the validation page and its components, permission-hidden control, error messages, placeholder substitution, and the same-origin check in the new route handler.
- **Playwright**: project `goz-0006`.

**Target Platform**: Linux containers (Compose; Kubernetes via Helm). No Helm or Compose change, except that the seed is updated.

**Project Type**: web application (API + Next.js BFF) in the existing monorepo.

**Performance Goals**:
- a result in under 2 s for a 512 KiB native document (SC-006);
- schemas compiled once at start-up by a non-blocking warm-up;
- regular expressions with `NonBacktracking`, so matching time stays linear in the input.

**Constraints**:
- **Offline**: no network, file or process I/O from adapters, and no content execution.
- **Order**: the manifest is never parsed before membership, permission, target, suspension and the rate limit have passed.
- **No echoed values**: no value is echoed in a response, log, span or metric.
- **Bounded labels**: metric labels come from closed sets.
- **Determinism**: identical input gives an identical result.
- **Boundary pins**: earlier demos and the boundary pins stay unchanged. `POST /api/v1/publish` and `GET /api/v1/capabilities` still answer `404`.

**Scale/Scope**:
- **API**: 1 endpoint, 1 action, a `Manifests/` module (envelope reader, parsers, 6 adapters, rules, secret patterns, projection, validator, limiter, telemetry), about 14 bundled schema files with `SOURCES.md`, and a seed change.
- **Web**: 1 page, 1 route handler, about 4 components, about 20 sample files.
- **e2e**: 1 Playwright suite.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | Offline validation of documents only. Nothing is fetched, connected to or executed, and no MCP server is contacted (primitive lists are publisher-declared and advisory) | PASS |
| II. Portable baseline | No new service, store or configuration beyond a rate-limit default. Two new permissively licensed packages, verified for licence and advisories on 2026-11-17 (R17). JsonSchema.Net was rejected because of its fee-bearing licence. Bundled schemas, so it runs offline | PASS |
| III. Permanent identity and provenance | The identity claim must equal the canonical ID or alias exactly, with no lookup of other IDs (R8). Signatures, attestations, trust, delegation, transfer, visibility, ownership and lifecycle claims are rejected rather than trusted (CR-01). Native and projection are kept distinct, and the projection is versioned independently | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | One decision point. Non-members and foreign IDs get the identical empty `404`, and the existing forced RLS applies to the reads. Nothing is written, so there is no new policy surface | PASS |
| V. Contracts and bounded slices | The envelope, projection, rule catalogue, secret pattern list, native-format matrix, model card and MCP manifest are all versioned contracts. Storage, signing, child IDs and search are deferred to GOZ-0007, GOZ-0008 and GOZ-0013 | PASS |
| VI. Demo-first | Cumulative `goz-0006` browser demo with screenshots for every family, every rejection category, the boundary test, roles, no storage, and a telemetry no-secret scan, with earlier suites kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Through `drive-speckit` | PASS |
| IX/X. Progress and decisions | Q1–Q4, the clarify answers and P1–P4 are recorded in `progress.yaml` and the spec. Agent choices A1–A3 and the specify-time choices are disclosed for veto. Upstream facts are verified, and unverifiable ones are marked "verify at vendoring" rather than assumed | PASS |

**Post-design re-check (after Phase 1): PASS.**
- No new table class, definer helper, grant or audit range. `RlsCatalog` is unchanged.
- **Security-review items for convergence**: secret pattern list v1, the reserved vocabulary and where it applies, the parser restrictions, and the offline resolver.
- **Vendoring risks**: the Corvus run-time compile behaviour and error output (R2), the 2025-11-25 MCP schema dialect and the A2A bundle generation (R1). Each is a first implementation task with an explicit stop for maintainer review if the fact differs, so P1 is never silently overridden.

## Project Structure

### Documentation (this feature)

```text
docs/specs/006-typed-manifests/
├── spec.md
├── plan.md                                   # this file
├── research.md                               # R1–R17
├── data-model.md                             # no storage; in-memory entities
├── quickstart.md
├── contracts/
│   ├── manifest-validation.md                # request, order, result, categories, stages, bounds, rule catalogue
│   ├── manifest-api.openapi.yaml
│   ├── manifest-envelope-v1.schema.json
│   ├── native-formats.md                     # family → format matrix, per-adapter extension/reserved/secret rules
│   ├── mcp-server-manifest-v1.schema.json    # maintainer P4
│   ├── model-card-v1.schema.json
│   ├── projection-v1.md
│   ├── secret-patterns.md                    # pattern list v1 (security review)
│   └── permissions-delta.md                  # applied to docs/architecture/contracts/organization-permissions.md
├── checklists/requirements.md
├── progress.yaml
├── evidence/                                 # demo-run.json, manifest-evidence.json, screenshots/ (created by the demo)
├── reviews/                                  # impact analysis (post-convergence)
└── tasks.md                                  # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── Manifests/                                   # new module
│   ├── ManifestEndpoints.cs                     # POST …/capabilities/{capabilityId}/manifests/validate; order R11
│   ├── ManifestContracts.cs                     # result, problem, location, error bodies
│   ├── ManifestValidator.cs                     # pure pipeline (R5): stages, de-dup, ordering, cap
│   ├── EnvelopeReader.cs                        # envelope v1 (R3, R8)
│   ├── Parsing/{ManifestNode,JsonTreeReader,RestrictedYamlReader,SkillMarkdownReader,TextPositions}.cs
│   ├── Adapters/{IProtocolAdapter,McpAdapter,A2aAdapter,OpenApiAdapter,AsyncApiAdapter,AgentSkillsAdapter,ModelCardAdapter}.cs
│   ├── Rules/{ManifestRules,ReservedClaims,SecretPatterns,SecretFields,ExternalReferences}.cs
│   ├── Projection/{Projection,ProjectionBuilder}.cs
│   ├── Schemas/                                 # embedded resources + SOURCES.md (URL, tag/commit, SHA-256, licence)
│   │   ├── upstream/{mcp-2026-07-28,mcp-2025-11-25,mcp-registry-2025-12-11,a2a-1.0.1,oas-3.1-2026-11-05,oas-3.0-2024-10-18,asyncapi-3.0.0}.json
│   │   └── gozleme/{envelope-1,mcp-server-manifest-1,mcp-strict-*,agent-skills-2026-11-17,model-card-1,projection-1}.json
│   ├── SchemaRegistry.cs                        # offline resolver, compile once, warm-up hosted service
│   ├── ManifestValidationLimiter.cs             # per-account fixed window, Gozleme:Manifests:ValidatePerMinute
│   ├── ManifestTelemetry.cs                     # span + 2 counters (R16)
│   └── ManifestServiceCollectionExtensions.cs
├── Organizations/OrganizationPermissions.cs     # + manifest.validate (publisher only)
├── Identity/OperatorCommands.cs                 # seed: demo publisher (alice) + capability (carol), fixed IDs (R14)
├── GozlemeApp.cs                                # module registration, MapManifestEndpoints on the organizations group
└── Gozleme.Api.csproj                           # + 2 packages, embedded schemas
Directory.Packages.props                         # Corvus.Json.Validator, YamlDotNet (+ transitive pins)
web/src/
├── app/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/manifest/page.tsx
├── app/api/organizations/[organizationId]/publishers/[publisherId]/capabilities/[capabilityId]/manifests/validate/route.ts   # POST, isSameOrigin
├── components/{ManifestValidator,ManifestResult,ProblemList,FamilyPicker}.tsx   # + tests
├── components/CapabilityList.tsx               # "Validate manifest" link when permitted and active
├── lib/manifests.ts                             # types, messages, sample loading, placeholder substitution
└── samples/manifests/{index.json, mcp/, a2a/, api/, event/, skill/, model/}
tests/Gozleme.Api.IntegrationTests/
├── ManifestEndpointTests.cs  ManifestNoStorageTests.cs  ManifestTelemetryTests.cs  ManifestSampleTests.cs  ManifestPerformanceTests.cs
├── Gozleme.Api.IntegrationTests.csproj          # links web/src/samples/manifests/**
└── OrganizationPermissionContractTests.cs EndpointAuthorizationTests.cs TenantSeedTests.cs BootstrapBoundaryTests.cs
tests/Gozleme.Api.Tests/
├── Manifests/{ManifestParserTests,ManifestBoundsTests,SecretPatternTests,ReservedClaimTests,ProjectionBoundaryTests,
│              ProjectionSchemaTests,McpManifestTests,ManifestRuleCatalogueTests,ManifestSchemaPinTests}.cs
└── OrganizationPermissionsTests.cs
docs/architecture/contracts/organization-permissions.md     # permissions delta applied
e2e/
├── playwright.config.ts                         # + goz-0006 (depends on goz-0005)
└── tests/{manifest-demo.spec.ts, evidence.ts (+ specDirs)}
```

**Structure Decision**: a new `Manifests/` module beside `Publishers/`.
- **Reuse**: it reuses `OrganizationAccess.AuthorizeAsync`, `PublisherStore` (target and active state) and `RegistryOriginReader` (canonical ID) without duplicating them.
- **Mapping**: it maps on the organizations route group, so `TenantTransaction`, `no-store`, the client header filter and the contract test's `IsScoped` apply unchanged.
- **Pure core**: parsers, adapters, rules and projection are pure, I/O-free classes, unit-tested without a database. Only the endpoint touches `HttpContext` and the database.
- **Samples**: they live in the web tree because the UI bundles them, and the .NET tests link them, so there is one source of truth.

## Complexity Tracking

No constitution violations. Deliberate design points:

| Design point | Why needed | Simpler alternative rejected because |
|---|---|---|
| Two new packages (Corvus, YamlDotNet) | Official schemas span draft-04, draft-07 and 2020-12. YAML must be inspected at the event level to refuse anchors and tags | Hand-written OpenAPI/AsyncAPI validation diverges from upstream. JsonSchema.Net has a fee-bearing licence and no draft-04 (P1, P2) |
| Gözleme strict overlays over upstream schemas | Q4 requires unknown fields to be rejected, but the MCP and Agent Skills upstream objects are open | Editing vendored upstream files breaks digest pinning and provenance |
| A Gözleme-defined MCP server manifest | No upstream static document carries tools, resources or prompts (P4) | `server.json` and Server Cards deliberately omit primitives |
| A positioned tree shared by every format | FR-012 needs identical pointer locations for JSON, YAML and front matter, plus line and column for parse errors | `JsonDocument` and the YAML deserializers lose positions or expand aliases |
| An in-handler rate limiter instead of middleware | Non-members, forbidden callers and suspended targets must never see `429` (FR-019 order) | ASP.NET rate-limiting middleware runs before authorization |
| `200` for both accepted and rejected (A3) | A rejection is a successful validation, and authorization failures keep distinct statuses | `422` for rejected mixes "invalid manifest" with HTTP error handling in the BFF and UI |
