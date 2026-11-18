# Feature Specification: Immutable Revisions and Signed Attestations

**Feature Branch**: `feature/goz-0007-immutable-revisions`

**Created**: 2026-11-18

**Status**: Draft (specify 2026-11-18)

**Input**: User description: "GOZ-0007 — Immutable revisions and signed attestations. User-visible outcome: publish two revisions and reject mutation of prior signed revision. In scope: immutable digest material, initial signed attestations and historical provenance. Important boundary: do not advertise unimplemented delegation chains." (full prompt: `docs/prompts/spec-0007.md`, revision 8, including the convention sections GOZ-0000-IF-01/04/11/13, GOZ-0001-IF-01/02/09/10/11/17/20, GOZ-0002-IF-01/05/06/07/08/09, GOZ-0003-IF-01/02/07/08/09/10/19, GOZ-0004-IF-01/02/04/06/07/08/09/13, GOZ-0005-IF-01/03/06/07/08/09/10/13 and GOZ-0006-IF-01/02/03/04/05/06/08/13/14)

## Clarifications

### Session 2026-11-18

Maintainer decisions taken before drafting (2026-11-18, recorded in `docs/specs/007-immutable-revisions/progress.yaml`):

- Q1: Who holds the private key that signs a revision's attestation? → A: **Registry-held, one key per publisher.** Gözleme creates a signing key for each publisher and signs on the publisher's behalf when an authorized publisher member publishes. Private keys are encrypted under a key-encryption key supplied through a Secret reference, never stored in clear in the database and never protected only by the database or the cookie Data Protection key ring (GOZ-0001-IF-20). Stated limit: an attestation proves that registry *origin* accepted the revision from publisher *P* through an authorized member. It does not prove that the publisher held the key, and an operator with the key-encryption key could forge one. Later federation key pins (GOZ-0029) pin these per-publisher public keys.
- Q2: What does a revision's attestation cover? → A: **The native digest and a projection digest.** The signed statement names the canonical capability and publisher IDs, the registry origin, the declared version, the native SHA-256 digest and byte length, the adapter ID and version, the projection version, and a SHA-256 digest of the projection's canonical, byte-deterministic serialization. A later adapter version therefore can never silently reinterpret a signed revision.
- Q3: What happens to existing revisions when their publisher is suspended (no held claim)? → A: **Readable, verifiable and frozen.** Existing revisions and attestations stay readable by members and still verify. They are shown as belonging to a suspended publisher. New revisions are refused with "suspended" in the API and in the database write policy.
- Q4: Which permissions does the new `manifest` resource have? → A: A new `manifest` resource with `manifest.publish` (held by `publisher` only, with the held-claim check in the write policy) and member-level `manifest.list` and `manifest.read`. `manifest.validate` moves from the `capability` resource onto the `manifest` resource. Its role (`publisher` only) and behaviour are unchanged; only the permission contract row changes.
- Q5: How is a lost or leaked key-encryption key handled, given that publisher signing keys could not be replaced? (raised after drafting) → A: **Add-only key succession.** One operator command, run like `migrate` and with no API route, adds a new signing key for a publisher and retires its current key, recording the time and a reason (`lost` or `compromised`). Old keys are never changed or deleted and still verify the revisions they signed; new revisions are signed with the new key. This also recovers from a lost key-encryption key. API and UI rotation, revocation of earlier signatures, and cross-registry key replacement stay later work (GOZ-0033, GOZ-0039), and federation peers must not trust a key succession without their own check (GOZ-0029, CR-05).

Clarification questions (`/speckit-clarify`, 2026-11-18):

- Q: If an existing version is republished with byte-for-byte identical content (for example a retry after a lost response), is it a conflict or a success? → A: **Always a conflict.** It is refused with `409 version_exists` whatever the content, with no idempotent success and no idempotency key. Clients recover from a lost response by listing revisions.
- Q: Can a capability's versions be published in any order, or must each new version be higher than all earlier ones? → A: **Any order.** Versions need only be unique by SemVer 2.0.0 precedence (so backport releases such as `1.2.5` after `2.0.0` are allowed). Lists are ordered by publication sequence, and this slice designates no "latest" revision (GOZ-0026, GOZ-0027).
- Q: When is a publisher's signing key created: at first publication, at publisher registration, or only by an operator command? → A: **Lazily, at first publication**, inside that publication's transaction. Publisher registration (GOZ-0005) is unchanged and never needs the key-encryption key. A publisher that has never published has no key, and a key never exists without a revision it signed.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Publish two revisions of a capability (Priority: P1)

