# Feature Specification: Typed Manifests for Capability Families

**Feature Branch**: `feature/goz-0006-typed-manifests`

**Created**: 2026-11-17

**Status**: Draft (specify done 2026-11-17)

**Input**: User description: "GOZ-0006 — Typed manifests for capability families. User-visible outcome: validate sample MCP, A2A, model, skill, API and event manifests. In scope: versioned typed envelope and protocol-specific adapters; reject unknown unsafe claims. Important boundary: keep protocol-native documents and normalized projections distinct." (full prompt: `docs/prompts/spec-0006.md`, revision 7, including the convention sections GOZ-0000-IF-11, GOZ-0001-IF-01/02/09/11/17, GOZ-0002-IF-01/05/06/07/08, GOZ-0003-IF-01/02/07/08/09/10/19, GOZ-0004-IF-01/02/04/06/07/08/09/13 and GOZ-0005-IF-01/03/06/07/09/10)

## Clarifications

### Session 2026-11-17

Maintainer decisions taken before drafting (2026-11-17, recorded in `docs/specs/006-typed-manifests/progress.yaml`):

- Q1: Does this slice store manifests, or only validate them? → A: Validate only. A manifest is validated against a registered capability, which lets the system check the manifest's claimed identity. The result is the typed envelope, the native document exactly as submitted, and a separate normalized projection. Nothing is stored. Storage arrives as immutable revisions in GOZ-0007, so there is no mutable draft for it to reconcile.
- Q2: Who may validate a manifest? → A: A new action, held by the `publisher` role only (the same as creating a capability). A suspended publisher's capabilities cannot be validated.
- Q3: Which native formats does each family accept? → A: Each family accepts one pinned format, and a later format version is added as a new adapter version:
  - MCP: the MCP Registry server description (`server.json`);
  - A2A: the A2A Agent Card (current stable version);
  - API: OpenAPI 3.1, plus 3.0;
  - Event: AsyncAPI 3.0;
  - Skill: the Agent Skills `SKILL.md` front matter;
  - Model: a Gözleme-defined typed model card, because no single standard exists.

  The plan pins the exact versions after checking the current published specifications.
- Q4: How strictly are unknown or unsafe claims rejected? → A: Strictly, and by named category.
  - The envelope rejects any unknown field.
  - Reserved security claims are rejected with a named category. These are signatures, attestations, trust, verified or approved status, delegation and transfer chains, visibility and grants, and owner or publisher overrides.
  - Inline secret values are rejected; secret references are allowed.
  - Native documents keep unknown fields only through the protocol's own extension mechanism. Such fields are preserved verbatim but never projected.

Clarify session (2026-11-17):

- Q: How is the native document carried inside the request? → A: Always as a string inside the envelope, for every family and format. Its bytes are that string encoded as UTF-8, and the SHA-256 digest covers exactly those bytes. Option A.
- Q: Are OpenAPI and AsyncAPI native documents accepted in YAML as well as JSON? → A: Yes. OpenAPI and AsyncAPI accept JSON or restricted YAML (FR-016). `server.json`, the A2A Agent Card and the model card are JSON only. The envelope declares the native encoding, and a mismatch is `malformed`. Option A.
- Q: How does Gözleme decide that a value outside a secret-bearing field is an inline secret? → A: Two deterministic rules, documented as a contract. (1) Any non-empty value in a secret-bearing field is rejected. (2) A fixed pattern list of known credential formats is rejected in any field. There is no randomness (entropy) heuristic. Option A. (The maintainer first chose option B, which added an entropy heuristic, then reversed it the same session.)
- Q: How is a problem's location written? → A: A part (`envelope` or `native`) plus an RFC 6901 JSON Pointer path. Parse failures (`malformed`) give a line and column in the native string instead. `too_large` has no location. Option A.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Validate a sample manifest for every capability family (Priority: P1)

A member who holds `publisher` opens one of their organization's capabilities and chooses **Validate manifest**. For each of the six families (MCP, A2A, model, skill, API and event), they load the bundled valid sample, or paste their own manifest, and validate it. An accepted manifest shows three clearly separated parts:

