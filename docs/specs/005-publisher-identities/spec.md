# Feature Specification: Publisher Identities and Canonical Capability IDs

**Feature Branch**: `feature/goz-0005-publisher-identities`

**Created**: 2026-11-16

**Status**: Ready for implementation (specify, clarify, plan, tasks and analyze passed 2026-11-16)

**Input**: User description: "GOZ-0005 — Publisher identities and canonical capability IDs. Build Publisher identities and canonical capability IDs as one bounded, runnable, tested vertical slice. User-visible outcome: register publisher; create stable capability ID unaffected by display-name changes. In scope: origin-qualified IDs and publisher namespace rules. Important boundary: preserve permanent origin and friendly alias reservation." (full prompt: `docs/prompts/spec-0005.md`, revision 6, including implementation-alignment bullets GOZ-0000-IF-01/11/16, GOZ-0001-IF-01/02/05/09/10/11/17, GOZ-0002-IF-01/05/06/07/08/09/10/11, GOZ-0003-IF-01/02/06/07/08/09/10/19 and GOZ-0004-IF-01/02/04/05/06/07/08/09/13)

## Clarifications

### Session 2026-11-16

Maintainer decisions taken before drafting (2026-11-16, recorded in `docs/specs/005-publisher-identities/progress.yaml`):

- Q1: How does the installation get its permanent origin? → A: The operator configures an origin name. The first migration run records it as instance reference data. From then on the origin never changes, and if the configured value differs from the recorded one, the service refuses to start.
- Q2: How do publishers relate to organizations? → A: Each publisher belongs to exactly one organization, and an organization may register several publishers. Publishers are tenant-owned and private by default, and never move to another organization in this slice.
- Q3: How unique are publisher handles? → A: Unique across the whole installation. Registering a handle that is already taken is answered "unavailable" (conflict), which reveals only that the handle is reserved: never its owner or any metadata. Registration is rate-limited per account. Every other constraint failure is answered exactly like an unknown identifier.
- Q4: What shape is a canonical capability ID? → A: The origin plus an opaque, permanent capability identifier. It never embeds an organization identifier or a display name. Separately, a friendly alias `<publisher-handle>/<capability-name>` is reserved to the capability permanently and is never reassigned or reused.
- Q5: Which roles hold the new actions? → A: Registering a publisher needs `admin`. Creating a capability ID and changing a capability's display name need `publisher`. Viewing publishers and capabilities is open to every member.
- Q6: Do organization creators also start with `publisher`? → A: No. GOZ-0004's initial-role rule is unchanged (creators hold `admin` and `member`). An admin grants `publisher` through the existing role controls. The seed grants it to a demo account.
- Q7: What happens to an account's work when it loses `publisher` or leaves? → A: Nothing. Publishers, capability IDs and alias reservations belong to the organization, not the account. This slice has no drafts or pending publications; later specs decide their own in-flight work.
- Q8: What may change after creation? → A: Only display names. The origin, publisher handles, capability names, aliases and canonical IDs are immutable. There is no retirement, deletion or alias release in this slice, and a reserved alias is never reusable.

- Q9 (raised after drafting, 2026-11-16; amends Q8): How is handle squatting handled, given open sign-up and unverified emails let anyone create an organization and claim any handle? → A: Operator reserve and reclaim, through the operator command line only (no HTTP surface, like `reset-password` and `unlock`). The operator can (1) reserve a handle in advance, either blocked for everyone or earmarked for one organization by its opaque ID, and (2) reclaim a registered handle. Reclaiming suspends the publisher that held it. Its capabilities keep their canonical IDs, and their exact aliases stay retired and reserved forever. The handle can then be registered by the rightful organization, which can never reuse those retired aliases. Every operator action is audited. Q8 is amended: a **handle** may be reassigned through reclaim, but canonical IDs and aliases are never reassigned or reused.

- Q10 (clarify session, 2026-11-16): How does operator reclaim suspend a publisher in another organization without a row-security bypass, a new privileged helper or a member service account (FR-036, GOZ-0003-IF-04)? → A: Instance-level claim tables. Handle claims, operator handle reservations and alias reservations are installation-owned reference data (like the origin), not tenant rows; their unique constraints enforce installation-wide uniqueness. The application login may insert into them but not list or read other organizations' entries. Reclaim changes only the handle-claim row; a publisher is suspended exactly when it no longer holds its handle claim, so no tenant row is changed from outside its tenant.