A member who holds `publisher` opens one of their organization's capabilities, validates a manifest as in GOZ-0006, and chooses **Publish revision**. Gözleme stores the revision immutably and signs an attestation for it. The member then publishes a second manifest with a new version. The capability's **Revisions** list shows both revisions in publication order. Each one shows its version, sequence number, publication time, the opaque ID of the member who published it, its native digest and its verification status.

**Why this priority**: This is the stated user-visible outcome. Later specs build on stored, signed revisions: child identities (GOZ-0008), artifacts (GOZ-0009), discovery and search (GOZ-0012, GOZ-0013), approvals (GOZ-0021) and federation import (GOZ-0030).

**Independent Test**: Sign in as the seeded publisher member and publish two valid manifests with different versions to a seeded capability. Confirm that both revisions appear in order, that each has an attestation that verifies, and that the API returns the same records.

**Acceptance Scenarios**:

1. **Given** a member holding `publisher` and a capability of an active publisher in their organization, **When** they publish a manifest that GOZ-0006 validation accepts, **Then** a revision is created with the envelope summary, the native document stored byte-for-byte with its digest and byte length, the projection, the adapter ID and version, and the projection version, and a signed attestation is created for it.
2. **Given** a capability with one revision, **When** the member publishes a manifest with a different version, **Then** a second revision is created with the next sequence number, and the first revision is unchanged.
3. **Given** a capability with revisions, **When** any member of the organization opens **Revisions**, **Then** they see every revision in publication order with its version, sequence, publication time, publishing account (opaque ID), native digest and verification status.
4. **Given** a manifest that validation rejects, **When** publication is attempted, **Then** nothing is stored, and the answer is the same as GOZ-0006 validation gives for that manifest (the rejected result body with "malformed", "too large" or "unprocessable").
5. **Given** a publisher that has never signed before, **When** its first revision is published, **Then** its signing key is created in the same operation, and its public key and key ID are readable by members afterwards.

---

### User Story 2 - A published signed revision can never be changed (Priority: P1)

The publisher member tries to change a revision that is already signed. They publish a different manifest under an existing version, try to edit or delete a revision through the API, and an operator with direct database access tries to update or delete a revision or its attestation. Every attempt fails. If stored bytes are changed out of band, for example by a superuser who bypasses the protections, verification reports the revision as failed and never as verified.

**Why this priority**: "Reject mutation of prior signed revision" is the stated outcome, and security invariant 6 and constitution principle III require immutable signed publications.

**Independent Test**: After publishing two revisions, republish version 1 with different content, send update and delete requests for a revision, and run `UPDATE`, `DELETE` and `TRUNCATE` against the revision and attestation tables as the application login and as the owner login. Confirm that every attempt is refused and that both revisions still verify. Then, as a superuser with protections bypassed, alter one stored byte and confirm that verification fails.

**Acceptance Scenarios**:

1. **Given** a capability with a revision at version V, **When** a manifest with version V is published again (with different or identical content, and including any version equal to V by Semantic Versioning precedence, such as one that differs only in build metadata), **Then** it is refused with a "version exists" conflict, and nothing is stored or changed.
2. **Given** any revision, **When** a client asks to update, replace or delete it or its attestation, **Then** no such operation exists (the route answers "not found" or "method not allowed"), and the revision is unchanged.
3. **Given** the database, **When** the application login or the owner login runs `UPDATE`, `DELETE` or `TRUNCATE` on a revision or attestation, **Then** the database refuses it.
4. **Given** a revision whose stored native bytes, projection or attestation were altered out of band, **When** it is verified, **Then** the result is "failed" with a fixed reason (digest mismatch or signature invalid), and the UI never shows it as verified.
5. **Given** two concurrent publications of the same version to one capability, **When** both complete, **Then** exactly one revision exists, and the other request gets "version exists".

---

### User Story 3 - Verify attestations and see historical provenance (Priority: P1)

