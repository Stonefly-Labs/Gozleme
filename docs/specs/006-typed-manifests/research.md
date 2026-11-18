# Research: Typed Manifests for Capability Families

Decisions for `plan.md`. Each entry gives the decision, the rationale and the alternatives considered.
Maintainer decisions Q1–Q4 (specify), the four clarify answers, and P1–P4 (taken during this plan) are
inputs here, not open questions (`spec.md` Clarifications; `progress.yaml`).

**Verified sources** (2026-11-17, primary sources only: specification sites, the upstream GitHub
repositories at the tags named, and the NuGet API). Facts that could not be verified are marked
**verify at vendoring** and are the first implementation tasks. No result in this file is invented. When
vendoring finds that a fact is different, the task records the difference and stops for review.

## R1 — Native format pins (Q3, P3, P4)

**Decision**: one adapter per family, and each adapter pins exact upstream material. In the envelope,
`native.format` and `native.formatVersion` select the adapter.

| Family | `native.format` | `formatVersion` | Encodings | Pinned upstream material | Dialect | Upstream closed? |
|---|---|---|---|---|---|---|
| `mcp` | `mcp-server-manifest` | `1` (Gözleme-defined, P4) | `json` | MCP core `schema/2026-07-28/schema.json` and `schema/2025-11-25/schema.json` (repo `modelcontextprotocol/modelcontextprotocol`, tags `2026-07-28` and `2025-11-25`); registry `server.schema.json` `2025-12-11` for `remotes` and `packages` | 2020-12 (2026-07-28); **verify at vendoring** for 2025-11-25 | the manifest root is closed by Gözleme; upstream objects are open, so Gözleme closes them (R2) |
| `a2a` | `a2a-agent-card` | `1.0` | `json` | A2A v1.0.1 (2026-05-26). `specification/a2a.proto` is normative; JSON Schema is generated from the proto at tag `v1.0.1` with A2A's own generator and vendored **unstitched**, as 47 per-message files under `upstream/a2a-1.0.1/`, each with its own `$id` (maintainer decision, 2026-11-17: the stitched `a2a.json` has 33 unresolvable relative `$ref`s). A Gözleme overlay adds the proto's `REQUIRED` fields, which the generator drops | 2020-12 | yes (`additionalProperties:false` on AgentCard, AgentCapabilities, AgentCardSignature) |
| `api` | `openapi` | `3.1` | `json`, `yaml` | OAS schema `https://spec.openapis.org/oas/3.1/schema/2026-11-05` (latest patch 3.1.2) | 2020-12 | yes (`unevaluatedProperties:false`) |
| `api` | `openapi` | `3.0` | `json`, `yaml` | OAS schema `https://spec.openapis.org/oas/3.0/schema/2024-10-18` (latest patch 3.0.4) | draft-04 | yes (`additionalProperties:false`) |
| `event` | `asyncapi` | `3.0.0` | `json`, `yaml` | `asyncapi/spec-json-schemas` `schemas/3.0.0-without-$id.json` (release v6.11.1) | draft-07 | yes (`additionalProperties:false`, root included) |
| `skill` | `agent-skills` | `2026-11-17` | `markdown` | agentskills.io specification as read on 2026-11-17. It has no version identifier, so the snapshot date is the version, and Gözleme transcribes the rules into a bundled JSON Schema | 2020-12 (Gözleme) | Gözleme closes it |
| `model` | `gozleme-model-card` | `1` | `json` | Gözleme-defined ([contracts/model-card-v1.schema.json](./contracts/model-card-v1.schema.json)) | 2020-12 | yes |

Version consistency rules:
- An OpenAPI document's `openapi` field must be in the declared line (`^3\.1\.\d+$` or `^3\.0\.\d+$`).
- An AsyncAPI document's `asyncapi` must be exactly `3.0.0`.
- An MCP manifest's `protocolRevision` must be `2026-07-28` or `2025-11-25`.

Any other value is `unsupported_format` at `/openapi`, `/asyncapi` or `/protocolRevision`.

**Verified at vendoring (T003/T004, 2026-11-17)**: the six upstream files were fetched from the pinned locations, and their SHA-256 values are listed in `Schemas/SOURCES.md` (T006). The MCP `2025-11-25` core schema is **2020-12**, like `2026-07-28`; neither file has a `$id`, so Gözleme registers them under its own bundled URIs. The registry `server.schema.json` 2025-12-11 is draft-07 (`definitions`). A2A: the published `https://a2a-protocol.org/v1.0.1/spec/a2a.json` is a 2020-12 bundle (`definitions`, 47 entries) produced by A2A's `scripts/proto_to_json_schema.sh` with bufbuild `protoc-gen-jsonschema`, and its `AgentCard`, `AgentCapabilities` and `AgentCardSignature` are closed. Generating from `a2a.proto` (T004, maintainer-approved container run; generator, versions and command are in `Schemas/SOURCES.md`) gives a bundle byte-identical to the published file (SHA-256 `ba4b702b…55d8dd`), so no material difference. Definitions are keyed by title with spaces (`Agent Card`), so overlays reference `#/definitions/Agent Card`.

Later upstream versions each become a new adapter version or format version. They are never edits to an
existing one: OpenAPI 3.2 (3.2.1, 2026-09-10), AsyncAPI 3.1.0 (2026-01-31, P3), and newer MCP revisions.