- the **typed envelope** (envelope version, family, the capability it claims, the declared version, and the native format and version);
- the **native document**, exactly as submitted, with its content digest;
- the **normalized projection** that Gözleme derived from it, labelled with its own projection version.

**Why this priority**: This is the stated user-visible outcome. GOZ-0007 (revisions), GOZ-0008 (child identities and projections) and search all rely on manifests being typed and validated the same way for every family.

**Independent Test**: Sign in as the seeded publisher member and validate the valid sample of each family against a seeded capability. Each one is accepted, and its envelope, native document and projection are shown separately. The same requests through the API return the same results.

**Acceptance Scenarios**:

1. **Given** a member holding `publisher` and a capability of an active publisher in their organization, **When** they validate the valid sample manifest of any of the six families, **Then** it is accepted, and the result shows the envelope, the verbatim native document with its digest, and the normalized projection, each labelled with its own version.
2. **Given** an accepted manifest, **When** its projection is shown, **Then** the projection lists the family's entries: MCP tools, resources and prompts; A2A skills; API operations; event channels and operations; the skill's description; and the model's declared modalities and limits. Entries are listed by their native names only. No child identity is assigned, because that is GOZ-0008.
3. **Given** the same manifest validated twice, **When** both results are compared, **Then** the envelope, the native digest and the projection are identical (validation is deterministic).
4. **Given** any validation, **When** it completes, **Then** nothing is stored: there is no manifest, revision or projection record. Reloading the page shows the capability unchanged.

---

### User Story 2 - Unknown and unsafe claims are rejected with a clear reason (Priority: P1)

The publisher member pastes manifests that claim things Gözleme cannot accept in this release. Examples are a signature, a "verified" or "approved" flag, a delegation chain, a visibility grant, a different owning publisher, an envelope field nobody defined, an inline API key, a remote reference that would need fetching, or a native field outside the protocol's extension mechanism. Each manifest is rejected. The result lists every problem by category and location, and never repeats the offending value.

**Why this priority**: "Reject unknown unsafe claims" is in scope, and the v1 security profile requires unsupported trust claims to be rejected rather than trusted (CR-01, constitution principle III). Secret values must never enter manifests (invariant 8).

**Independent Test**: Validate each bundled invalid sample (at least one per rejection category, and at least one per family), and confirm each is rejected with the expected category and location. Confirm that neither the response, the page, the logs nor the telemetry contains the rejected value.

**Acceptance Scenarios**:

1. **Given** an envelope with a field the envelope version does not define, **When** it is validated, **Then** it is rejected with `unknown_field` and the field's location.
2. **Given** an envelope or native document that asserts a reserved security claim (a signature, attestation, trust or verification status, approval, a delegation or transfer chain, visibility or grants, or an owner or publisher override), **When** it is validated, **Then** it is rejected with `reserved_claim` and the claim's location, even if the native protocol defines that field.
3. **Given** a manifest that contains an inline secret value (a credential, token, password or private key, in a field that carries one or in a recognizable credential form), **When** it is validated, **Then** it is rejected with `secret_value` and the location. The value never appears in the response, page, logs or telemetry. **Given** the same field holding a secret reference instead of a value, **Then** it is accepted.
4. **Given** a manifest that claims a capability or publisher other than the target capability (another canonical ID, another alias, another organization's capability), **When** it is validated, **Then** it is rejected with `identity_mismatch`, and the response does not reveal whether the claimed capability exists.
5. **Given** a native document with a field outside its pinned schema, **When** the field is not under the protocol's own extension mechanism, **Then** it is rejected with `unknown_field`. **When** it is under that mechanism, **Then** it is accepted, preserved verbatim in the native document, and absent from the projection.
6. **Given** a native document that refers to an external resource by address (for example a remote schema reference), **When** it is validated, **Then** it is rejected with `external_reference`, and no network request is made.
7. **Given** an unknown envelope version, an unknown family, or a native format or version that does not belong to the declared family, **When** it is validated, **Then** it is rejected with `unsupported_version`, `unsupported_family` or `unsupported_format` respectively.
8. **Given** a manifest that is malformed (unparseable, duplicate keys, unsafe YAML features such as aliases or custom tags), too large or too deeply nested, **When** it is validated, **Then** it is rejected with `malformed` or `too_large` before any family-specific processing.

---

### User Story 3 - Native documents and normalized projections stay distinct (Priority: P1)

A reviewer of this feature checks the stated boundary. The native document is exactly what the publisher submitted: Gözleme never rewrites, reorders or fills in defaults in it. The projection is a separate, versioned, Gözleme-defined summary derived from the native document, and nothing flows back from it into the native document.

**Why this priority**: This is the spec's important boundary, and the architecture requires separately versioned normalized projections (constitution principle III). GOZ-0007 will sign native material, and GOZ-0008 will version projections independently, so mixing them here would corrupt both.

**Independent Test**: For each family, validate a manifest and confirm three things. First, the returned native document is byte-for-byte identical to the input, with a matching digest. Second, changing only an extension field changes the native digest but not the projection. Third, the projection contains only fields defined by its projection version.

**Acceptance Scenarios**:

1. **Given** an accepted manifest, **When** the native document is returned, **Then** it is byte-for-byte identical to the submitted native document, and its digest is computed over exactly those bytes.
2. **Given** two manifests that differ only in an extension field or in key order, **When** both are validated, **Then** their native digests differ (or match for identical bytes only) while their projections are identical.
3. **Given** an accepted manifest, **When** its projection is inspected, **Then** it carries its own projection version, separate from the envelope version and the native format version, and contains no native field outside the projection version's defined set.
4. **Given** a projection, **When** it would differ from what the native document states (for example a defaulted value), **Then** the native document is still shown unchanged, and the projection marks the value as derived.

---

### User Story 4 - Only publisher members of the owning organization can validate (Priority: P1)

An ordinary member sees the capability but is not offered **Validate manifest**, and a direct request is refused as forbidden. An admin without `publisher` is refused in the same way. A non-member, or someone using another organization's capability ID, gets the same "not found" as for an unknown organization. A capability of a suspended publisher cannot be validated.

**Why this priority**: The permission contract and the decision point are mandatory for every new organization-scoped action (GOZ-0004-IF-01), and the maintainer chose `publisher` only (Q2).

**Independent Test**: For each built-in role, attempt validation as a member holding exactly that role, and compare the result with the published contract table. Repeat as a non-member and with a cross-organization capability ID. Validate against a suspended publisher's capability.

**Acceptance Scenarios**:

1. **Given** a member without `publisher` (including an admin), **When** they request validation, **Then** they get "forbidden", and the manifest is not parsed.
2. **Given** a non-member, an unknown or malformed organization or capability ID, or a capability that belongs to another organization, **When** validation is requested, **Then** the answer is the identical empty "not found".
3. **Given** a capability whose publisher is suspended, **When** a `publisher` member requests validation, **Then** the answer is "suspended" (conflict), and the manifest is not parsed.
4. **Given** an account that repeatedly requests validation, **When** it exceeds its validation budget, **Then** further requests are answered "rate limited". The rejection is recorded under its own operation, never as a sign-in failure.

---

### Edge Cases

- **Empty or whitespace-only input**: rejected with `malformed`.
- **Wrong encoding**: a native document that does not parse as its declared encoding is rejected with `malformed`. So is YAML declared for a JSON-only format (`server.json`, A2A Agent Card, model card), or a `SKILL.md` without front matter. OpenAPI and AsyncAPI accept either JSON or restricted YAML.
- **Several problems at once**: all problems found are reported in a stable order, up to a fixed cap (for example 50), and the response states that more problems exist. Parsing failures (`malformed`, `too_large`) stop processing before other checks.
- **Very large documents and deep nesting**: rejected with `too_large` at a fixed limit, before family parsing. The limit is documented.
- **Unicode tricks**: invalid UTF-8, a byte-order mark, and control characters in names are handled deterministically: invalid UTF-8 is `malformed`, and a byte-order mark is preserved in the native bytes.
- **Native names**: a protocol-native identifier (such as an MCP server name or an A2A agent name) does not need to equal the Gözleme capability name or alias. It is projected as the native name and is never treated as a Gözleme identity claim. Only envelope identity claims are checked.
- **Secret references**: a secret reference names a provider and a key, and is accepted. A placeholder that is clearly not a credential (for example an environment variable name with no value) is accepted.
- **Ambiguous credential detection**: in a field that is not secret-bearing, a value is rejected with `secret_value` only if it matches the documented credential pattern list. Random-looking values (UUIDs, digests, opaque IDs) are not flagged by randomness alone. The pattern list is a contract, applied deterministically. A false positive is fixed by using a secret reference.
- **Same manifest, different capabilities**: the identity claim matches one capability only. Every other target answers `identity_mismatch`.
- **Organization role change mid-session**: the role is re-read on every request, so losing `publisher` takes effect on the next validation.
- **Database outage**: validation still needs the membership, role and capability check, so it fails closed with "unavailable".
- **Remote references**: OpenAPI and AsyncAPI internal references (within the same document) are allowed. Any reference to another document or address is rejected, and nothing is fetched.

## Requirements *(mandatory)*

### Functional Requirements

**Typed envelope**

- **FR-001**: Gözleme MUST define a versioned, typed manifest envelope (envelope version 1 in this slice) that contains exactly:
  - the envelope version;
  - the capability family;
  - the claimed capability (canonical ID or friendly alias);
  - the declared capability version;
  - the native format and its version;
  - the native encoding (`json` or `yaml` for OpenAPI and AsyncAPI; `json` for `server.json`, the A2A Agent Card and the model card; `markdown` for `SKILL.md`);
  - the native document, always as a string (for every family and format); its native bytes are that string encoded as UTF-8;
  - optional publisher-provided descriptive fields that the envelope version defines.

  The envelope schema is a published, versioned contract. A breaking change needs a new envelope version.
- **FR-002**: The family MUST be one of exactly `mcp`, `a2a`, `model`, `skill`, `api`, `event`. Any other value is `unsupported_family`.
- **FR-003**: The declared capability version MUST be a valid Semantic Versioning 2.0.0 string. This slice validates its syntax only; ordering and uniqueness belong to GOZ-0007.
- **FR-004**: Each family MUST accept exactly the native formats pinned in its adapter (Q3), each pinned to an exact published specification version. A format or version that does not belong to the declared family is `unsupported_format`. An unknown envelope version is `unsupported_version`.

**Protocol adapters**

- **FR-005**: Each family MUST have an adapter that validates the native document against its pinned schema and derives a normalized projection. An adapter has its own version. Supporting a new native version adds a new adapter version and never changes an existing one.
- **FR-006**: Adapters MUST perform no network, file system or process I/O and execute no content. External or cross-document references MUST be rejected with `external_reference`, never fetched.
- **FR-007**: Native fields outside the pinned schema MUST be rejected with `unknown_field`, unless they are under the protocol's own extension mechanism. Extension fields MUST be preserved verbatim in the native document and MUST NOT appear in the projection. Each adapter documents the extension mechanism it recognizes.

**Unsafe claims and secrets**

- **FR-008**: The envelope MUST reject every field that its version does not define, with `unknown_field`.
- **FR-009**: Reserved security claims MUST be rejected with `reserved_claim`, wherever they appear in the envelope or the native document. Each adapter documents the native fields that map to these claims. The claims are:
  - signatures and attestations;
  - trust, verification or approval status;
  - delegation and transfer chains;
  - visibility, sharing or grants;
  - ownership or publisher overrides;
  - lifecycle state (for example deprecated or revoked status asserted by the publisher).
- **FR-010**: Inline secret values MUST be rejected with `secret_value`. Detection uses two deterministic rules, with no randomness (entropy) heuristic:
  1. any non-empty value in a secret-bearing field (credential, token, password, private key or authorization header value);
  2. any value in any field that matches a fixed, documented pattern list of known credential formats (for example private-key PEM blocks, `Bearer`/`Basic` header values, well-known provider key prefixes, JWTs, and URLs with embedded `user:password@`).

  The pattern list is a versioned contract. Secret references (a provider plus a key name) and declarations without values MUST be accepted.
- **FR-011**: The envelope's claimed capability MUST identify the target capability exactly. Another canonical ID, another alias, a foreign origin, or a capability of another publisher or organization is `identity_mismatch`. The answer MUST NOT reveal whether the claimed capability exists anywhere. A native document's own names are projected as native names and are never identity claims (GOZ-0005-IF-01).

**Results**

- **FR-012**: A rejected manifest MUST answer with a list of problems. Each problem has a category from a closed, documented set (`malformed`, `too_large`, `unsupported_version`, `unsupported_family`, `unsupported_format`, `unknown_field`, `reserved_claim`, `secret_value`, `identity_mismatch`, `external_reference`, `invalid_value`), a location, and a fixed message. The location is a part (`envelope` or `native`) plus an RFC 6901 JSON Pointer into that part's parsed structure, which works the same for JSON, YAML and `SKILL.md` front matter. A parse failure (`malformed`) instead gives a line and column in the native string, and `too_large` has no location. Results MUST NOT echo submitted values. Problems appear in a stable order up to a documented cap, with an indicator when more exist. Adding a category is a contract change.
- **FR-013**: An accepted manifest MUST answer with three distinct parts:
  - the envelope, without the native document;
  - the native document, byte-for-byte as submitted, with a SHA-256 digest of exactly those bytes;
  - the normalized projection.
- **FR-014**: The normalized projection MUST carry its own projection version, separate from the envelope and native versions. It contains only fields that the projection version defines for the family: family, kind, native name, title, description, declared version and the list of the family's entries by native name. It MUST mark values that the adapter derived or defaulted, rather than read directly. It MUST NOT assign child identities (GOZ-0008), and it MUST NOT write anything back into the native document.
- **FR-015**: Validation MUST be deterministic: the same input against the same capability yields an identical result.
- **FR-016**: Inputs MUST be bounded. There is a documented maximum size for the whole submission and for the native document, a maximum nesting depth, and a cap on entries per family. JSON with duplicate keys, invalid UTF-8, and YAML with aliases, anchors, merge keys or custom tags are `malformed`. Exceeding a bound is `too_large`.
- **FR-017**: Validation MUST store nothing: no manifest, native document, projection or result is persisted in the database, logs, telemetry, audit records or files.

**Samples**

- **FR-018**: The product MUST bundle synthetic samples. These are one valid sample per family, and invalid samples that cover every rejection category with at least one invalid sample per family. A member can load them in the validation view. Samples contain no real organizations, people, endpoints or credentials, and secret-like values in invalid samples are obviously synthetic.

**Authorization and tenant isolation**

- **FR-019**: Validation MUST be a new organization-scoped action, `manifest.validate` on the existing `capability` resource, held by `publisher` only (Q2). It is added to the living permission contract with its role-table row and endpoint row. The endpoint MUST be mapped under the organization's capability path and pass the existing decision point in this order: membership (identical empty "not found"), then permission ("forbidden"), then capability lookup ("not found"), then publisher suspension ("suspended" conflict). The manifest is parsed only after all four checks pass.
- **FR-020**: Validation MUST be rate-limited per account, with its own named operation for rejections.
- **FR-021**: The existing boundary pins MUST stay. The publish endpoint and the unscoped capability catalogue still answer "not found", and no manifest, revision or projection table exists. Pins are narrowed only where this slice legitimately adds a surface.

**Observability**

- **FR-022**: Each validation MUST produce a span with the family and the outcome, and a counter labelled with family, outcome and rejection category, all bounded values. Manifest bodies, native documents, projections, field values, capability names, aliases and native names MUST NOT appear in span names, attributes, events, metric labels or logs. The capability is referred to by opaque ID only, after membership is confirmed.
- **FR-023**: Validation changes nothing, so it writes no new security audit event; authorization denials keep the existing audit event. Audit range 1501–1510 stays free for the next module that changes state.

**Product, demo and compatibility**

- **FR-024**: The capability page MUST offer a **Validate manifest** view, shown only to members whose permissions include `manifest.validate`. It lets the member choose a family, load a sample or paste a manifest, and see either the three result parts in clearly separate panels or the problem list. It handles "forbidden", "suspended", "rate limited", "unavailable" and "not found" with clear messages. The page MUST require a signed-in session, and the browser-facing route MUST keep the same-origin check.
- **FR-025**: The seed command MUST provide a fixed demo capability under a seeded active publisher in Seeded Demo Org, held for carol (`publisher`). It MUST stay idempotent and act as the seeded admin or publisher.
- **FR-026**: The cumulative demo MUST include a `goz-0006` suite that covers:
  - User Story 1 in the browser for all six families, with screenshots;
  - at least one rejection per category;
  - the native-versus-projection boundary test (User Story 3);
  - the role matrix for the new action, non-member and cross-organization "not found", and the suspended publisher;
  - a no-secret scan of captured telemetry, including the synthetic secret values from the invalid samples;
  - regression of all earlier suites.
- **FR-027**: This slice MUST add no database table, column or migration, unless the plan proves one necessary. In that case it follows the GOZ-0003/GOZ-0004/GOZ-0005 rules and updates their pins.

### Key Entities

- **Manifest envelope**: The Gözleme-defined typed wrapper, at envelope version 1, with family, claimed capability, declared version, native format and version, and native document. It is a portable contract.
- **Capability family**: One of `mcp`, `a2a`, `model`, `skill`, `api`, `event`. It decides which adapter and native formats apply.
- **Native document**: The protocol's own document (`server.json`, Agent Card, OpenAPI, AsyncAPI, `SKILL.md`, model card). It is kept byte-for-byte as submitted and identified by a SHA-256 digest.
- **Protocol adapter**: A versioned, I/O-free validator and projector for one family's pinned native format.
- **Normalized projection**: A Gözleme-defined, separately versioned summary derived from the native document. It contains family entries by native name and marks derived values.
- **Validation problem**: A category, a location and a fixed message, and never the value.
- **Sample manifest**: A bundled, synthetic, valid or deliberately invalid manifest for a family.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The valid sample of each of the six families is accepted, and each bundled invalid sample is rejected with exactly its expected category, 100% of the time.
- **SC-002**: In the demo, a publisher member validates a sample of every family in the browser in under three minutes of total interaction.
- **SC-003**: For 100% of accepted manifests, the returned native document is byte-identical to the input, and changing only an extension field or the key order leaves the projection unchanged.
- **SC-004**: Zero submitted values from rejected fields, secret samples, native documents or projections appear in captured telemetry, logs, screenshots of the problem list, or error responses.
- **SC-005**: 100% of (built-in role × new action) combinations behave as the published contract table states, verified automatically. Non-members and cross-organization IDs get an identical "not found".
- **SC-006**: Validation results arrive in under two seconds for a manifest at the documented size limit.
- **SC-007**: Zero manifest, projection or result records exist after the demo; the database schema is unchanged.
- **SC-008**: All earlier cumulative demo suites still pass.

## Assumptions

- Validation is synchronous and stateless (Q1). The "declared capability version" exists so that GOZ-0007 can reuse the envelope unchanged when it stores revisions.
- The envelope and native document are submitted together as one JSON object. The native document is always carried as a string, whatever its format (JSON, YAML or `SKILL.md`), and its bytes are those of the string in UTF-8 (clarified 2026-11-17). A native document sent as a nested JSON value instead of a string is `malformed`.
- Projections list entries (tools, skills, operations, channels) by native name only. Child capability identities and projection generations belong to GOZ-0008, and search indexing to GOZ-0013.
- Signatures, attestations and digests that make a revision immutable belong to GOZ-0007. The SHA-256 digest here identifies the submitted bytes only and makes no authenticity claim.
- The model card schema is Gözleme-defined for this slice (Q3) and documented as a versioned contract.
- Rate limits are in memory and per API instance, like the existing session-scoped limiters (GOZ-0002-IF-08). Validation changes no shared state, so a shared budget is not required.
- No dependency on an external registry, schema host or network service. All pinned schemas are bundled.
- Row-level security remains defence in depth beside application authorization. This slice reads only the existing capability and publisher rows.
