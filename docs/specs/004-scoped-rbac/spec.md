# Feature Specification: Scoped RBAC Permission Foundation

**Feature Branch**: `feature/goz-0004-scoped-rbac`

**Created**: 2026-11-16

**Status**: Draft

**Input**: User description: "GOZ-0004 — Scoped RBAC permission foundation. Build Scoped RBAC permission foundation as one bounded, runnable, tested vertical slice. User-visible outcome: assign a role and watch a privileged operation be denied then permitted. In scope: built-in roles, organization membership permissions, action/resource contract. Boundary: full policy evaluator comes later (GOZ-0018)." (full prompt: `docs/prompts/spec-0004.md`, revision 6, including implementation-alignment bullets GOZ-0000-IF-10/11, GOZ-0001-IF-01/02/04/05/09/11/17, GOZ-0002-IF-01/03/04/05/06/07/08/09/13/14/15 and GOZ-0003-IF-01/02/03/05/09/10/19)

## Clarifications

### Session 2026-11-16

Maintainer decision taken before drafting (recorded in `.gozleme/spec-registry.yaml` and the prompt, revision 6):

- Q: How do existing memberships get their initial roles? → A: Option B. No data migration assigns roles. The role is added to each membership with a default naming the managing role, so every existing member keeps exactly the rights they have today. The default then changes to the least-privileged role for future memberships. The creator of a new organization gets the managing role; an account that joins by invitation gets the least-privileged role. Existing organizations tidy up through the new role controls. The seed sets the fixed demo members' roles explicitly while acting as a seeded member.

Maintainer decisions on the drafted clarification questions (2026-11-16):