**Rationale**: these are the latest stable releases in each line the maintainer chose, verified on
2026-11-17.
- **OpenAPI**: the 3.1 schema has been reissued five times since 2022-10-07. The newest dated ID is pinned,
  so a schema bug fixed upstream is not frozen in.
- **MCP (P4)**: the registry `server.json` and Server Cards (SEP-2127) deliberately omit primitives.
  - Only the core schema types tools, resources, resource templates and prompts. The manifest therefore
    reuses those `$defs` unchanged and adds a closed Gözleme root.
  - Statefulness is a property of the protocol revision. SEP-2575 and SEP-2567, both Final in `2026-07-28`,
    removed `initialize` and `Mcp-Session-Id`. So the mode is derived from `protocolRevision`, and there is
    no free-form flag that could disagree with it.

**Alternatives rejected**: `server.json` alone (no primitives; P4); Server Card v1 (pre-release, `$schema`
URL returns 404, remotes only); a free `stateful: true|false` flag (could contradict the revision);
pinning OpenAPI 3.1.0's 2022-10-07 schema (superseded upstream).

## R2 — Schema validation engine (P1)

**Decision** (P1 as revised by the maintainer on 2026-11-17, after blocker GOZ-0006-B1): `Corvus.Text.Json.Validator` 5.8.11 (Apache-2.0), the V5 runtime evaluator. It needs no code generation or Roslyn, and its results come from a `JsonSchemaResultsCollector` at `Detailed` level. It replaces the originally chosen V4 `Corvus.Json.Validator` 5.8.11, which compiled C# with Roslyn at run time and could not compile the AsyncAPI 3.0.0 schema. Its CTJ* analyzers are removed from compilation in `Directory.Build.targets`, because they would rewrite unrelated code.
- **Bundled schemas**: every upstream and Gözleme schema is an embedded resource under `src/Gozleme.Api/Manifests/Schemas/`. Its source URL, tag or commit and SHA-256 are listed in `Schemas/SOURCES.md`. `ManifestSchemaPinTests` recomputes the digests, so changing a bundled schema fails a test.
- **Offline**: schemas are registered by their `$id` in an in-memory document resolver. The resolver refuses every URI that is not bundled, so the validator can never reach the network or the file system (FR-006). A test proves that resolving an unbundled `$ref` fails rather than fetches.
- **Closing open upstream objects** (MCP core types, `server.json` shapes, Agent Skills): we do not edit upstream files. A small Gözleme *strict overlay* schema per format references the upstream `$defs` and adds `unevaluatedProperties: false` (2020-12) at each object that Gözleme closes. Extension locations stay open (R6).
  - Overlays are Gözleme files with their own digests.
  - For the draft-07 `server.json` shapes, the overlay is a 2020-12 schema that references the draft-07 `$defs`, so `unevaluatedProperties` applies at the overlay level. Verified for V5: mixed-dialect references evaluate, and `unevaluatedProperties` at the overlay closes the draft-07 shapes.
- **Error mapping**: schema output is reduced to `(keyword, instance location)` pairs. The keyword is the last segment of the evaluation path of a leaf failure; parent "expected to match the subschema" entries are dropped.
  - `additionalProperties`/`unevaluatedProperties`/`propertyNames` failures become `unknown_field` at the property's pointer.
  - A failed in-place subschema discards its annotations, so `unevaluatedProperties` also reports members that subschema declares. Such a member is dropped only when the schema owning the failing `unevaluatedProperties`, or an in-place applicator beside it (`$ref`, `allOf`, `if`/`then`/`else`, `dependentSchemas`, and the `anyOf`/`oneOf` branches the object matches when validated against each branch alone), declares it. A member nothing declares is always reported, and an invalid document never yields zero problems (maintainer decision, 2026-11-17, "separate real from false"; `SchemaDeclarations`). Remaining limit: when an object matches no branch of an alternative, every branch counts, and the alternative's own failure is reported.
  - Every other keyword becomes `invalid_value` at its instance location.
  - Nothing from the schema's message text is returned (FR-012). Message text comes only from the fixed catalogue (R9).
- **Compile cost**: schemas are compiled once at start-up, by a hosted warm-up that does not block health. Measured for V5: each pinned schema compiles in 1–68 ms, with no Roslyn or dynamic assembly (evidence `evidence/corvus-v5-spike.txt`).

**Rationale**: P1. It is the only validator with a permissive licence that covers draft-04 (OpenAPI 3.0) and 2020-12 (OpenAPI 3.1, A2A, MCP). Upstream schemas are the conformance source, so Gözleme does not re-implement OpenAPI or AsyncAPI.

**Alternatives rejected**: JsonSchema.Net 9.7.0 (NuGet licence is the OSMF EULA, which needs licence acceptance and a fee for revenue-generating users, and it has no draft-04); NJsonSchema (centred on draft-04, weak on 2020-12); hand-written validators (P1).

