# Feature Specification: Child Capability Identities and Projections

**Feature Branch**: `feature/goz-0008-child-identities`

**Created**: 2026-11-19

**Status**: Draft (specify 2026-11-19)

**Input**: User description: "GOZ-0008 — Child capability identities and projections. User-visible outcome: browse server with independent tool/resource/prompt child identities. In scope: hierarchical child identities, immutable revision and projection tracking. Important boundary: child IDs are not projection generation IDs." (full prompt: `docs/prompts/spec-0008.md`, revision 9, including the convention sections GOZ-0000-IF-11, GOZ-0001-IF-01/02/09/11/17, GOZ-0002-IF-01/05/06/07/08, GOZ-0003-IF-01/02/07/08/09/10/19, GOZ-0004-IF-01/02/04/06/07/08/09/13, GOZ-0005-IF-01/03/06/07/09/10/12, GOZ-0006-IF-01/02/03/04/05/07/12/14 and GOZ-0007-IF-01/02/03/04/05/06/08/09/11)

## Clarifications

### Session 2026-11-19

Maintainer decisions taken at specify (2026-11-19, verbatim reply "1: A, 2: A, 3: B"; recorded in `docs/specs/008-child-identities/progress.yaml`):

- Q1: What permanent form does a child ID take, and do children get aliases? → A: **Hierarchical, no aliases.** `gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>`. Only the child UUID is stored; the form is rendered with the verified registry origin, like the GOZ-0005 forms. Children get no friendly aliases and no alias reservations. This is a namespace contract change (GOZ-0005-IF-12).
- Q2: How is a later revision's entry matched to an existing child, and what happens to duplicate kind + native name within one revision? → A: **Match by kind + native name.** A renamed entry is a new child, and the old child is shown as absent from that revision. Publishing refuses a revision whose projection has two entries with the same kind and native name, through a new publish-time rule. GOZ-0006 validation, envelope v1, projection v1 and the adapters are unchanged, so validate can accept a manifest that publish then refuses; this is a documented contract delta.
- Q3: How far do projection generations go? → A: **Generation 1 plus an operator re-projection command.** Every revision records generation 1 (the attested publication projection). One operator command, run like `migrate` and `signing-key-succession` with no API route, re-projects a stored revision into an additive, unattested generation N+1 that keeps every child ID.

Clarify session (2026-11-19, maintainer):

- Q: When this feature is installed, how should the one-time child assignment (FR-016) handle a revision that was already published and has two or more entries with the same kind and native name? → A: **Option A.** The first duplicate entry, by position in the stored projection, takes the kind + native name child. Each further duplicate gets its own new child marked as a duplicate. Later revisions never match a marked duplicate, so it shows as absent from every later revision.
- Q: If the operator re-projection command produces output that is byte-for-byte identical to the revision's latest generation, should it still record a new generation? → A: **Option A.** Always record generation N+1, even when it is identical. The output reports that its digest equals the previous generation's, and the command exits 0.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Browse a server's independent child identities (Priority: P1)

A member of an organization opens a capability that has published revisions, for example an MCP server. A new **Children** view lists every tool, resource, resource template and prompt that any of its revisions declares. Each child shows its kind, its permanent child ID, the name and title it has in each revision where it appears, and the revisions it appears in. The member opens one child and sees that child's own page: its ID, its kind, its parent capability, and its history across revisions. The same records are available from the API.

**Why this priority**: This is the stated user-visible outcome. Later specs key on child identities: canonical visibility and discovery (GOZ-0012), search (GOZ-0013), per-child approvals (GOZ-0021) and the relationship graph (GOZ-0024).

**Independent Test**: Sign in as a seeded member. Publish one MCP revision that declares two tools, one resource and one prompt to the seeded capability. Confirm that the Children view lists four children with distinct child IDs and the right kinds. Open each child page, and confirm that the API returns the same children.

**Acceptance Scenarios**:

1. **Given** a capability with one published revision whose projection has N entries, **When** a member opens its Children view, **Then** exactly N children are listed, each with a distinct permanent child ID, its kind and its native name and title in that revision.
2. **Given** a child, **When** a member opens its page, **Then** they see the child ID, kind, parent capability (canonical ID and display name), and every revision of the capability with whether the child appears in it and, where it does, its native name and title in that revision.
3. **Given** a revision, **When** a member opens the revision's page (GOZ-0007), **Then** it lists the children that revision declares, each linked to its child page by child ID.
4. **Given** families whose projections have no entries (`skill`, `model`), **When** a member opens the Children view, **Then** it states that this capability declares no children, and no child identity exists.
5. **Given** any child view, **When** it is shown, **Then** it states that children are declared by the publisher in the manifest and are not verified behaviour (GOZ-0006-IF-12).

---

### User Story 2 - Child identities persist across revisions (Priority: P1)

The publisher member publishes a second revision of the same server. It keeps one tool unchanged, changes the title of another, drops the prompt and adds a new tool. In the Children view, the kept and retitled tools keep their child IDs, the new tool gets a new child ID, and the dropped prompt keeps its identity and history but is shown as absent from the newer revision. No child identity is deleted or reassigned.

**Why this priority**: Child IDs are only useful to approvals, search and dependencies if they are stable across revisions. A child ID that changed with every revision would be just an entry position.

**Independent Test**: After story 1, publish a second revision with the changes above. Confirm the IDs stay stable for the kept and retitled tools, that the new tool has a new ID, that the dropped prompt is kept but absent from the newer revision, and that the first revision still lists exactly its original children.

**Acceptance Scenarios**:

1. **Given** a child that appears in revision 1, **When** revision 2 declares an entry with the same kind and native name, whatever its title, description or position, **Then** that entry is the same child, with the same child ID.
6. **Given** a revision whose projection has two entries with the same kind and native name, **When** publication is attempted, **Then** it is refused with an unprocessable result that names the duplicate rule and location without echoing the name, and nothing is stored; validating the same manifest still answers as GOZ-0006 does.
7. **Given** an entry renamed between revisions, **When** the newer revision is published, **Then** the new name gets a new child ID and the old child is shown as absent from that revision.
2. **Given** a revision that declares an entry that matches no existing child of the capability, **When** it is published, **Then** a new child identity is created in the same operation as the revision.
3. **Given** a child that a later revision no longer declares, **When** members view it, **Then** it keeps its ID and history and is shown as absent from that revision; it is never deleted.
4. **Given** two revisions published in any order (GOZ-0007 allows any version order), **When** children are matched, **Then** matching follows publication sequence, and the children recorded for an earlier revision never change.
5. **Given** two capabilities whose manifests declare entries with the same kind and native name, **When** both are published, **Then** each capability has its own child with a different child ID.

---

### User Story 3 - Child IDs are not projection generation IDs (Priority: P1)

Each revision's projection is recorded as a projection generation: the first generation is the projection stored and attested at publication (GOZ-0007). An operator can re-project a stored revision with an operator command (no API route); this adds generation N+1, additive and unattested, and never changes generation 1 or the attestation. A child keeps the same child ID in every projection generation. Generations and child identities have different identifiers, and neither is derived from the other.

**Why this priority**: This is the stated important boundary. A future adapter version can re-project a revision; if child IDs were generation IDs, every re-projection would break approvals and references.

**Independent Test**: For a published revision, run the operator re-projection command and confirm a generation 2 exists, that every child keeps its ID, that generation and child IDs are distinct values of distinct record kinds, that the revision still verifies, and that no API field, URL or telemetry attribute uses one in place of the other.

**Acceptance Scenarios**:

1. **Given** any published revision, **When** it is viewed, **Then** it has a projection generation 1 whose bytes and digest are exactly the stored, attested projection.
2. **Given** a revision with more than one projection generation, **When** members view its children, **Then** each child has the same child ID in every generation, and the first generation, its bytes and the revision's attestation are unchanged.
3. **Given** a child ID, **When** it is used where a projection generation is expected (or the reverse), **Then** it is not found: the identifiers are never interchangeable.
4. **Given** a later projection generation, **When** it is shown, **Then** it is labelled as not attested, since statement v1 covers only the publication projection (GOZ-0007-IF-01).
5. **Given** the re-projection command, **When** its re-projection would add, drop or change the kind or native name of any entry compared with generation 1, **Then** it refuses and records nothing, because a generation never changes which children a revision declares; it may differ only in fields that are not child keys.
6. **Given** the re-projection command, **When** it targets an unknown revision or runs without the owner login, **Then** it exits with a documented non-zero code and records nothing.
7. **Given** the re-projection command, **When** its output is byte-for-byte identical to the latest generation, **Then** it still records generation N+1, reports that its digest equals the previous generation's, and exits 0.