Any member opens a revision. They see its attestation: the signed statement, what it covers, the signing key ID, the publisher's public key, and Gözleme's verification result. The verification result is computed again from the stored bytes each time, not read from a stored flag. They can download the statement, its signature and the public key, and verify the signature independently of Gözleme. Provenance stays attributed to the original permanent identities. After the capability is renamed, or the publisher's handle is reclaimed by an operator, the revision and its attestation still name the same canonical capability and publisher IDs and still verify.

**Why this priority**: "Initial signed attestations and historical provenance" are in scope. Constitution principle III requires that original signatures and provenance survive legitimate ownership changes. GOZ-0005-IF-01 requires attestation subjects to use canonical IDs and never the alias or handle.

**Independent Test**: Verify a revision in the UI and the API. Verify the downloaded statement and signature with an independent standard implementation of the signature algorithm. Rename the capability and reclaim the publisher's handle with the operator command, then confirm that the earlier revisions still name the same canonical IDs and still verify.

**Acceptance Scenarios**:

1. **Given** a revision, **When** a member views its attestation, **Then** they see the statement fields (Q2), the key ID, the public key, the algorithm, the statement version and the verification result, with a plain statement of what it proves: "Signed by registry *origin* for publisher *P*". The UI never presents it as a key held by the publisher.
2. **Given** a downloaded statement, signature and public key, **When** they are checked with an independent standard implementation of the algorithm, **Then** the signature verifies, and the digests in the statement match the downloaded native document and the projection's canonical serialization.
3. **Given** revisions of a capability that is later renamed, **When** they are viewed, **Then** they still name the canonical capability ID and verify, and the attestation contains no display name, alias or handle.
4. **Given** a publisher whose handle is reclaimed by an operator (so the publisher is suspended), **When** its earlier revisions are viewed, **Then** they remain readable, are shown as belonging to a suspended publisher, still name the original publisher ID and still verify (Q3). New publication is refused with "suspended".
5. **Given** a revision, **When** the native document and projection are retrieved, **Then** the native document is byte-for-byte what was published, and the projection equals the stored projection at its recorded projection version. It is never re-derived with a newer adapter.

---

### User Story 4 - No delegation or transfer chain is advertised (Priority: P1)

A reviewer of this feature checks the stated boundary. Attestations, verification results, public key records, API responses and UI text never claim or imply delegated signing authority, key transfer, cross-registry authority, transparency-log inclusion or any trust beyond "registry-held per-publisher key". The attestation statement has no field for delegation, transfer or a chain of signers, and Gözleme's verifier refuses any statement that carries a field, statement version or algorithm it does not know.

**Why this priority**: This is the spec's important boundary. CR-05 and constitution principle III require v1 to reserve authority and delegation fields and to reject, rather than trust, unsupported delegation chains, transfer chains, unknown signature algorithms and untrusted key replacement.

**Independent Test**: Inspect the published statement contract and the API and UI for any delegation, transfer or chain wording. Submit statements to the verifier with an added delegation field, an unknown statement version, an unknown algorithm, and a key ID that is not the publisher's key, and confirm each is refused with a fixed reason. Confirm that the boundary routes for delegation, transfer and key replacement answer "not found".

**Acceptance Scenarios**:

1. **Given** the attestation statement contract, **When** it is read, **Then** it defines no delegation, transfer, chain, witness or transparency field, and it states that these are unsupported in v1.
2. **Given** a statement with an unknown field, an unknown statement version, an unknown algorithm, or a key ID other than the publisher's registered key, **When** Gözleme verifies it, **Then** the result is "failed" with a fixed reason, never "verified".
3. **Given** the API, **When** a client requests routes for delegation, key transfer, key replacement or key rotation of a publisher, **Then** they answer "not found". The only way to replace a key in this slice is the operator succession command (Q5).
4. **Given** any revision view, response or screenshot, **When** it is inspected, **Then** it contains no wording that claims delegation, transfer, a chain of trust, transparency-log inclusion or publisher-held signing.
5. **Given** a manifest that tries to carry a signature, attestation, provenance or delegation claim, **When** it is published, **Then** it is refused with `reserved_claim`, exactly as in GOZ-0006 validation.

---

### User Story 5 - Only publisher members publish; members read; others see nothing (Priority: P1)