**Verified at vendoring (T002, 2026-11-17; evidence `evidence/corvus-spike.txt`)**:
- (a) **Differs — blocker GOZ-0006-B1.** Corvus.Json.Validator 5.8.11 generates C# from each schema and compiles it at run time with Roslyn (`Microsoft.CodeAnalysis.CSharp` 5.3.0, loaded into a collectible dynamic assembly). It pulls in about 20 MB of new assemblies (Roslyn, Corvus code generation, `Corvus.Json.CodeGeneration.HttpClientDocumentResolver`, Humanizer) and needs Roslyn transitive pins that also move EF Core Design's Roslyn to 5.3.0. Cold compile of the OAS 3.1 schema took about 7 s. **Compiling the AsyncAPI 3.0.0 bundle fails** after about 12 s ("Unable to compile generated code": the generated `Asyncapi300.AnySchema`/`MultiFormatSchema` code contains `\0` characters and does not parse). The package README also calls 5.8.11 a "V4 package" and points new projects at the V5 `Corvus.Text.Json` family.
- (b) Holds at `ValidationLevel.Detailed`: each failure gives the evaluation path, whose last segments name the keyword (for example `…/unevaluatedProperties/bogus`, `…/properties/title/type`), and the instance location as a JSON Pointer fragment (`#/info/bogus`). `Flag` gives no results and `Basic` gives no locations.
- (c) Holds: a 2020-12 overlay `$ref`s draft-07 `server.json` `definitions` and 2020-12 MCP `$defs`, and its `unevaluatedProperties: false` rejects extra members of both.
- (d) Holds: the draft-04 OAS 3.0 schema validates and rejects an unknown root member through `additionalProperties`.
- Offline: an unbundled `$ref`, with file-system and HTTP resolution off, is refused at compile time (`Unable to locate the root document`) and is not fetched.

**V5 spike (maintainer decision on B1, 2026-11-17; evidence `evidence/corvus-v5-spike.txt`)**: `Corvus.Text.Json.Validator` 5.8.11 (Apache-2.0; depends only on `Corvus.Text.Json`, `Corvus.Regex.Ecma262` and NodaTime; about 2.6 MB) evaluates schemas at run time **without code generation or Roslyn**: no `Microsoft.CodeAnalysis` assembly and no dynamic assembly is loaded.
- (a) Cold compile: OAS 3.1 68 ms, OAS 3.0 4 ms, **AsyncAPI 3.0.0 24 ms (compiles and validates)**, MCP 2026-07-28 5 ms, MCP 2025-11-25 2 ms. The first validation took 1.1 ms.
- (b) `JsonSchemaResultsLevel.Detailed` gives the evaluation path (the keyword is its last segment), the schema location and the instance location as an RFC 6901 pointer (`/info/bogus`, `/paths/~1a/get/nope`). Parent "expected to match the subschema" entries also appear and must be filtered to their leaf keywords.
- (c) Holds: a 2020-12 overlay over draft-07 `server.json` and 2020-12 MCP `$defs` rejects extra members of both.
- (d) Holds: draft-04 OAS 3.0 validates and rejects an unknown root member.
- (e) Holds: an unbundled `$ref` is refused at compile time (`JsonSchemaCompilationException`) through a `JsonSchemaDocumentResolver` delegate over in-memory bytes, with file-system and HTTP resolution off.
- **A2A finding (not engine-specific)**: the A2A bundle keeps 33 relative `$ref`s to per-message files (`lf.a2a.v1.AgentCapabilities.jsonschema.json`, `google.protobuf.Struct.jsonschema.json`, …), because its `jq` bundling step deletes each `$id` but does not rewrite references. So it cannot be compiled as one document by any validator. **Resolution (maintainer, 2026-11-17)**: vendor the 47 unstitched generator outputs. Each one's `$id` equals its file name, and all references resolve. V5 compiles `lf.a2a.v1.AgentCard.jsonschema.json` in 4 ms and reports `/bogus` and `/capabilities/bogus` as `additionalProperties` failures.
- **A2A required fields**: `protoc-gen-jsonschema` v0.6.0 does not emit `required` for the 61 `(google.api.field_behavior) = REQUIRED` fields in `a2a.proto`, and neither does the published `a2a.json`. The Gözleme A2A strict overlay (`gozleme/a2a-strict-1.0.1.json`, T005) restates them as `required` lists taken from the proto annotations, which are normative (agent choice, disclosed for veto).

## R3 — Parsing into one positioned tree (P2, FR-016)

**Decision**: every part is parsed into one immutable `ManifestNode` tree: object (ordered members), array, string, number (kept as source text), boolean or null. Each node carries its RFC 6901 pointer and its start line and column in its source text. Schema validation, adapter rules and projection all read this tree, never the raw text.
- **Envelope (the request body)**: `Utf8JsonReader` with `JsonReaderOptions { MaxDepth = 64, CommentHandling = Disallow, AllowTrailingCommas = false }`. Gözleme detects duplicate member names itself while building the tree, which is ordinal and per object. It does not rely on `AllowDuplicateProperties`, because that option belongs to `JsonDocument` and the serializer, not the reader (**verify at vendoring**; if the reader exposes it, use it as a second check). Line and column are computed from the reader's byte offset.
- **Native JSON**: the same reader over the UTF-8 bytes of the native string. A leading U+FEFF is preserved in the bytes and the digest, and the parser skips it.
- **Native YAML** (OpenAPI and AsyncAPI, P2): only the low-level `YamlDotNet.Core.Parser` event stream, from YamlDotNet 18.1.0 (MIT).
  - The following are `malformed`: any `AnchorAlias` event, any node with a non-empty `Anchor` or an explicit `Tag` (including `!!str`), a `<<` merge key, more than one document, a complex (non-scalar) mapping key, and a duplicate key.
  - Plain scalars resolve with the **YAML 1.2 core schema**: `null`, `~` and empty → null; `true`/`false` → boolean; the integer and float patterns of the core schema → number; everything else → string. So `yes`/`no`/`on`/`off` stay strings, unlike YAML 1.1.
  - Line and column come from the event `Start` mark.
  - YamlDotNet has no built-in depth or alias limit (agent check, 2026-11-17), so Gözleme counts depth and nodes while streaming and stops at the limits (R4) before building more tree.