- Q: Which built-in roles ship now? → A: Option B, the full MVP role set: `admin`, `publisher`, `reviewer`, `approver` and `member`. Roles whose actions belong to later specs (publishing GOZ-0005, approvals GOZ-0021) are defined now and gain their actions in those specs.
- Q: Who may create, list and revoke invitations, and what happens on demotion? → A: Option A. Only members holding the managing role (`admin`) may create, list and revoke invitations. A role change that removes that permission revokes the account's open invitations in the same transaction, with audit 1104 for each, exactly as removal does (GOZ-0002-IF-04).
- Q: How far does the database enforce roles? → A: Option B. Row-level rules and guards restrict every manager-only write (rename, invitation writes, member removal and role changes) to `admin` members of that organization, in addition to the application check. Departure (a member deleting its own membership) stays permitted subject to the last-manager rule. If this needs a new privileged database helper, that helper is a security-review item and goes back to the maintainer before implementation.
- Q: What do `publisher`, `reviewer` and `approver` permit in this slice? → A: Option A. Exactly what `member` permits; their own actions arrive with GOZ-0005 and GOZ-0021. `admin` does not implicitly hold them.
- Q: One role or several per membership? → A: Option B. A set of roles per membership, held in the membership row. A schema default can still fill existing rows, so the initial-role decision (option B) applies unchanged and no data migration is needed. (The maintainer first answered C, then corrected it to B the same day; the follow-up question about a separate table's upgrade mechanism was withdrawn.)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Assign a role and watch a privileged operation go from denied to permitted (Priority: P1)

Two people belong to the same organization. One manages it; the other is an ordinary member. The ordinary member tries a privileged operation, for example renaming the organization. The UI does not offer the control, and a direct request is refused as forbidden. Nothing changes. The manager opens the member list, sees each member's role, and gives the ordinary member the managing role. On the ordinary member's very next request, with no sign-out or sign-in, the same privileged operation succeeds. The manager then takes the role back, and the operation is refused again on the next request.

**Why this priority**: This is the stated user-visible outcome. It also closes GOZ-0002-IF-15: until now any member could do everything, including minting an invitation to admit a second account they control.

**Independent Test**: Sign in as the seeded manager and the seeded ordinary member of the fixed demo organization in two browsers. Have the ordinary member attempt the privileged operation through the UI and the API (denied, unchanged). Have the manager change the role. Repeat the attempt without signing in again (permitted). Revoke the role and repeat (denied).

**Acceptance Scenarios**:

1. **Given** an ordinary member of an organization, **When** they view the organization page, **Then** controls for operations their role does not permit are not shown, and the member list shows every member's role.
2. **Given** an ordinary member, **When** they send a privileged request directly (for example a rename), **Then** the request is refused as forbidden, the organization is unchanged, and the refusal is recorded as an authorization denial.
3. **Given** a manager, **When** they give an ordinary member the managing role, **Then** the change is saved, the member list shows the new role, and an audit event names the organization, the target account and the acting account by opaque identifiers only.
4. **Given** the member who was just given the managing role, **When** they repeat the privileged request without signing in again, **Then** it succeeds.
5. **Given** a manager who removes that role again, **When** the affected member repeats the request, **Then** it is refused as forbidden on that very request.
6. **Given** a person who is not a member of the organization, **When** they attempt any operation in it, including changing a role, **Then** they get the same empty "not found" as for an unknown organization, never "forbidden".

---

### User Story 2 - Every organization operation is governed by one published permission contract (Priority: P1)

A maintainer of a later feature (publishing, approvals, policy) needs a stable, documented list of what can be done to what, and by which built-in role. Each existing organization operation is an action on a resource type. Each built-in role is a fixed set of permitted actions. Every organization endpoint asks one authorization decision point, after the membership check, whether the caller's current role permits the action. The contract is published as a document and pinned by tests, so that an endpoint without a declared action, or an action without a declared role mapping, fails the build.

**Why this priority**: The action/resource contract is in scope and is what GOZ-0005, GOZ-0018 and GOZ-0021 build on. Without a single decision point, later specs would scatter role checks.

**Independent Test**: Read the published contract. For every built-in role and every organization action, call the operation as a member holding that role and compare allowed versus forbidden with the contract table. Confirm an automated check fails if an organization endpoint has no declared action.

**Acceptance Scenarios**:

1. **Given** the published contract, **When** each role attempts each organization action, **Then** the outcome matches the contract table exactly (allowed or forbidden), with no exceptions.
2. **Given** a new organization endpoint added without a declared action, **When** the test suite runs, **Then** it fails.
3. **Given** any authorization decision, **When** it is traced, **Then** the trace records the action, the resource type and the allow or deny outcome, and never a display name, email, invitation code or other private value.

---

### User Story 3 - An organization can never lose its last manager (Priority: P1)

A manager tries to demote themselves while they are the only manager, or to remove or demote the only other manager while concurrently that manager demotes them. The system refuses any change that would leave the organization without at least one member who can manage membership. The existing "last member" protection becomes this broader "last manager" protection, and it holds under concurrent requests.

**Why this priority**: Without it, a single role change could leave an organization unmanageable, and there is no platform administrator to recover it (GOZ-0001-IF-05).

**Independent Test**: With exactly one manager, attempt self-demotion and self-removal (leave); each is refused with a clear conflict. Run two concurrent demotions of the last two managers; exactly one succeeds.

**Acceptance Scenarios**:

1. **Given** an organization with exactly one manager, **When** that manager demotes themselves or leaves, **Then** the request is refused with a "last manager" conflict and nothing changes.
2. **Given** two managers who concurrently demote each other, **When** both requests complete, **Then** at least one manager remains.
3. **Given** an organization with several managers, **When** one manager demotes themselves, **Then** the change succeeds and takes effect on their next request.

---

### User Story 4 - Existing organizations keep working after the upgrade (Priority: P2)

An operator upgrades an existing deployment. Every member of every existing organization keeps exactly the rights they had before (everything), so no organization becomes unmanageable. New invitees from then on join with the least-privileged role, and a new organization's creator manages it. Members of existing organizations can tidy up roles through the new controls.

**Why this priority**: This implements the maintainer's option-B decision. The upgrade must be additive and safe for the pre-upgrade migration step.

**Independent Test**: Create organizations and memberships on the previous release's schema, apply the migration, and confirm every pre-existing member holds the managing role, a newly redeemed invitation yields the least-privileged role, and a newly created organization's creator holds the managing role.

**Acceptance Scenarios**:

1. **Given** memberships created before the upgrade, **When** the migration runs, **Then** every one of them holds `admin` and `member`, and no data-changing statement was needed.
2. **Given** an upgraded deployment, **When** an account redeems an invitation, **Then** it joins with the least-privileged role.
3. **Given** an upgraded deployment, **When** an account creates an organization, **Then** it holds the managing role in it.

---

### User Story 5 - The database refuses role escalation even if the application is bypassed (Priority: P2)

A security reviewer connects as the ordinary application role with a valid context for an ordinary member and tries to raise that member's own role, change another member's role, or move a membership to another organization. The database refuses these changes independently of the application check, to the degree the security posture below requires.

**Why this priority**: GOZ-0003-IF-05 and IF-19 show that, without a change, a member could rewrite its own membership row at the database layer, and that an authorized manager's change to another member's role would silently change nothing. Row-level security is meant to be an independent layer (constitution principle IV).

**Independent Test**: As the application role, with a valid context, attempt each forbidden change against the real database and confirm it is refused or changes nothing, while the permitted manager change succeeds.

**Acceptance Scenarios**:

1. **Given** a valid context for an ordinary member, **When** it updates its own membership row to raise its role, **Then** the change is refused.
2. **Given** a valid context for any member, **When** it changes a membership's organization, account, join method, join time or originating invitation, **Then** the change is refused.
3. **Given** a valid context for a manager, **When** it changes another member's role in its own organization, **Then** the change succeeds (it is not silently filtered out).
4. **Given** a valid context for a manager of organization A, **When** it changes a role in organization B, **Then** nothing in organization B changes.

### Edge Cases

- A member's role is changed while they have the organization page open: their next action is decided by the new role; the page shows a clear "you no longer have permission" message on a forbidden response and refreshes its controls.
- A manager tries to change the role of an account that is not a member, of a malformed identifier, or of a member of another organization: "not found", identical to an unknown member.
- A manager assigns the role a member already holds: succeeds without change and without a duplicate audit event.
- A request names a role that does not exist: validation error; no custom roles can be created.
- A member removed concurrently with a role change: the removal and the role change serialize under the organization lock; the result is either removed, or role-changed then removed, never a role on a deleted membership.
- A member who holds no permission for an action and is also the target (for example leaving): leaving remains permitted for every member except the last manager.
- An invitation created by a manager who is later demoted or removed: see FR-012.
- The database is unavailable during a role change: the request fails closed with the standard unavailable response.
- A role change by email address is attempted: no such path exists; roles are assigned only to existing members by opaque account identifier (GOZ-0002-IF-14).

## Requirements *(mandatory)*

### Functional Requirements

**Built-in roles and the permission contract**

- **FR-001**: The system MUST provide exactly these built-in organization roles: `admin` (the managing role: rename the organization, create, list and revoke invitations, remove members, change roles), `publisher`, `reviewer`, `approver` and `member` (the least-privileged role: view the organization and its member list with roles, switch to it, leave). In this slice `publisher`, `reviewer` and `approver` permit exactly what `member` permits; their own actions arrive with GOZ-0005 and GOZ-0021. No role implies another: `admin` does not hold `publisher`, `reviewer` or `approver` unless granted them explicitly. Roles cannot be created, renamed or edited by users.
- **FR-002**: Each membership MUST hold a set of built-in roles, held in the membership itself, with no duplicates and no unknown role names. `member` is the baseline: every membership holds it, and it cannot be taken away while the membership exists. A caller's permissions are the union of the actions its roles permit. Roles exist only through a membership in that organization, disappear with it, and grant nothing in any other organization.
- **FR-003**: The system MUST publish an action/resource contract: a documented catalogue in which every organization operation is a named action on a named resource type (starting from the existing operation vocabulary: create, rename, select, invitation create/list/revoke, invitation redeem, member list/remove/leave, plus role change), and a table mapping each built-in role to the actions it permits. The contract MUST be versioned alongside the API contract.
- **FR-004**: Every organization-scoped operation MUST first perform the existing membership check (non-members, unknown and malformed organization identifiers get the same empty "not found", uncached) and THEN ask a single authorization decision point whether the caller's role permits the action. A member who lacks the permission MUST receive "forbidden" with no change made.
- **FR-005**: The caller's role MUST be read from the database on every request. A role grant or removal MUST take effect on the affected account's next request. Roles MUST NOT be stored in or read from session claims, and role changes MUST NOT sign the account out (GOZ-0001-IF-04).
- **FR-006**: An automated check MUST fail when an organization endpoint has no declared action, or when an action has no entry in the published role-permission table.
- **FR-007**: Operations that are not organization-scoped keep their current rule: any signed-in account may create an organization and redeem an invitation; selecting an organization and leaving it are permitted to every member (leaving is subject to FR-010).

**Assigning roles**

- **FR-008**: A member whose role permits it MUST be able to change another member's role, and their own, by selecting the member by opaque account identifier from the member list. Assignment by email address or to non-members MUST NOT exist.
- **FR-009**: The member list MUST show each member's role to every member who can see the list.
- **FR-010**: The system MUST refuse any role change, removal or departure that would leave the organization with no member holding the managing role ("last manager"). This replaces the GOZ-0002 "last member" rule, MUST serialize with all membership changes in that organization, and MUST hold under concurrency.
- **FR-011**: The creator of a new organization MUST hold the managing role in it. An account that joins by invitation MUST hold the least-privileged role. On upgrade, every existing member MUST keep exactly today's rights, so every existing membership MUST hold `admin` and `member`. This MUST be applied by a schema default only (`admin` and `member`), which the same migration then changes to `member` alone for future memberships, with no data-changing statement and no lifting of the owner binding (maintainer decision, option B). The guard on the role set MUST ship in the same migration.
- **FR-012**: Only `admin` members MUST be able to create, list and revoke invitations; other members do not see the invitation list. A role change that takes the invitation permission away MUST revoke that account's open invitations in the same transaction, under the organization lock, with an audit event for each (as removal does). Removal and departure MUST keep revoking the departing account's open invitations (GOZ-0002-IF-04) and keep that behaviour's test. A test MUST show that an invitation created by a demoted account can no longer be redeemed.

**Database layer**

- **FR-013**: At the database layer, with the ordinary application role and a valid account context:
  - a membership's organization, account, join method, join time and originating invitation MUST be immutable;
  - a member MUST NOT be able to change its own role through its own-row update rule;
  - a permitted role change of another member MUST actually take effect (not be silently filtered);
  - only an `admin` of the row's organization may rename it, insert, update or revoke its invitations, delete another member's membership, or change any role; a non-admin member's attempt MUST be refused or change nothing, and MUST leave the data unchanged;
  - a member MAY delete its own membership (departure), subject to the last-manager rule, and MAY update its own context preference;
  - the invitation-redemption path (an account adding its own membership through an open invitation it found by code) MUST keep working and MUST create a `member` membership only.
- **FR-013a**: The last-manager rule (FR-010) MUST also hold at the database layer, so that no write by the application role can leave an organization without an `admin`.
- **FR-014**: Each new database rule and guard MUST follow the GOZ-0003 conventions (forced row-level security binding the owner too, no new privileged helper unless separately approved, public execution revoked, `insufficient_privilege` for guard refusals) and MUST be proven by behavioural tests as both the application and owner roles.
- **FR-015**: The migration MUST be additive and safe for the pre-upgrade migration step; it MUST NOT disable any guard and MUST NOT lift the owner binding.

**Boundary: no policy evaluator**

- **FR-016**: This slice MUST NOT introduce custom roles, per-resource or per-user grants, deny rules, conditions, attribute-based rules, policy documents, policy versions or a policy authoring surface. Those belong to GOZ-0018 and later. A boundary test MUST assert that no such API route, UI control or stored policy exists, and that the decision point accepts only (role, action, resource type).
- **FR-017**: Ownership transfer, organization deletion, delegated administration across organizations, a platform administrator, and HTTP password reset or unlock remain absent (R3-08, R3-24, GOZ-0001-IF-05). The GOZ-0002 boundary tests that forbid role wording and role routes MUST be narrowed deliberately to allow exactly this slice's role surface and nothing more.

**Observability and audit**

- **FR-018**: Every authorization decision MUST produce a span with the action, the resource type and the allow or deny outcome. A bounded `forbidden` failure category MUST be added to the organization operation counter. No display names, emails, invitation codes, account identifiers as metric labels, or other private values may appear in telemetry.
- **FR-019**: Role changes and forbidden attempts MUST write security audit events in a new event-ID range starting at 1301, referring to people only by opaque account identifiers.

**Product, demo and compatibility**

- **FR-020**: The organization page MUST show each member's role, offer role controls only to members permitted to change roles, hide controls for actions the viewer may not perform, and handle a forbidden response with a clear message. Every new or changed browser-facing unsafe route MUST keep the same-origin check, and every new API route MUST require a session and be mapped under the organization route group.
- **FR-021**: The seed command MUST give the fixed demo members explicit roles (one manager and one ordinary member in the fixed demo organization) while acting as a seeded member, and remain idempotent.
- **FR-022**: The cumulative demo MUST include a `goz-0004` suite that demonstrates User Story 1 in the browser with screenshots, a role/action matrix check through the API, the last-manager refusal, the database-layer refusals, the policy-evaluator boundary, a no-secret scan of captured telemetry, and regression of all earlier suites.

### Key Entities

- **Built-in role**: A named, fixed set of permitted actions in one organization. Not user-editable.
- **Membership role**: The built-in role (or roles, per FR-002) a member holds in one organization.
- **Action**: A named operation (for example "rename organization", "change member role") on a resource type.
- **Resource type**: The kind of thing an action applies to: organization, membership or invitation in this slice.
- **Authorization decision**: The allow or deny outcome for (caller's current role, action, resource type), evaluated after the membership check.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the demo, an ordinary member's privileged operation is refused, then permitted on the first request after a manager grants the role, then refused on the first request after it is removed, with zero sign-outs required.
- **SC-002**: 100% of (built-in role × organization action) combinations behave as the published contract table states, verified automatically.
- **SC-003**: Non-members get an identical "not found" for 100% of organization operations, including role changes; "forbidden" is never shown to a non-member.
- **SC-004**: Across repeated concurrent demotion and removal attempts, no organization is ever left without a manager.
- **SC-005**: After upgrade, 100% of pre-existing members retain every operation they could perform before.
- **SC-006**: Every database-layer escalation attempt in the test matrix is refused or changes nothing, as both the application and owner roles.
- **SC-007**: Zero private values (names, emails, codes, signed contexts, keys) appear in captured telemetry or screenshots.
- **SC-008**: All earlier cumulative demo suites still pass.

## Assumptions

- `member` as an always-held baseline grant (FR-002) is how "least-privileged role" is represented in a set-of-roles model; it gives every membership at least one grant and keeps the role list readable.
- "Forbidden" is answered with the existing `forbidden` error code already in the organization API contract's error enum.
- Rate limiting of role changes is not added; role changes are only open to managers of the organization (GOZ-0002-IF-13 notes the absence of quotas).
- The member and invitation lists stay unpaged.
- Publisher, reviewer, approver and policy concepts are delivered by GOZ-0005, GOZ-0018, GOZ-0021 and later; this slice provides the contract they extend.
- Row-level security remains defence in depth beside application authorization and is not claimed as protection against SQL running inside an attacker's own valid request context (invariant 2).