An ordinary member can list and read revisions but is not offered **Publish revision**, and a direct request is refused as forbidden. An admin without `publisher` is refused in the same way. A non-member, or someone using another organization's capability ID, gets the same empty "not found" as for an unknown organization, for publishing, listing and reading alike. A suspended publisher's capability cannot receive a new revision.

**Why this priority**: The permission contract and the decision point are mandatory for every new organization-scoped action (GOZ-0004-IF-01). The maintainer chose `publisher` only for publishing and member-level reads (Q4). Revisions are tenant-owned and private by default (GOZ-0003-IF-07).

**Independent Test**: For each built-in role, attempt publish, list and read as a member who holds exactly that role, and compare with the published contract table. Repeat as a non-member and with a cross-organization capability or revision ID, through both the API and the database isolation matrix.

**Acceptance Scenarios**:

1. **Given** a member without `publisher` (including an admin), **When** they request publication, **Then** they get "forbidden", and the manifest is not parsed.
2. **Given** a non-member, an unknown or malformed organization, capability or revision ID, or a capability or revision that belongs to another organization, **When** publication, listing or reading is requested, **Then** the answer is the identical empty "not found".
3. **Given** a member of organization A, **When** they query revisions, attestations or publisher keys of organization B directly in the database under their own context, **Then** they see no rows and can insert none.
4. **Given** a capability whose publisher is suspended, **When** a `publisher` member requests publication, **Then** the answer is "suspended", and both the API and the database write policy refuse it.
5. **Given** an account that publishes repeatedly, **When** it exceeds its publication budget, **Then** further requests are answered "rate limited". The rejection is recorded under its own operation.

---

### Edge Cases

- **Version precedence**: versions are unique per capability by Semantic Versioning 2.0.0 precedence. `1.0.0+a` and `1.0.0+b` are the same version, while `1.0.0-rc.1` and `1.0.0` are different versions. A version may be published in any order (for example `1.2.5` after `2.0.0`). The list is ordered by publication sequence, and this slice designates no "latest" revision.
- **Same bytes, new version**: an identical native document under a new version is a new revision with the same native digest. Digests are not unique.
- **Identical replay**: republishing an existing version with identical content is still `409 version_exists`. It is not an idempotent success and there is no idempotency key, so a retry after a lost response finds the stored revision by listing. Content is never compared, so the answer never reveals whether the submitted bytes match the stored revision.
- **Signing key unavailable**: if the key-encryption key is missing, wrong or cannot decrypt a publisher key, publication fails closed with "unavailable" and stores nothing. Reading and verifying existing revisions still works, because verification needs only the public key. The operator recovers by configuring a new key-encryption key and running key succession for the affected publishers (Q5).
- **Retired keys**: a revision signed before its key was retired still verifies, and its view shows that the signing key is retired, with the time and reason. A key retired as `compromised` is shown prominently. Gözleme does not decide which earlier signatures are genuine, because revocation is GOZ-0033.
- **Succession races**: a publication that is signing while its key is retired either commits with the old key (and is listed before the retirement) or fails and can be retried. A publisher never has two active keys.
- **First-key race**: two concurrent first publications for the same publisher (to the same or different capabilities) create exactly one signing key. Both revisions are signed with it, or the losing request fails and can be retried. Before its first publication a publisher has no key, and its key view says "no key yet".
- **Partial failure**: a revision and its attestation are created together or not at all. No unsigned revision is ever visible.
- **Registry origin not verified**: publication answers "unavailable", as for other publisher endpoints (GOZ-0005-IF-06).
- **Capability renamed between validate and publish**: the identity claim is checked again at publication; an alias that no longer belongs to the capability is `identity_mismatch`.
- **Role change mid-session**: the role is re-read on every request, so losing `publisher` takes effect on the next publication.
- **Large documents**: the GOZ-0006 bounds apply unchanged. The request body is at most 1 MiB and the native document at most 512 KiB.
- **Database outage**: publication, listing and reading fail closed with "unavailable".
- **Clock and ordering**: publication time is the database transaction time. Sequence numbers are per capability, start at 1, have no gaps for committed revisions, and are never reused.
- **Verification of a revision whose adapter has a newer version**: verification uses the stored projection and the recorded adapter and projection versions. It never re-runs a newer adapter.

## Requirements *(mandatory)*

### Functional Requirements

**Revisions**