- **`SKILL.md`**:
  - The native string, after an optional BOM, must start with a `---` line, followed by YAML front matter and a closing `---` line. Otherwise it is `malformed` at line 1.
  - The front matter is parsed with the YAML rules above. The body after the closing line is not parsed. It is kept in the native bytes and scanned only by the secret pattern list (R7).
- **Invalid text**:
  - A native string with a lone surrogate (from a `\uD800` escape) cannot be encoded as UTF-8, so it is `malformed` at its position in the request body.
  - Control characters other than tab, LF and CR in a native JSON or YAML text are `malformed` (JSON already forbids them unescaped inside strings).

**Rationale**:
- One tree with pointers makes FR-012 locations identical for JSON, YAML and front matter.
- Event-level YAML parsing is the only way to see anchors, aliases and tags before they are expanded.
- The core schema avoids the YAML 1.1 boolean trap, which would otherwise change OpenAPI documents' meaning.

**Alternatives rejected**:
- YamlDotNet's deserializer or `YamlStream`: these expand aliases and resolve merge keys and tags.
- `JsonDocument` for the tree: it has no line and column, and returns the first or last duplicate depending on options.
- Our own YAML parser: P2.

## R4 — Bounds (FR-016, SC-006)

**Decision** (documented in [contracts/manifest-validation.md](./contracts/manifest-validation.md)):