---

### User Story 4 - Child identities are immutable and tenant-private (Priority: P1)

Child identities, their per-revision records and projection generations are never changed or deleted by anyone. Members of another organization, signed-out visitors and non-members cannot see that a child exists. Members cannot create, rename or delete children directly; children come only from publication.

**Why this priority**: Security invariant 6 (immutable publications) and the private-by-default tenancy of GOZ-0003 apply to every new record.

**Independent Test**: As the application and owner logins, try to update, delete and truncate the child, child-revision and generation records, and confirm every attempt is refused. Read the Children views and API as a member of the other seeded organization, as a signed-out visitor and with malformed or foreign IDs, and confirm the same empty "not found" answer.

**Acceptance Scenarios**:

1. **Given** child records, **When** any login (owner and superuser included) tries to update, delete or truncate them, **Then** the change is refused.
2. **Given** a member of another organization, **When** they request a child, a child list or a projection generation by ID, **Then** they get the same empty "not found" as for an unknown ID, with no existence leak.
3. **Given** a signed-out visitor, **When** they request any child page or endpoint, **Then** they are sent to sign in (pages) or refused (API).
4. **Given** the API, **When** a client sends a create, update, replace or delete request for a child, **Then** no such route exists.
5. **Given** a suspended publisher (GOZ-0007-IF-11), **When** members view its children, **Then** the children stay readable and frozen.

---

### User Story 5 - Revisions published before this feature have children (Priority: P2)

Revisions that already exist when this feature is installed, such as those the GOZ-0007 demo published, also get their child identities and generation 1 records, in publication order, as if they had been published with this feature. Nothing in those revisions changes.

**Why this priority**: The cumulative demo stack already holds revisions, and later specs must be able to rely on every revision having children.

**Independent Test**: Start from a stack with GOZ-0007 revisions, install the feature, and confirm that every existing revision has generation 1 and children that follow the same continuity rule, while every revision still verifies.

**Acceptance Scenarios**:

1. **Given** revisions published before the feature, **When** it is installed, **Then** each has generation 1 and its children, assigned in publication sequence per capability.
2. **Given** the step that assigns them, **When** it is repeated, **Then** it changes nothing (idempotent).
3. **Given** those revisions, **When** they are verified, **Then** every one verifies exactly as before.
4. **Given** an existing revision whose stored projection has two entries with the same kind and native name, **When** the feature is installed, **Then** the first entry by position takes the kind + native name child, the second gets a new child marked as a duplicate, both appear in that revision, and no later revision ever matches the duplicate-marked child.

### Edge Cases

- A projection whose entries share a kind and native name within one revision (possible for `a2a` skills and `api` operations, GOZ-0006-IF-07): publication is refused (FR-005a). A revision published before this feature with such entries is never refused; it is assigned per FR-016a (first by position keeps the key, the rest become duplicate-marked children). An `event` channel and an `event` operation with the same key differ in kind and are two children.
- An `api` operation whose derived name `"<METHOD> <path>"` equals another operation's `operationId`: two entries of the same kind and native name, so publication is refused.
- Entries reordered between revisions: order is never part of a child key, so IDs do not change (GOZ-0006-IF-01).
- A renamed entry (same tool, new native name): a new child; the old child is absent from that revision.
- Native names that differ only in case or Unicode normalization: different native names, so different children (exact match).
- An entry whose kind changes (for example a `channel` that becomes an `operation`): always a different child; a child's kind never changes.
- A capability with 1,000 entries per revision (the GOZ-0006 bound): children are assigned in the same operation as publication without exceeding the publication's normal latency by more than the success criterion allows.
- Concurrent publications to the same capability: GOZ-0007 serializes the sequence; child assignment follows it, so no two child identities are created for the same match.
- A publication that fails after children are created: nothing is stored (the revision, its children and its generation commit together or not at all).
- A child ID, generation ID, revision ID or capability ID used in the wrong route: the empty "not found".
- A member removed from the organization between requests: the next request is "not found".
- Child names, titles and native names in URLs, spans, metrics or logs: never; only opaque IDs.

## Requirements *(mandatory)*

### Functional Requirements

**Child identities**