- **FR-001**: A member holding `publisher` MUST be able to publish a manifest to a capability of an active publisher in their organization. Publication MUST run the GOZ-0006 validation pipeline unchanged (the same parser, adapters, rules and bounds) and MUST store only an accepted manifest.
- **FR-002**: A revision MUST record:
  - the capability (canonical ID) and publisher (canonical ID), and the organization;
  - the declared version;
  - a per-capability sequence number;
  - the envelope summary (the envelope without the native document);
  - the native format, format version and encoding;
  - the native document, byte-for-byte as submitted, with its SHA-256 digest and byte length;
  - the projection as produced at publication, its projection version, and its digest;
  - the adapter ID and version, and the rule-catalogue and schema-bundle versions they imply;
  - the publishing account (opaque ID);
  - the publication time.
- **FR-003**: Versions MUST be unique per capability by Semantic Versioning 2.0.0 precedence (build metadata ignored). A duplicate is refused with a "version exists" conflict, also under concurrency. No ordering is imposed between versions: a version lower than an existing one is accepted if it is unique, and revision lists are ordered by publication sequence.
- **FR-004**: A revision and its attestation MUST be immutable from creation. There is no update, replace or delete operation in the API. In the database, the application and owner logins MUST NOT be able to update, delete or truncate revisions, attestations, publisher public keys or key retirement records, and this is proven by integration tests as each login (GOZ-0000-IF-04, GOZ-0003-IF-19).
- **FR-005**: A revision and its attestation MUST be committed together atomically, so an unsigned revision is never visible.
- **FR-006**: A rejected manifest MUST be answered exactly as GOZ-0006 validation answers it (status code and result body), and nothing is stored.

**Signing keys and attestations**

- **FR-007**: Each publisher MUST have exactly one active signing key at a time, created inside the transaction of its first publication (never at publisher registration, which stays unchanged); key succession (FR-007a) replaces it with a new one. The key is registry-held (Q1). Its public key and key ID are stored immutably and readable by members. Its private key is stored only encrypted under a key-encryption key that is supplied through a Secret reference (Compose: an overridable variable with a throwaway default in `.env.example`; Helm: a Secret referenced by name). The private key and key-encryption key MUST never appear in API responses, the UI, logs, telemetry, screenshots or chart values, and MUST NOT rely on the Data Protection key ring or the database alone for protection.
- **FR-007a**: An operator command (run like `migrate`, with no API route) MUST add a new signing key for a named publisher, or for every publisher whose current key cannot be decrypted, and retire the current key with the time and a reason from a closed set (`lost`, `compromised`), in one transaction (Q5). Retirement is an append-only record: the old public key and its key ID are never changed or deleted, and the old private key is never used again. The command prints opaque IDs only, never key material, and works for a suspended publisher as well, while publication stays refused.
- **FR-008**: Each revision MUST have an attestation: a versioned statement (statement version 1) signed with the publisher's key. The statement MUST contain exactly:
  - the statement version and type;
  - the registry origin;
  - the canonical publisher ID and canonical capability ID;
  - the declared version and the sequence number;
  - the native digest and byte length, and the native format, format version and encoding;
  - the adapter ID and version, the projection version, and the projection digest;
  - the publication time;
  - the signing key ID and algorithm.

  It contains no alias, handle, display name, organization name or account email. The statement format, its canonical bytes and the signature algorithm are a published, versioned contract.
- **FR-009**: The projection digest MUST be the SHA-256 of a canonical, byte-deterministic serialization of the projection, defined in the contract, so that anyone can recompute it from the downloaded projection.
- **FR-010**: Verification MUST be computed again on request from the stored bytes: recompute both digests, check that they match the statement, and verify the signature with the publisher's stored public key named by the key ID, whether that key is active or retired. The result also reports the key's status (active, or retired with time and reason). The result is `verified` or `failed`, with a fixed reason from a closed set (for example `digest_mismatch`, `signature_invalid`, `unknown_key`, `unknown_algorithm`, `unknown_statement_version`, `unknown_field`). Nothing else counts as verified.
- **FR-011**: A member MUST be able to download a revision's native document, projection, statement, signature and the publisher's public key, in documented formats, so that the signature can be verified with an independent standard implementation.

**Boundary: no delegation**