- Q11 (plan, 2026-11-16): Which database login do the operator handle commands use? → A: The owner login (`gozleme_migrator`), which the operator already holds for `migrate`. The claim tables enable row security without forcing it, so the owner is the installation's authority over them while the application login stays bound by their policies. Guard triggers bind the owner to the allowed one-way transitions. No new login or secret is added (plan research R4).

Agent choice, disclosed to the maintainer for veto: changing a **publisher's** display name needs `admin`, matching publisher registration (Q5 named only the capability display name).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Register a publisher and create a capability whose ID survives renaming (Priority: P1)

An organization admin registers a publisher for the organization by choosing a unique handle (for example `acme-tools`) and a display name. The confirmation shows the handle and the organization's opaque reference, not just its (non-unique) display name. A member holding `publisher` then creates a capability under that publisher, choosing a capability name (for example `weather-lookup`) and a display name. The system assigns a canonical, origin-qualified capability ID and reserves the friendly alias `acme-tools/weather-lookup`. The publisher member then changes the capability's display name. The canonical ID and the alias are exactly the same afterwards.

**Why this priority**: This is the stated user-visible outcome, and every later publishing spec (typed manifests GOZ-0006, revisions GOZ-0007, child IDs GOZ-0008) builds on a stable capability identity.

**Independent Test**: Sign in as the seeded admin, register a publisher, sign in as the seeded publisher member, create a capability, record its canonical ID and alias, rename it twice, and confirm the canonical ID and alias are byte-for-byte unchanged in the UI and the API.

**Acceptance Scenarios**:

1. **Given** an organization admin, **When** they register a publisher with a valid, unused handle and a display name, **Then** the publisher is created in that organization, gets a permanent origin-qualified publisher ID, and the confirmation shows the handle and the organization's opaque reference.
2. **Given** a member holding `publisher`, **When** they create a capability with a valid capability name and a display name under one of their organization's publishers, **Then** the capability gets a canonical ID made of the installation origin and a new opaque identifier, and the alias `<handle>/<capability-name>` is reserved to it.
3. **Given** a capability, **When** a member holding `publisher` changes its display name (any number of times), **Then** its canonical ID and alias are unchanged and the new display name is shown.
4. **Given** any member of the organization, **When** they view the organization's publishers and capabilities, **Then** they see each publisher's handle, display name and ID, and each capability's canonical ID, alias and display name.
5. **Given** a successful registration, creation or rename, **When** it completes, **Then** a security audit event names the organization, the acting account and the publisher or capability by opaque identifiers only.

---

### User Story 2 - The origin and every alias stay permanently reserved (Priority: P1)

An operator has started the installation with origin `registry.demo.gozleme.test`. Later the operator (by mistake, or on purpose) restarts it configured with a different origin. The service refuses to start and says the configured origin does not match the recorded one; no identifier was rewritten. Separately, a second organization tries to register the handle `acme-tools`, which is already taken; it is told only that the handle is unavailable. A publisher member of the first organization tries to create another capability named `weather-lookup` under `acme-tools`; it is refused because the alias is already reserved. No request can change a handle, a capability name, an alias, a canonical ID or the origin.

**Why this priority**: This is the spec's stated important boundary, and constitution principle III requires that canonical IDs are never reassigned or reused.

**Independent Test**: Restart the service with a different origin and observe the refusal; restore the original origin and observe a normal start with identical IDs. Attempt a duplicate handle from another organization, a duplicate capability name under the same publisher, and direct changes to every immutable field through the API and as the application database login; every attempt is refused and nothing changes.

**Acceptance Scenarios**:

1. **Given** an installation with a recorded origin, **When** it is started with a different configured origin, **Then** it refuses to start with a clear operator message, and once restarted with the original value every existing canonical ID is unchanged.
2. **Given** an installation with no recorded origin, **When** the first migration runs with a configured, valid origin, **Then** that origin is recorded permanently; **When** no origin or an invalid one is configured, **Then** the migration and the service refuse to proceed.
3. **Given** a handle already held by any organization, **When** another (or the same) organization tries to register it, **Then** the answer is "unavailable" and reveals nothing else: not the owning organization, the publisher's display name, its ID or whether it has capabilities.
4. **Given** a capability name already used under a publisher, **When** a publisher member tries to create another capability with that name under the same publisher, **Then** it is refused as a conflict and nothing is created.
5. **Given** any existing publisher or capability, **When** anyone tries to change its handle, capability name, alias, canonical ID or owning organization through the API, **Then** no such operation exists or it is refused, and nothing changes.
6. **Given** a valid database context for a member (even an admin holding `publisher`), **When** it tries to change any immutable field or delete a publisher or capability directly in the database, **Then** the change is refused for both the application and the owner database logins.

---

### User Story 3 - Only the right roles can register publishers and create capabilities (Priority: P1)

An ordinary member sees the organization's publishers and capabilities but is not offered registration or creation controls, and direct requests are refused as forbidden. An admin without `publisher` can register a publisher but cannot create a capability until an admin grants them `publisher`. A non-member of the organization gets the same "not found" as for an unknown organization for every one of these operations. When the publisher member loses `publisher` or leaves, everything they created stays with the organization.

**Why this priority**: GOZ-0004 deliberately left `publisher` inert; this spec gives it its actions and must follow the published permission contract and the database-layer role enforcement decided for GOZ-0004 (IF-02 option A).

**Independent Test**: For each built-in role and each new action, attempt the action as a member holding exactly that role and compare against the published contract table. Remove `publisher` from the creating account and confirm its capabilities and their IDs are unchanged.

**Acceptance Scenarios**:

1. **Given** a member without `admin`, **When** they try to register a publisher or change a publisher's display name, **Then** they are refused as forbidden and nothing changes.
2. **Given** a member without `publisher` (including an admin), **When** they try to create a capability or change a capability's display name, **Then** they are refused as forbidden and nothing changes.
3. **Given** a person who is not a member of the organization, **When** they attempt any publisher or capability operation in it (including with a valid publisher or capability ID of that organization), **Then** they get the identical empty "not found" as for an unknown organization.
4. **Given** a publisher or capability ID belonging to organization B, **When** a member of organization A uses it under organization A's path, **Then** the answer is the same "not found" as for an unknown ID.
5. **Given** an account that created capabilities, **When** it loses `publisher` or leaves the organization, **Then** the publishers, capabilities, canonical IDs and aliases are unchanged and still belong to the organization.
6. **Given** the database layer, **When** a member without the required role inserts a publisher or capability, or updates a display name, directly with a valid context, **Then** the database refuses it independently of the application.

---

### User Story 4 - Handles and names follow one predictable namespace rule (Priority: P2)

A publisher member types a handle or capability name with uppercase letters, spaces, accents, look-alike characters, leading or trailing hyphens, or a reserved word. The system rejects it with a clear explanation of the allowed form, rather than silently changing it into something else, so two people can never end up with handles that look alike.

**Why this priority**: Namespace rules are in scope, and silent normalization or Unicode handling would create look-alike aliases and non-deterministic identifiers.

**Independent Test**: Submit a table of valid and invalid handles and capability names through the API and the form and confirm every valid one is accepted unchanged and every invalid one is rejected with a validation error and nothing created.

**Acceptance Scenarios**:

1. **Given** a handle or capability name in the allowed form, **When** it is submitted, **Then** it is stored exactly as typed.
2. **Given** a handle or capability name outside the allowed form (uppercase, non-ASCII, whitespace, punctuation other than single interior hyphens, too short or too long, or a reserved handle), **When** it is submitted, **Then** it is rejected with a validation error naming the rule and nothing is created.

---

### User Story 5 - An operator can prevent and undo handle squatting (Priority: P1)

Anyone who can sign up can create an organization and become its admin, and emails are not verified (GOZ-0001-IF-05). Before inviting the real Acme, the operator reserves `acme` for Acme's organization (by its opaque ID) and blocks `gozleme-official` for everyone. Later a stranger registers `acme-labs` and creates two capabilities under it. Acme complains, and the operator reclaims `acme-labs`. The stranger's publisher is suspended: it can no longer create capabilities or change display names. Its two capabilities keep their canonical IDs, and their aliases stay permanently retired. Acme's admin then registers `acme-labs`, and its publisher members can create capabilities under any name except the two retired ones.