- **FR-001**: The system MUST give every projection entry of every published revision a child identity: a permanent identifier that belongs to exactly one capability and has exactly one kind, taken from the projection entry `kind` (`tool`, `resource`, `resource-template`, `prompt`, `skill`, `operation`, `webhook`, `channel`).
- **FR-002**: The child identifier form MUST be `gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>` (maintainer Q1), with only the child UUID stored and the form rendered with the verified registry origin; it is recorded in the namespace contract. Children MUST have no friendly alias and no alias reservation. It MUST NOT embed or be derived from a native name, title, alias, handle, organization ID, entry position, revision ID, sequence or projection generation (GOZ-0006-IF-01, GOZ-0007-IF-08).
- **FR-003**: A child identity MUST never be changed, reassigned to another capability or kind, or deleted. A capability's set of child identities only grows.
- **FR-004**: Children MUST come only from publication (and from the one-time assignment for existing revisions, FR-016). There MUST be no route or UI to create, rename, edit or delete a child.
- **FR-005**: The system MUST match a later revision's entries to existing child identities of the same capability by exact kind and native name (maintainer Q2), applied in publication sequence per capability. Title, description and entry position MUST NOT affect matching. A renamed entry MUST get a new child identity. Matching MUST be deterministic: the same revisions published in the same sequence always produce the same child assignments.
- **FR-005a**: Publication MUST refuse a revision whose projection has two or more entries with the same kind and native name, with an unprocessable result naming a new, documented duplicate-entry rule and the location of each duplicate, never echoing the name, and storing nothing. GOZ-0006 validation results MUST stay unchanged; the difference between validate and publish is documented as a contract delta.
- **FR-006**: Children of different capabilities MUST never share an identity, even with identical kinds and native names.

**Per-revision child records**

- **FR-007**: For every revision, the system MUST record which children it declares and, for each, its kind, native name and title as projected in that revision. These records MUST be immutable and created in the same operation as the revision; a revision and its child records are stored together or not at all.
- **FR-008**: The children recorded for a revision MUST never change after it is published, whatever later revisions declare.

**Projection generations**

- **FR-009**: The system MUST record each revision's publication projection as projection generation 1, referencing exactly the stored, attested projection bytes and digest, with no copy that could diverge.
- **FR-010**: A projection generation MUST have its own identifier, distinct from and never derived from any child identifier, and the reverse. No API field, route parameter or telemetry attribute may carry one in place of the other.
- **FR-011**: Projection generations MUST be additive and immutable. A later generation MUST never replace, rewrite or re-attest generation 1 (GOZ-0007-IF-01), MUST be compared only within one adapter ID, adapter version and projection version (GOZ-0007-IF-09), and MUST be labelled "not attested".
- **FR-011a**: The only way to add a later generation MUST be an operator command run with the owner login and no account context (no API route, pattern GOZ-0007-IF-06), with a bounded span, after-commit audit with opaque IDs, documented exit codes, refusal of an unknown revision, and refusal when the output's set of entry kinds and native names differs from generation 1. Output byte-identical to the latest generation MUST still be recorded as generation N+1 (exit 0), with the command reporting that the digest equals the previous generation's.
- **FR-012**: A child's identity MUST be the same in every projection generation of a revision in which it appears.

**Browsing**

- **FR-013**: Members of the capability's organization MUST be able to list a capability's children (paged, in a stable order that does not depend on names), read one child with its per-revision history, read the children of a revision, and read a revision's projection generations. Reads are member-level, like `manifest.list` and `manifest.read`.
- **FR-014**: Every child view MUST state that children are publisher-declared and not verified behaviour, and MUST NOT show approval, trust, safety, lifecycle or verification badges for a child.
- **FR-015**: Organization-scoped child endpoints MUST follow the existing access order: unavailable (origin not verified) → empty not-found (malformed ID, unknown or non-member organization) → forbidden (no permission) → empty not-found (unknown, foreign or misplaced publisher, capability, revision, child or generation). Pages MUST require a session and render the existing "not found" page.

**Existing revisions**