- **FR-012**: The statement contract MUST define no delegation, transfer, chain, witness or transparency field, and MUST state that these are unsupported in v1. The verifier MUST refuse unknown fields, statement versions, algorithms and keys (CR-05).
- **FR-013**: No API route, UI element or response text may claim or imply delegated signing, key transfer, key replacement, key rotation, cross-registry authority, transparency-log inclusion or a publisher-held key. Routes for publisher key rotation, replacement, transfer and delegation MUST answer "not found", pinned by a boundary test. Operator key succession (FR-007a) is a local operator action. It is described as such and never as delegation or as a transfer of authority, and it makes no claim to other registries.

**Provenance and suspension**

- **FR-014**: Revisions and attestations MUST name canonical IDs only, and MUST be unaffected by renames and handle reclaims (GOZ-0005-IF-01, IF-03).
- **FR-015**: When a publisher is suspended, its revisions and attestations MUST stay readable by members and verifiable, MUST be shown as belonging to a suspended publisher, and new revisions MUST be refused with "suspended" in the API and in the write policy (Q3).

**Authorization and tenant isolation**

- **FR-016**: A new `manifest` resource MUST be added to the living permission contract (`docs/architecture/contracts/organization-permissions.md`) with the actions `manifest.publish` (`publisher` only), `manifest.list` and `manifest.read` (member-level), and `manifest.validate` moves from `capability` to `manifest` with its role unchanged (Q4). The contract tests and pinned counts are updated deliberately.
- **FR-017**: Publish, list and read endpoints MUST be mapped under `/api/v1/organizations/{organizationId}/…` on the authorized route group and pass the decision point in this order: origin verified ("unavailable"), membership (identical empty "not found"), permission ("forbidden"), capability or revision lookup (identical empty "not found"), and for publication, suspension ("suspended") and rate limit ("rate limited"). The manifest is parsed only after these checks. `POST /api/v1/publish` stays "not found".
- **FR-018**: Revisions, attestations and publisher keys MUST be tenant-owned by the publisher's organization, with row-level security enabled and forced. Their insert policies MUST admit only members holding `publisher` for an active publisher (held claim), and reads stay member-level. They MUST be added to the table classification and to the cross-tenant isolation matrix for both logins.
- **FR-019**: Publication MUST be rate-limited per account, shared across API instances and kept across restarts, with its own named operation for rejections.
- **FR-020**: Unsafe browser-facing routes MUST keep the same-origin check, and new pages MUST require a signed-in session.

**Observability and audit**

- **FR-021**: Publication and verification MUST each produce a span and an outcome counter with bounded labels (family, outcome, failure reason). Key material, signatures, signed statements, digests, native documents, projections, versions, names and aliases MUST NOT appear in span names, attributes, events, metric labels or logs. Capabilities, publishers and revisions are referred to by opaque ID only, after membership is confirmed.
- **FR-022**: Publishing a revision, creating a publisher signing key and key succession MUST each write a security audit event in the new range 1501–1510, after commit, naming opaque IDs only.

**Product, demo and compatibility**

- **FR-023**: The capability page MUST offer **Publish revision** (shown only with `manifest.publish`) after a successful validation, and a **Revisions** list and revision detail view (shown with `manifest.list` and `manifest.read`). The detail view shows the native document, the projection, the attestation, the verification result and the downloads. It handles "version exists", "forbidden", "suspended", "rate limited", "unavailable" and "not found" with clear messages.
- **FR-024**: The seed command MUST stay idempotent and act through the seeded admin or publisher. Any fixed demo revision it creates is created only once, because revisions are never deleted. Demos MUST publish with run-unique versions or capabilities.
- **FR-025**: The cumulative demo MUST include a `goz-0007` suite that covers:
  - User Story 1 in the browser, with screenshots;
  - each refused mutation of User Story 2, including database-level refusals as each login and the out-of-band tamper check;
  - independent verification of a downloaded attestation;
  - provenance across a rename and a handle reclaim;
  - key succession: after the operator retires a key as `lost`, a new revision is signed with the new key, and earlier revisions still verify with the retired key shown as retired;
  - the delegation boundary of User Story 4;
  - the role matrix, non-member and cross-organization "not found";
  - spans for publication and verification in the trace store, and a no-secret scan of captured telemetry for key material, signatures and statements;
  - regression of all earlier suites.