**Why this priority**: Without it, a squatted handle could only be fixed by editing the database by hand (maintainer decision Q9).

**Independent Test**: Using the operator command line, reserve one handle for a fixed organization and block another. Confirm that a different organization gets "unavailable" for both and that the earmarked organization can register its own. Then register a handle from a second organization, create a capability under it, reclaim the handle, and confirm: the old publisher is suspended, the capability's canonical ID and alias are unchanged and still shown to its own organization, the earmarked organization can register the handle, and creating the retired capability name under it is refused.

**Acceptance Scenarios**:

1. **Given** a handle the operator blocked, **When** any organization tries to register it, **Then** the answer is "unavailable", identical to a taken handle.
2. **Given** a handle the operator earmarked for organization A, **When** an admin of A registers it, **Then** it succeeds; **When** any other organization tries, **Then** the answer is "unavailable".
3. **Given** a registered handle, **When** the operator reclaims it, **Then** the holding publisher is suspended. Its members still see it, its capabilities, their canonical IDs and aliases, marked as suspended. They can no longer create capabilities under it or change its display names, and the handle is free to register or is earmarked as the operator chose.
4. **Given** a reclaimed handle registered by a new organization, **When** a publisher member there creates a capability whose alias was retired, **Then** it is refused as a conflict; any other name succeeds.
5. **Given** any operator reservation, release of a reservation, or reclaim, **When** it runs, **Then** a security audit event records it by opaque identifiers only, and the operator command shows the result.
6. **Given** a signed-in account (even an admin), **When** it looks for any HTTP route to reserve, reclaim, unsuspend or transfer a handle, **Then** no such route exists.

### Edge Cases

- Two organizations register the same handle at the same moment: exactly one succeeds; the other gets "unavailable".
- Two publisher members create the same capability name under the same publisher at the same moment: exactly one succeeds; the other gets a conflict.
- A handle differs from a taken one only by case or by a look-alike character: impossible, because only lowercase ASCII is accepted.
- A display name is set to the same value it already has: succeeds without a duplicate audit event.
- A display name is empty, only whitespace or over the length limit: validation error, nothing changes.
- A publisher ID of another organization, a malformed ID, an unknown ID, or a capability ID used under the wrong publisher: identical "not found".
- A handle that is unavailable: the answer is identical whether the handle belongs to the caller's own organization or another, and identical for every caller.
- Repeated registration attempts probing for taken handles: rate-limited per account; the rate-limit rejection is recorded as a publisher-registration rejection, not as a sign-in failure.
- A publisher member loses `publisher` while a creation request is in flight: the request is decided by the role read in that request's transaction; nothing created before is affected.
- The database is unavailable: every operation fails closed with the standard unavailable response.
- An operator changes the origin configuration while the service runs: the change takes effect only on restart, which is refused (User Story 2).
- The organization's display name is the same as another organization's: the registration confirmation still identifies the organization unambiguously by its opaque reference.

## Requirements *(mandatory)*

### Functional Requirements

**Registry origin**

- **FR-001**: The installation MUST have exactly one permanent origin, an operator-configured name in a restricted host-name form (lowercase ASCII letters, digits, hyphens and dots; no scheme, path, port, user information or trailing dot; at most 253 characters). It is not derived from an organization, an account or an email address.
- **FR-002**: The first migration run with a valid configured origin MUST record it. Recording MUST be possible only through migrations or operator configuration, never through a request handler; the ordinary application database login MUST be able only to read it, through a single pinned read path.
- **FR-003**: If the configured origin is missing, invalid, or differs from the recorded one, the migration step and the service MUST refuse to proceed with a clear operator message, and MUST NOT rewrite any recorded identifier. Nothing in the product MAY change the recorded origin.
- **FR-004**: The origin MUST be shown to signed-in users wherever canonical IDs are shown, and MUST NOT be treated as a secret.

**Publishers**