- **FR-016**: Revisions that exist when the feature is installed MUST receive generation 1 and their children by the same continuity rule, in publication sequence per capability, through an idempotent step that changes no revision or attestation.
- **FR-016a**: When an existing revision's stored projection has two or more entries with the same kind and native name, the install step MUST NOT refuse or skip it. The first such entry, by position in the stored projection, MUST take (or match) the kind + native name child. Each further duplicate MUST get its own new child identity, permanently marked as a duplicate. Matching (FR-005) MUST never select a duplicate-marked child, so it is absent from every later revision. Every entry still has exactly one child revision record (SC-002).

**Isolation and immutability**

- **FR-017**: Every new record MUST be tenant-owned by the capability's organization with enforced row-level security, appear in the cross-tenant isolation matrix for both logins, and be refused update, delete and truncate for every login (GOZ-0003-IF-01, IF-19; GOZ-0007-IF-02).
- **FR-018**: Writes of child records MUST be admitted only in the publishing transaction for an active publisher (held claim) by a member holding `publisher`, mirrored in the database write policies (GOZ-0004-IF-02, GOZ-0005-IF-01).

**Observability and audit**

- **FR-019**: Child assignment and projection generation recording MUST emit spans with bounded attributes and counts per projection (children declared, children newly created, children matched, children absent), and counters with bounded labels (family, kind, outcome). Names, titles, native names, digests and documents MUST never appear in spans, metrics, logs or URLs; only opaque IDs, after membership is confirmed.
- **FR-020**: Audit events, if any, MUST use the next free range from 1601 and carry opaque IDs only.

**Compatibility**

- **FR-021**: GOZ-0006 validation results, GOZ-0007 publication responses (other than any added child summary), statements, signatures and verification MUST be unchanged. Envelope v1, projection v1 and the adapters MUST NOT change (GOZ-0006 FR-005).
- **FR-022**: Existing demos and smoke tests MUST keep passing; the Compose stack MUST stay runnable without cloud services.

### Key Entities

- **Child identity**: A permanent identity of one declared element (tool, resource, resource template, prompt, skill, operation, webhook or channel) of one capability. Has a kind that never changes. Is never deleted. May carry a permanent duplicate marker (only from FR-016a), which excludes it from matching.
- **Child revision record**: The appearance of one child in one revision: the revision, the child, and the kind, native name and title projected in that revision. Immutable.
- **Projection generation**: One recorded projection of one revision, numbered from 1. Generation 1 is the publication projection covered by the attestation. Has its own identifier, unrelated to child identifiers.
- **Capability, revision, attestation** (existing, GOZ-0005, GOZ-0007): unchanged.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A member can go from a capability to any of its children and see the child's history across revisions in at most two navigations.
- **SC-002**: For every published revision, the number of child revision records equals the number of projection entries (100% across the demo and test fixtures).
- **SC-003**: Republishing the same sequence of manifests into a fresh stack produces the same matching of entries to children every time (deterministic; checked across repeated runs).
- **SC-004**: 0 child identities change, disappear or move across capabilities over any sequence of publications, re-projections and installs in the test suite.
- **SC-005**: 0 cross-tenant, signed-out or malformed-ID requests reveal whether a child or generation exists.
- **SC-006**: Publishing a revision with 1,000 entries completes within the existing publication time budget plus 1 second.
- **SC-007**: 0 occurrences of child names, titles, native names, documents, digests or secrets in captured telemetry and logs during the demo.

## Assumptions

- Child kinds and native names come only from projection v1 entries as stored with the revision (GOZ-0006, GOZ-0007-IF-01); the system never re-parses the native document to find children.
- `skill` and `model` projections have no entries, so those capabilities have no children in this slice.
- Child reads reuse the member-level read permissions of the `manifest` resource unless planning shows a new resource is needed; any new action follows the GOZ-0004-IF-01 contract obligations and is raised with the maintainer before it is added.
- Child status beyond "present in revision" or "absent from revision" (deprecation, withdrawal, approvals, visibility) is out of scope (GOZ-0021, GOZ-0012).
- Canonical public visibility, search indexing of children and portable child references in lockfiles or federation are out of scope (GOZ-0012, GOZ-0013, GOZ-0028, GOZ-0030); this slice only keeps child identities anchored on the canonical capability ID so they can become portable (GOZ-0007-IF-08).
- Child identities are publisher-declared and advisory; live discovery of a running server's tools is out of scope.
- The demo publishes to the seeded `manifest-demo` capability with run-unique versions and never expects it to have no revisions or children (GOZ-0007-IF-02).