| Bound | Value | When exceeded |
|---|---|---|
| Request body (transport) | 1 MiB, read by the handler with a counting stream | `413 {"error":"too_large"}`, nothing parsed |
| Envelope without the native document | 16 KiB of UTF-8 | problem `too_large`, no location |
| Native document | 512 KiB of UTF-8 bytes (the digest input) | problem `too_large`, no location |
| Nesting depth (envelope and native, separately) | 64 | problem `too_large` |
| Nodes per native tree | 50,000 | problem `too_large` |
| Member name length | 1,024 characters | problem `too_large` |
| Number length (JSON and YAML numeric scalars) | 1,024 characters, checked before conversion | problem `too_large`, rule `bound.number` (maintainer decision 2026-11-17, T079) |
| Location length (RFC 6901 text of any value's pointer, envelope and native) | 4,096 characters, checked while reading | problem `too_large`, rule `bound.location` (maintainer decision 2026-11-17, T084) |
| Schema results (the validator's result buffer cannot grow) | buffer overflow | problem `too_large`, rule `bound.results`, never a 500 (maintainer decision 2026-11-17, T084) |
| Entries per family (tools + resources + templates + prompts; skills; operations; channels + operations) | 1,000 | problem `too_large` |
| Problems returned | 50, in stable order, then `"truncated": true` | — |

`too_large` and `malformed` stop processing immediately (spec edge case), and the result holds that one problem.

**Rationale**:
- 512 KiB comfortably holds large real OpenAPI documents. Very large ones can be split per API, and the bound is a contract value that can be raised later.
- The 1 MiB transport cap leaves headroom for JSON string escaping of the native text.
- SC-006 (under 2 s at the limit) is measured by `ManifestPerformanceTests` with a 512 KiB OpenAPI sample.

**Alternatives rejected**: `RequestSizeLimitAttribute`. It aborts with an empty 413 from Kestrel, so the documented error body could not be returned. Unbounded documents violate FR-016.

## R5 — Validation pipeline and problem order (FR-012, FR-015)

**Decision**: a pure function `ManifestValidator.Validate(bytes, target) → Result`. The endpoint calls it only
after membership, permission, target, suspension and the rate limit have passed (R11). Stages:

1. **Envelope parse**. Failures are `malformed` (envelope line and column) or `too_large`. Both stop.
2. **Envelope checks**:
   - `envelopeVersion` (`unsupported_version`, which stops);
   - `family` (`unsupported_family`, stops);
   - `native.format`/`formatVersion` for that family (`unsupported_format`, stops);
   - `native.encoding` for that format (`malformed`, stops);
   - the remaining checks collect problems without stopping: unknown fields (`unknown_field`), reserved names (`reserved_claim`, R6), `version` SemVer 2.0.0 syntax (`invalid_value`), identity (`identity_mismatch`, R8), and secret patterns in descriptive fields (`secret_value`).
3. **Native parse**: size, encoding, parse, depth and nodes. Failures are `malformed` or `too_large`, and both stop.
4. **Native checks** (collecting):
   - version consistency (R1);
   - schema validation against the strict overlay (R2);
   - adapter rules: reserved claims (R6), secret-bearing fields and the pattern list (R7), external references (R10), and the entry cap.
5. **Projection**: built only when there is no problem at all.

Problems are de-duplicated by `(part, pointer, category, rule)`. They are ordered by part (`envelope` before
`native`), then by pointer compared segment by segment (array indices numerically, names ordinally), then
by category in contract order, then by rule. Every distinct rule at a pointer is reported (maintainer decision,
2026-11-17, replacing "one problem per pointer", which contradicted the spec's "all problems found are reported").

**Rationale**: a stable order makes results deterministic (FR-015) and diffable in tests. Stopping only on
the stages that make later checks meaningless satisfies the spec's "parsing failures stop processing"
rule, and still lists every independent problem.

## R6 — Reserved claims and extension mechanisms (FR-007, FR-009, Q4)

**Decision**: reserved claims are detected in two places.

1. **Reserved vocabulary in name positions that Gözleme controls or that are extension points**:
   - every envelope member name;
   - every member name inside a format's extension mechanism;
   - each adapter's listed native fields.

   - every member name nested inside an extension's value (`x-*` values, MCP `_meta` values and `capabilities.experimental`, Agent Skills `metadata`), as for A2A extension `params` (convergence T075);
   - the member names of closed native objects (Agent Skills front matter, the closed MCP objects other than schemas and variable names, the model-card root), so a reserved name there is both `unknown_field` and `reserved_claim` (US2/AC2, convergence T076).

   A name's words are found as follows (maintainer decisions 2026-11-17, "harden all" and "built-in fold table"):
   1. Fold look-alike characters to ASCII with the generated table `ReservedClaimFolds` (`scripts/reserved-folds/generate.py`: fullwidth forms, roman numerals, mathematical and letterlike alphanumerics, enclosed letters, super/subscripts, modifier letters, ligatures, accented Latin letters, dotless `ı`). The API runs with invariant globalization, where .NET applies no NFKC, so the table stands in for it. It also folds the Cyrillic and Greek letters that Unicode TR39 confusables 18.0.0 (pinned by SHA-256 in the generator) lists as look-alikes of ASCII letters or digits, such as Cyrillic `е` and Greek `ν`, and the Latin-script letters it lists that have no plain form, such as small-capital `ᴠ` and IPA `ɑ` (maintainer decisions 2026-11-17). It is not full NFKC, and look-alikes from other scripts are not folded.
   2. Remove default-ignorable characters (Unicode `Default_Ignorable_Code_Point`: format characters such as zero-width space and soft hyphen, variation selectors, and the blank Hangul fillers).
   3. Remove the namespace prefix up to the last `/` or `.`, then a leading `x-`. The whole name is also checked without this cut, with dots and slashes as word separators, so `x-trust.level` matches like `x-trust-level` and `x-verified.` like `x-verified` (maintainer decision 2026-11-17).
   4. Split at every character that is not a letter or digit and at camelCase boundaries, then lower-case.

   A name matches when its words joined, or the joined form of any trailing run of its words, is in the vocabulary. The trailing runs make `x-gozleme-verified` and `x-isVerified` match; they also reserve names such as `x-api-status` in extension positions (maintainer decision, 2026-11-17). `x-verifiedBy` does not match, because `verified` is not a trailing word:

   `signature, signatures, sig, jws, attestation, attestations, provenance, trust, trusted, trustlevel, verified, verification, approved, approval, certified, delegation, delegations, delegationchain, delegatedby, transfer, transfers, transferchain, transferredfrom, visibility, visible, grant, grants, sharing, sharedwith, acl, owner, ownedby, publisher, publishedby, organization, status, lifecycle, deprecated, revoked, withdrawn, yanked`.

   A match is `reserved_claim`, rule `reserved.vocabulary`.
2. **Adapter-listed native fields** (documented per adapter in [contracts/native-formats.md](./contracts/native-formats.md)):
   - A2A `/signatures` (JWS card signatures) → `reserved_claim`, rule `reserved.a2a-signatures`.
   - MCP `_meta` keys under `io.modelcontextprotocol.registry/` other than `publisher-provided` (for example the registry-asserted `official` status) → `reserved_claim`.
   - Model card: no native field is reserved, because the card is closed and defines none. A reserved name at the root is therefore reported by rule 1 as `reserved_claim`, alongside the schema's `unknown_field` at the same pointer.

| Format | Extension mechanism (preserved verbatim, never projected) |
|---|---|
| MCP manifest | `_meta` objects at the root and on `serverInfo`, each tool, resource, resource template and prompt (MCP's own mechanism). Keys must be reverse-DNS prefixed (`^[a-z0-9-]+(\.[a-z0-9-]+)+/`), otherwise `invalid_value`. The prefix `io.modelcontextprotocol/` is reserved upstream and is `reserved_claim` |
| A2A Agent Card | `capabilities.extensions[]` (`uri`, `description`, `required`, `params`). `params` is free-form, and its member names are checked against the vocabulary |
| OpenAPI 3.0/3.1 | `^x-` members wherever the schema allows them |
| AsyncAPI 3.0.0 | `^x-[\w\d.\-_]+$` members wherever the schema allows them |
| Agent Skills | the `metadata` map (string → string) |
| Model card v1 | `^x-` members at the root and in `modalities` and `limits` |

**Protocol-native `deprecated` flags** on OpenAPI operations, parameters, headers and schemas, and on
AsyncAPI messages and schemas, describe an API element. They are not a publisher-asserted lifecycle state of
the capability, so they are **not** reserved (A1, maintainer approved 2026-11-17). The vocabulary is applied only to
name positions in rules 1 and 2, never to user data such as schema property names (for example a
`components.schemas.Repo.properties.owner` field) or example values. Otherwise ordinary API descriptions
would be rejected. Sample data is recognised by **position**, not by name (maintainer decisions 2026-11-17, T083):
`example`, `default`, `enum`, `const` and an array-valued `examples` are data only as fields of an ordinary object,
and an OpenAPI Example object's `value` is data. An entry of a map that has one of those names (`responses.default`,
a schema or parameter named `enum`) and an Example object's own members are structure and are checked. The same
positions apply to the R10 reference walk.

**Rationale**: FR-009 says each adapter documents its mapped fields. Extension points are the only places
where a publisher can invent names, so that is where trust claims would be smuggled in. Normalization catches
`x-gozleme-verified`, `io.example/trust-level` and `Approved` alike.

**Alternatives rejected**: matching the vocabulary everywhere (false positives on API data models);
matching only exact names (trivially evaded by prefixes).

## R7 — Inline secrets (FR-010, clarify Q3)

**Decision**: two deterministic rules, plus a defined reference form.

- **Rule 1, secret-bearing fields** (per adapter, [contracts/native-formats.md](./contracts/native-formats.md)): any non-empty value is `secret_value`, rule `secret.field`, unless it is a secret reference or a placeholder.
  - MCP: a `remotes[].headers[]`, `remotes[].variables{}` or `packages[].environmentVariables[]` entry whose `isSecret` is `true`, or whose header `name` is (case-insensitive) `Authorization`, `Proxy-Authorization`, `Cookie`, `X-Api-Key`, `Api-Key` or `X-Auth-Token`. Its `value` and `default` are checked.
  - OpenAPI and AsyncAPI: parameters and headers with those names (`in: header` or `in: cookie`). Their `example`, `examples.*.value` and `default` are checked.
  - The envelope, A2A, skill and model card have no secret-bearing fields. Only rule 2 applies to them.
- **Rule 2, pattern list v1** ([contracts/secret-patterns.md](./contracts/secret-patterns.md)): every string scalar and every member name, in the envelope and the native tree, plus the unparsed `SKILL.md` body line by line and every YAML comment (OpenAPI/AsyncAPI YAML and `SKILL.md` front matter, from `#` to the end of the line; maintainer decision 2026-11-17, T078), is matched against a fixed, ordered list of anchored .NET regular expressions. They are compiled with `RegexOptions.CultureInvariant | NonBacktracking` and a match timeout, so matching time stays linear in the input. The list covers PEM private keys, `Bearer`/`Basic`/`Digest` credentials, JWTs, URLs with `user:password@`, AWS access key IDs, GitHub, GitLab, Slack, Stripe, Google, OpenAI, Anthropic and npm token prefixes, and Azure `AccountKey=`/`SharedAccessSignature=` connection-string parts.
- **Accepted forms**: a value equal to a secret reference `secretref:<provider>/<key>` (provider `^[a-z][a-z0-9-]{0,31}$`, key `^[A-Za-z0-9._/-]{1,128}$`); an MCP placeholder `{NAME}` (`^\{[A-Za-z_][A-Za-z0-9_]*\}$`); an empty or absent value; a declaration such as `isSecret: true` with no value. The reference form is disclosed agent choice A2, and a later secrets spec may supersede it with a new manifest secret reference version.
- **Location for secrets in names**: when a member *name* matches, the problem location is the parent object's pointer, with rule `secret.name`. Pointers never contain a matched credential. A `SKILL.md` body or YAML comment match is located by line and column.
- **No entropy heuristic** (clarify Q3). A UUID, a digest or `packages[].fileSha256` is never flagged by randomness alone. Pattern list v1 is a versioned contract, and changing it is a contract change that needs security review.

**Rationale**: the clarify decision. `NonBacktracking` removes the risk of catastrophic backtracking on
untrusted input. Putting secrets in names is the obvious evasion, and parent-pointer locations close it.

## R8 — Identity claim (FR-011)

**Decision**: the envelope `capability` string matches when it is, ordinally, either:
- `CanonicalId.Capability(verifiedOrigin, capability.Id)`, or
- the alias `<held handle>/<capability name>`, built from the target's publisher handle (the publisher is active, because suspension was checked first) and the immutable capability `name`.

Anything else is `identity_mismatch` at `/capability`, rule `identity.capability`. This includes another canonical ID, another alias, a foreign origin, upper-case variants, surrounding whitespace and malformed strings. No other capability is ever looked up, so the answer cannot reveal whether the claimed one exists. The alias is computed from rows the caller already reads, and `alias_reservations` stays unreadable (GOZ-0005-IF-01). Native names (`serverInfo.name`, A2A `name`, `info.title`, skill `name`, model `name`) are projected only (GOZ-0005-IF-01).

**Alternatives rejected**: resolving the claimed ID to report "different capability" (reveals existence); trimming or case-folding (namespace rule says reject, never normalize).

## R9 — Problem and result shape (FR-012, FR-013)

**Decision** ([contracts/manifest-validation.md](./contracts/manifest-validation.md)):
- **Both outcomes** answer `200`, with `{"outcome":"accepted", …}` or `{"outcome":"rejected","problems":[…],"truncated":false}` (disclosed agent choice A3). A rejection is a successful validation, and authorization failures keep their own statuses, so clients can tell "you may not" from "this is invalid".
- **Problem**: `{category, rule, location, message}`.
  - `location` is `{"part":"envelope"|"native","pointer":"…"}`, or `{"part":…,"line":n,"column":n}` for `malformed` (except `parse.not-string` and an encoding the format does not allow, which carry the envelope pointer; maintainer decision 2026-11-17), `SKILL.md` body secrets and YAML-comment secrets, or `null` for `too_large`.
  - `rule` and `message` come from one fixed catalogue (`ManifestRules.cs`, pinned by `ManifestRuleCatalogueTests`). No message is built from input.
- **Accepted result**:
  - `envelope`: every envelope field except `native.document`;
  - `native`: `{format, formatVersion, encoding, document, digest:"sha256:<64 lowercase hex>", byteLength}`, where `document` is the submitted string unchanged;
  - `projection` (R12).

## R10 — External references (FR-006)

**Decision**:
- A `$ref` or `$dynamicRef` whose value does not start with `#` is `external_reference`, rule `ref.external`, except inside sample data, recognised by position as in R6 (T083). This applies to OpenAPI and AsyncAPI documents, to MCP `inputSchema`/`outputSchema`, and to embedded JSON Schemas. Internal `#…` references must resolve within the same document; otherwise the value is `invalid_value`, rule `ref.unresolved`.
- A `$schema` or `jsonSchemaDialect` value outside the bundled dialect list is `external_reference`, rule `ref.dialect`, because it would need fetching. The bundled list is draft-07, 2019-09 and 2020-12, and for OpenAPI 3.1 also the OAS dialect `https://spec.openapis.org/oas/3.1/dialect/base`.
- Informational URLs (`externalDocs.url`, `documentationUrl`, `iconUrl`, `websiteUrl`, `servers[].url`, remote endpoint URLs, OpenAPI Example `externalValue` and cross-document Link `operationRef` (maintainer decision 2026-11-17)) are data and are never fetched or rejected for being URLs. They are still subject to the secret patterns (userinfo).

## R11 — Endpoint, authorization order and rate limit (FR-019, FR-020)

**Decision**:
- **Endpoint**: `POST /api/v1/organizations/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}/manifests/validate`, with action `manifest.validate` on resource `capability`. It is mapped by a new `MapManifestEndpoints` on the organizations route group, so `TenantTransaction`, `no-store`, `WebClientHeaderFilter` and the contract test's `IsScoped` apply unchanged.
- **Order**:
  1. origin verified (`503`);
  2. membership (empty `404`);
  3. permission (`403`);
  4. capability lookup under the publisher (`404`, the same body for malformed, unknown and foreign IDs);
  5. publisher active (`409 {"error":"suspended"}`);
  6. rate limit (`429 {"error":"rate_limited"}` with `Retry-After`);
  7. body read (`415` unless `application/json`; `413` over 1 MiB);
  8. validation.

  The manifest is never parsed before step 5 (FR-019).
- **Rate limit** (disclosed agent choice from specify):
  - `ManifestValidationLimiter`: an in-memory `PartitionedRateLimiter` keyed by account ID, with a fixed window of `Gozleme:Manifests:ValidatePerMinute` (default 30).
  - It runs inside the handler after suspension, so a non-member, a forbidden caller or a suspended target never spends or sees `429` (pattern: GOZ-0005 U1).
  - A rejection is recorded as `gozleme.manifest.outcome=rate_limited` on the operation's own span and counter, never through the shared `OnRejected` sign-in path (GOZ-0002-IF-08).
  - Because the limiter is not middleware, no `RateLimitedOperation` metadata is needed.
- **No `RequestSizeLimitAttribute`**: R4's counting reader is used instead.

## R12 — Normalized projection v1 (FR-014, User Story 3)

**Decision** ([contracts/projection-v1.md](./contracts/projection-v1.md)): `projectionVersion: "1"` and
`adapter: "<family>/<n>"`, separate from `envelopeVersion` and `native.formatVersion`. The common fields
are `family`, `kind`, `nativeName`, `title`, `description` and `declaredVersion` (from the envelope), plus:
- `attributes`: a closed per-family set;
- `entries[]`: `{kind, nativeName, title?}`, in native document order;
- `derived[]`: JSON Pointers into the projection that mark derived or defaulted values.

| Family | `kind` | `attributes` | `entries[].kind` |
|---|---|---|---|
| mcp | `mcp-server` | `protocolRevision`, `mode` (`stateless` for 2026-07-28, `stateful` for 2025-11-25; always in `derived`), `transports[]` (from remotes `type` and packages `transport.type`) | `tool`, `resource`, `resource-template`, `prompt` |
| a2a | `a2a-agent` | `protocolBindings[]` | `skill` (native name = skill `id`, title = skill `name`) |
| api | `openapi-api` | `openapiVersion` | `operation` (native name = `operationId`; when absent, `"<METHOD> <path>"`, marked derived), `webhook` (3.1) |
| event | `asyncapi-application` | `asyncapiVersion` | `channel` (channel key), `operation` (operation key) |
| skill | `agent-skill` | none | none (the skill's description is the projection) |
| model | `model` | `inputModalities[]`, `outputModalities[]`, `contextWindowTokens?`, `maxOutputTokens?` | none |

`title` falls back to `nativeName` and is then marked derived. Extension members, `_meta`, descriptions of
entries, schemas, examples, URLs and every other native field are **never** projected. The projection is
built from the tree's typed values. It never re-serializes into, or mutates, the native bytes.
`ProjectionBoundaryTests` proves User Story 3 for every family:
- the native document is byte-identical to the input;
- changing an extension field or the key order changes the digest and not the projection;
- projection member names are a subset of the projection v1 schema.

## R13 — No storage, no audit, no migration (FR-017, FR-023, FR-027)

**Decision**:
- This slice adds no table, column or migration, so every GOZ-0005 migration pin stays as it is.
- Validation writes no audit event, and the range 1501–1510 stays free. Authorization denials keep the existing `OrganizationAccess` audit.
- The request body is held only for the request. It is never logged; ASP.NET request logging stays off for this route, as it is everywhere.
- `ManifestNoStorageTests` compares a schema and row-count snapshot of every `gozleme` table before and after a batch of validations (SC-007).
- The publish endpoint and the unscoped catalogue pins stay at `404` (FR-021).

## R14 — Seeded demo capability (FR-025)

**Decision**: `seed` gains one idempotent step after the organizations:
- alice (admin) registers publisher `seeded-demo` ("Seeded Demo Publisher") with the fixed ID `0193a000-0000-7000-8000-000000000010`;
- carol (`publisher`) creates capability `manifest-demo` ("Manifest Demo") with the fixed ID `0193a000-0000-7000-8000-000000000011`.

Each actor works through its own signed tenant context, so the step has no right that its actor lacks.
- **If the rows exist** (same IDs), nothing changes: names and handles are immutable.
- **If the handle is held by another organization** (for example after an operator reclaim on a long-lived volume), seed fails with a clear message rather than inventing a new handle.

The fixed IDs are UUIDv7-shaped constants that cannot collide with generated ones, because their timestamp is in 2024.

## R15 — Samples (FR-018, SC-001)

**Decision**:
- **Location**: `web/src/samples/manifests/` is the single source. `index.json` lists `{id, family, title, file, expected: {outcome, category?, rule?, location?}}`, and each file is a complete envelope.
- **Target placeholder**: the envelope `capability` is the literal placeholder `{{capability}}`. The UI and tests replace it with the target's canonical ID before the user sees the text or submits it. Samples are then valid for any target, and the identity rule is still exercised: an invalid sample hard-codes a foreign canonical ID.
- **Coverage**: one valid sample per family, plus at least one invalid sample per category and per family. Two of the MCP samples are valid, one stateless (`2026-07-28`) and one stateful (`2025-11-25`).
- **Who reads them**:
  - The integration test project links the directory (`<None Include="../../web/src/samples/manifests/**" CopyToOutputDirectory="PreserveNewest" />`), and `ManifestSampleTests` iterates `index.json`.
  - The web bundles the files at build time.
  - The e2e suite reads the same index.
- **Synthetic content**: secret-like values are obviously synthetic (for example `ghp_` + `EXAMPLE` repeated, or a PEM block whose body is `R0FMTEVNRS1TWU5USEVUSUMtS0VZ`). They still match pattern list v1, and the no-secret scan looks for exactly these values.

## R16 — Telemetry (FR-022)

**Decision**:
- **Span**: `gozleme.manifest.validate`. Attributes `gozleme.manifest.family` (one of the six or `unknown`), `gozleme.manifest.outcome` (`accepted`, `rejected`, `forbidden`, `not_found`, `suspended`, `rate_limited`, `too_large`, `unsupported_media_type`) and `gozleme.manifest.problem_count` (an integer). The opaque organization, publisher and capability IDs are set only after membership is confirmed.
- **Counters**:
  - `gozleme.manifest.validations` {`gozleme.manifest.family`, `gozleme.manifest.outcome`};
  - `gozleme.manifest.problems` {`gozleme.manifest.family`, `gozleme.manifest.category`}, incremented once per distinct category in a rejected result.

  Every label comes from a closed set.
- **Never recorded**: pointers, rules with input, names, documents, projections or digests. The digest is not a secret, but it identifies tenant content, so it stays out.
- **Browser**: span `gozleme.ui.manifest_validate`.
- **Tests**: `ManifestTelemetryTests` asserts the span and counters, and asserts that no sample's distinctive strings, synthetic secrets included, appear in captured spans or logs.

## R17 — Dependencies and licences

**Decision**:
- **New packages** in `Directory.Packages.props`: `Corvus.Text.Json.Validator` 5.8.11 (Apache-2.0; P1 revised) and `YamlDotNet` 18.1.0 (MIT). Transitive packages: `Corvus.Text.Json` 5.8.11, `Corvus.Regex.Ecma262` 1.0.15 and NodaTime 3.3.1 (Apache-2.0), about 2.6 MB in total, with no Roslyn. No extra transitive pins are needed.
- **Vulnerability check**: run `dotnet list package --vulnerable --include-transitive` at implementation time. GitHub advisories show only CVE-2018-1000210 for YamlDotNet ≤ 4.3.2, which is not applicable.
- **Bundled upstream schemas**: OpenAPI and AsyncAPI are Apache-2.0, the MCP specification is MIT, and A2A is Apache-2.0. Their notices go in `src/Gozleme.Api/Manifests/Schemas/SOURCES.md` and in the repository's third-party notices.
- **No web dependency**: the UI is plain React.

**Alternatives rejected**: see R2 and R3.