- **FR-005**: An organization member holding `admin` MUST be able to register a publisher for that organization with a handle and a display name. An organization MAY have several publishers. A publisher belongs to exactly one organization for its whole life.
- **FR-006**: A publisher MUST receive a permanent, origin-qualified publisher ID that consists of the origin and a new opaque identifier, and does not embed the organization's identifier, the handle or any display name.
- **FR-007**: Publisher handles MUST be unique across the whole installation: at any moment at most one active publisher holds a handle. A handle is released only by an operator reclaim (FR-032 to FR-036), never by a user. A taken, blocked or earmarked-for-another-organization handle MUST be answered "unavailable" (conflict) identically for every caller and every owner, revealing only that the handle cannot be registered. Every other constraint failure MUST be answered exactly like an unknown identifier.
- **FR-008**: Publisher registration MUST be rate-limited per account, and its rejection MUST be recorded under its own operation, not as a sign-in or registration failure.
- **FR-009**: The registration confirmation MUST identify the target organization unambiguously (handle plus the organization's opaque reference), because organization display names are not unique (GOZ-0002-IF-10).
- **FR-010**: Handles MUST NOT be derived from, suggested from or reserved by an organization's display name, an account's email or an email domain (GOZ-0001-IF-05, GOZ-0002-IF-10).
- **FR-011**: A member holding `admin` MUST be able to change a publisher's display name. The handle and the publisher ID MUST NOT change.

**Capabilities and canonical IDs**

- **FR-012**: A member holding `publisher` MUST be able to create a capability under one of the organization's publishers with a capability name and a display name.
- **FR-013**: The capability MUST receive a canonical ID consisting of the installation origin and a new, permanent, opaque capability identifier, rendered as `gz://<origin>/cap/<capability-identifier>`. It MUST NOT embed the organization identifier, the handle, the capability name or any display name, and MUST NOT be reassigned or reused (constitution principle III).
- **FR-014**: The capability MUST be given the friendly alias `<publisher-handle>/<capability-name>`, reserved to it permanently. Aliases MUST be unique across the whole installation, including aliases retired by a reclaim; a duplicate is refused as a conflict. Before any reclaim, that conflict can only come from the caller's own publisher. After a reclaim, it reveals to the handle's new owner only that a retired alias exists under that handle. That is inherent to permanent aliases, and the conflict never reveals the previous owner or the capability.
- **FR-015**: A member holding `publisher` MUST be able to change a capability's display name. The canonical ID, alias and capability name MUST NOT change.
- **FR-016**: Every member of the organization MUST be able to list its publishers, list each publisher's capabilities, and view a single publisher or capability, including IDs, handle, alias and display names.

**Namespace rules**

- **FR-017**: Handles and capability names MUST use one rule: lowercase ASCII letters `a–z`, digits `0–9` and single hyphens; starting with a letter; not ending with a hyphen; no consecutive hyphens. Handles are 2–39 characters, capability names 2–64. Input outside this form MUST be rejected, never normalized, case-folded or transliterated. Validation MUST be ordinal and behave identically under the installation's invariant-globalization setting (GOZ-0000-IF-16).
- **FR-018**: A documented, fixed list of reserved handles (for example `gozleme`, `admin`, `api`, `system`, `root`, `registry`, `publisher`, `capability`) MUST be rejected as a validation error for every caller.

**Permanence and immutability**

- **FR-019**: The origin, the handle a publisher registered, publisher IDs, capability IDs, capability names, aliases, the owning organization, the owning publisher and creation metadata MUST be immutable. Users can change only publisher and capability display names. The operator can only suspend a publisher by reclaiming its handle (FR-034), which changes the installation-owned handle claim, never the publisher row. This MUST be enforced both by the application (no other operation exists) and at the database layer (guards that bind every database login, including the owner). A suspension MUST never be undone in this slice.
- **FR-020**: There MUST be no deletion, user-initiated retirement, alias release, handle rename or transfer of publishers or capabilities between organizations in this slice. Reclaim is not a transfer: the suspended publisher and its capabilities stay with their organization.
- **FR-021**: Losing `publisher`, losing `admin`, or leaving the organization MUST NOT change, revoke or reassign any publisher, capability, ID or alias. They belong to the organization.

**Authorization and tenant isolation**

- **FR-022**: Each new operation MUST be a named action in the living organization permission contract, with its role-table row: register publisher and change publisher display name → `admin` only; create capability and change capability display name → `publisher` only; view publishers and capabilities → every role. Each new endpoint MUST be organization-scoped, declare its action, and pass the existing decision point (membership first, then permission, then rule checks).
- **FR-023**: Publishers and capabilities MUST be tenant-owned by their organization and private by default, protected by both the application check and forced row-level security; reads are member-level in the database. A non-member, an unknown or malformed ID, and an ID belonging to another organization MUST get the identical empty "not found".
- **FR-024**: Role-gated writes MUST also be enforced in the database: only `admin` members may insert publishers or change their display names, and only `publisher` members may insert capabilities or change their display names, through a non-privileged role check that generalises the existing admin check (GOZ-0004-IF-02, option A). Every such write MUST be proven for both the application and owner logins.
- **FR-025**: The existing boundary that no unscoped capability catalogue and no publish endpoint exist MUST stay: the instance-wide capability list and the publish endpoint MUST still answer "not found" (discovery is GOZ-0012, publication GOZ-0007 and later). Boundary tests are narrowed only where this slice legitimately adds a surface.

**Operator handle control (maintainer decision Q9)**

- **FR-032**: The operator command line MUST be able to reserve a handle as **blocked** (no organization may register it) or **earmarked** for one organization, named by its opaque ID (only an admin of that organization may register it), and to release a reservation that has not been used. There MUST be no HTTP, web or account-level surface for any operator handle action.
- **FR-033**: Reserved handles MUST follow the same namespace rule (FR-017). An earmark MUST name an existing organization; an unknown one MUST be refused with an operator error. Registering an earmarked handle MUST consume the earmark.
- **FR-034**: The operator command line MUST be able to reclaim a registered handle, with an optional earmark for the rightful organization. Reclaim MUST suspend the publisher that holds it, permanently in this slice, and free or earmark the handle in the same transaction. A suspended publisher and its capabilities MUST stay visible to its own organization with their canonical IDs and aliases, marked suspended. No capability can be created under it, and no display name of it or its capabilities can be changed.
- **FR-035**: Every alias created under a reclaimed handle MUST stay reserved to its original capability forever (FR-014). Reclaim MUST NOT change any canonical ID, alias, capability name or display name.
- **FR-036**: Each operator action MUST write a security audit event in the FR-027 range, by opaque identifiers only, and print an operator-facing result that contains no secrets. The mechanism MUST respect the tenant-RLS rules: no role that bypasses row security, no new privileged database helper, and no member service account (GOZ-0003-IF-04). Operator actions MUST change only installation-owned claim and reservation rows (FR-037), never a tenant-owned publisher or capability row. Any design that would need a non-account principal to touch tenant rows is out of scope and goes back to the maintainer as a security-review item.
- **FR-037** (maintainer decision Q10): Handle claims, operator handle reservations and alias reservations MUST be installation-owned reference data, not tenant-owned rows, and their unique constraints MUST be what enforces installation-wide uniqueness of handles (FR-007) and aliases (FR-014). The application database login MUST be able to insert claims and reservations within a valid member request but MUST NOT be able to list or read entries belonging to other organizations; a conflict reveals only that the value is taken. A publisher's suspended state MUST be derived from whether it still holds its handle claim, not stored on the publisher row. Claim rows are immutable except for the operator reclaim transition, which is guarded for every database login.

**Observability and audit**

- **FR-026**: Publisher registration, capability creation and display-name changes MUST produce spans and outcome counters with bounded labels (operation and outcome category). Handles, capability names, aliases, display names, emails and accounts MUST NOT appear in span names, metric labels or logs; publishers and capabilities are referred to only by opaque IDs, and only after membership is confirmed.
- **FR-027**: Registration, creation, display-name changes and operator handle actions MUST write security audit events in a new event-ID range starting at 1401, referring to people, organizations, publishers and capabilities only by opaque identifiers.

**Product, demo and compatibility**

- **FR-028**: The organization area MUST offer a publishers view (list, register, detail with capabilities, create capability, rename), showing controls only to members whose permissions allow the action and handling "forbidden", "unavailable", conflict and validation responses with clear messages. New pages MUST require a signed-in session; new browser-facing unsafe routes MUST keep the same-origin check.
- **FR-029**: The seed command MUST, while acting as the seeded admin, grant `publisher` to a fixed demo account in the fixed demo organization and remain idempotent. Compose MUST supply a throwaway default origin, and the deployment chart MUST require one.
- **FR-030**: The cumulative demo MUST include a `goz-0005` suite that demonstrates User Story 1 in the browser with screenshots, the handle "unavailable" answer from a second organization, the alias conflict, the role matrix for the new actions, the database-layer refusals of immutable-field changes, the origin-mismatch refusal, an operator reserve-and-reclaim walkthrough (User Story 5), a no-secret scan of captured telemetry, and regression of all earlier suites.
- **FR-031**: The migration MUST be additive, MUST NOT lift the owner binding or disable any guard, and MUST update the existing schema and migration pins deliberately.

### Key Entities

- **Registry origin**: The installation's single permanent identity, an operator-chosen host-style name recorded once. Instance reference data, not tenant-owned.
- **Publisher**: An organization-owned identity under which capabilities are named. Has a permanent publisher ID, the handle it registered (unique among active publishers), an editable display name, its owning organization, and an active or suspended state derived from whether it still holds its handle claim.
- **Handle claim**: Installation-owned record that a handle is held by one publisher; reclaim ends the claim, which suspends that publisher (FR-037).
- **Handle reservation**: An installation-owned, operator-made reservation of a handle, either blocked for everyone or earmarked for one organization.
- **Alias reservation**: Installation-owned record that permanently reserves one alias to one capability, including aliases retired by a reclaim (FR-037).
- **Capability**: A stable identity under one publisher. Has a canonical, origin-qualified ID, a capability name unique within its publisher, an editable display name, and a permanent friendly alias.
- **Friendly alias**: `<publisher-handle>/<capability-name>`, permanently reserved to exactly one capability.
- **Reserved handle**: A fixed handle that nobody may register.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the demo, a publisher is registered and a capability created in under two minutes of user interaction, and after at least two display-name changes the canonical ID and alias are identical to their original values.
- **SC-002**: 100% of attempts to change an immutable field (origin, handle, capability name, alias, IDs, owning organization or publisher) are refused or change nothing, through the API and at the database layer for both database logins.
- **SC-003**: Starting the installation with a mismatched origin is refused 100% of the time, and no recorded identifier changes as a result.
- **SC-004**: A taken handle yields the same "unavailable" answer for every caller, and the answer contains no owner, publisher ID, display name or capability information.
- **SC-005**: 100% of (built-in role × new action) combinations behave as the published contract table states, verified automatically.
- **SC-006**: Non-members and cross-organization IDs get an identical "not found" for 100% of publisher and capability operations.
- **SC-007**: Under concurrent duplicate registrations or creations, exactly one succeeds every time.
- **SC-008**: Zero handles, names, emails, signed contexts or keys appear in captured telemetry or screenshots beyond the synthetic values intentionally shown in the UI.
- **SC-009**: All earlier cumulative demo suites still pass.
- **SC-010**: In the demo, a squatted handle is reclaimed and registered by the rightful organization, with zero direct database edits and zero changes to any existing canonical ID or alias.

## Assumptions

- Capability creation is open only to `publisher` members of the owning organization and its name conflicts are invisible across organizations, so only publisher registration is rate-limited.
- Handles and names are rejected, not normalized, so no Unicode normalization or case folding is needed; this also avoids look-alike aliases.
- Closing open sign-up (GOZ-0001) stays the recommended posture for shared instances. Operator reclaim is the remedy when squatting happens anyway, not a substitute for it.
- The reserved-handle list (FR-018) is separate from operator reservations (FR-032). It is fixed in this slice and documented in the contract; changing it is a contract change.
- The publisher ID uses the same `gz://<origin>/…` form as the capability ID, with a distinct path segment, so the two kinds cannot be confused.
- Lists of publishers and capabilities stay unpaged in this slice.
- Alias lookup is only within the owning organization in this slice. Resolving an alias or canonical ID across organizations or anonymously belongs to discovery (GOZ-0012) and federation.
- Signing keys, publisher verification, manifests and revisions belong to GOZ-0006, GOZ-0007 and later; a registered publisher in this slice makes no signature or authenticity claim.
- Row-level security remains defence in depth beside application authorization and is not claimed as protection against SQL running inside an attacker's own valid request context (invariant 2).