- **FR-026**: Storage changes MUST be one or more reviewed migrations that follow the GOZ-0003 to GOZ-0006 rules and update their pins. The GOZ-0006 boundary assertion that allows only the rate-limit counter to match `(manifest|revision|projection)` is narrowed only to the tables this slice adds. The validate endpoint keeps storing no manifest data.

### Key Entities

- **Manifest revision**: An immutable, tenant-owned record of one accepted manifest for one capability at one version. It holds the native bytes, the digests, the projection, the versions of everything that interpreted it, and the publishing account and time.
- **Revision attestation**: An immutable signed statement (statement version 1) over a revision's identity and digests, with its signature, key ID and algorithm.
- **Publisher signing key**: A registry-held key per publisher, with an immutable public key and key ID readable by members, and a private key stored only encrypted under the key-encryption key. A publisher has one active key and any number of retired ones.
- **Key retirement**: An append-only record that a key was retired by operator succession, with the time, the reason (`lost` or `compromised`) and the successor key.
- **Key-encryption key**: An operator-supplied secret, given by Secret reference, that encrypts publisher private keys. It is never stored in the database.
- **Verification result**: A result computed on request: `verified` or `failed` with a fixed reason. It is never stored as authority.
- **Provenance**: The canonical publisher and capability IDs, the registry origin, the publishing account, the time and the sequence. It never changes when names, aliases or handles do.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the demo, a publisher member publishes two revisions of a capability in the browser in under three minutes of total interaction, and both verify.
- **SC-002**: 100% of mutation attempts are refused (same-version republish, API update or delete, and database update, delete or truncate as the application and owner logins), and every earlier revision still verifies afterwards.
- **SC-003**: 100% of out-of-band tampering cases (native byte, projection, statement or signature changed) verify as "failed" with the expected reason. None is reported as verified.
- **SC-004**: A downloaded attestation verifies with an independent standard implementation, and its digests match the downloaded native document and projection, for every family's sample.
- **SC-005**: Zero occurrences of private key material, the key-encryption key, signatures or signed statements in captured telemetry, logs, screenshots or error responses. Zero delegation, transfer or publisher-held-key wording in the API, UI or contract.
- **SC-006**: 100% of (built-in role × new action) combinations behave as the published contract table states, verified automatically. Non-members and cross-organization IDs get an identical "not found", and the database isolation matrix shows no cross-tenant row for either login.
- **SC-007**: Publishing a manifest at the documented size limit completes in under two seconds, and verifying a revision completes in under one second.
- **SC-008**: After key succession, 100% of earlier revisions still verify, and 100% of new revisions are signed with the new key.
- **SC-009**: All earlier cumulative demo suites still pass.

## Assumptions

- The signature algorithm is a single modern, widely implemented algorithm with deterministic signatures (expected Ed25519). The plan pins it and the statement serialization after checking the current .NET and independent-verifier support. Any other algorithm is refused.
- Operator key succession (Q5) is the only key change in this slice. Self-service or API rotation, revocation of signatures made with a retired key, and key-change trust for federation peers are out of scope: the open key-rotation question stays with GOZ-0039, revocation with GOZ-0033 and federation key pins with GOZ-0029.
- Signed lifecycle events (deprecation, withdrawal, yanking) are out of scope. Revisions have no lifecycle state in this slice.
- Artifacts (packaged ZIPs) are GOZ-0009. A revision here covers the manifest only.
- Child capability identities and projection generations independent of the revision are GOZ-0008. The stored projection here is the projection v1 produced at publication.
- Revisions are private to the organization (tenant-owned, member-level reads). Public visibility and discovery are GOZ-0012, and approval workflow is GOZ-0021.
- There is no "latest revision" designation and no version-constraint resolution in this slice (GOZ-0026, GOZ-0027).
- Republishing an existing version is always a conflict, even with identical content. Clients recover from a lost response by listing revisions.
- The publication rate limit uses a PostgreSQL counter like the validation limit (GOZ-0006 shared-rate-limit), with its own budget and configuration key.
- The native text is stored and exportable as sent. Search and embeddings later use projection fields only (maintainer decision stored-text-projection-only, 2026-11-18).
- Row-level security remains defence in depth beside application authorization.
