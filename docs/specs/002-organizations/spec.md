# Feature Specification: Organizations and Membership

**Feature Branch**: `feature/goz-0002-organizations`

**Created**: 2026-11-15

**Status**: Draft

**Input**: User description: "GOZ-0002 — Organizations and membership. Build Organizations and membership as one bounded runnable tested vertical slice: create two organizations, switch context and manage memberships. In scope: org identity, role-neutral membership, invitation or controlled local add. Boundary: do not build full administrative delegation." (full prompt: `docs/prompts/spec-0002.md`, revision 3, including implementation-alignment bullets GOZ-0000-IF-01/03/11 and GOZ-0001-IF-01/02/03/04/05/06/09/10/11/17/18)

## Clarifications

### Session 2026-11-15

Maintainer decisions taken before drafting (recorded in `progress.yaml`):

- Q: How does someone become a member of an organization? → A: Through a single-use invitation code. A member creates a short-lived code and shares it out of band. A signed-in person redeems it to join. The code is stored only as a hash and can be revoked. No step reveals whether an account exists. Direct "add by email" is not in this slice.
- Q: Membership is role-neutral until GOZ-0004. What may any member do? → A: Every member can create invitations and can remove any other member, and any member can leave. The last remaining member cannot leave or be removed. This is documented as an interim rule that GOZ-0004 restricts with roles.
- Q: Who may create organizations? → A: Any signed-in account. The creator becomes the first member. There is no operator switch.
- Q: What identifies an organization? → A: A permanent opaque identifier and an editable display name that need not be unique. Globally unique handles and namespaces are left to GOZ-0005.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create organizations and switch between them (Priority: P1)

A signed-in person creates an organization by giving it a display name and becomes its first member. They create a second organization the same way. A context switcher shows only the organizations they belong to. They switch between the two, and the page shows which organization is active. They can rename an organization they belong to.

**Why this priority**: This is the core of the stated outcome. Every later tenant-scoped feature (RLS, roles, catalog overlays, policy) needs an organization and an active-organization context to scope to.

**Independent Test**: Sign in as a synthetic account, create two organizations, confirm both appear in the switcher, switch to each in turn and confirm the active organization changes in the UI and in the API's view of the current context. Sign in as a second synthetic account and confirm neither organization is listed or reachable.

**Acceptance Scenarios**:

1. **Given** a signed-in account, **When** it submits a valid display name, **Then** a new organization is created with a permanent opaque identifier, the account is its only member, and the organization becomes the active context.
2. **Given** an account that belongs to two organizations, **When** it opens the context switcher, **Then** it sees exactly those two organizations and no others.
3. **Given** an account that belongs to two organizations, **When** it switches to the other one, **Then** the active organization changes, and the next page or API request uses the new context.
4. **Given** an account that is not a member of an organization, **When** it tries to select, view or act in that organization by its identifier, **Then** the request is refused with the same "not found" response it would get for an identifier that does not exist.
5. **Given** a member, **When** it renames the organization, **Then** the new display name is shown to every member, and the identifier does not change.
6. **Given** two organizations with the same display name, **When** both exist, **Then** both are allowed and are told apart by their identifiers.
7. **Given** a display name that is empty, only whitespace, or longer than the limit, **When** it is submitted, **Then** it is rejected with an accessible, field-level explanation, and nothing is created or changed.

---

### User Story 2 - Invite someone with a single-use code (Priority: P1)

A member of an organization creates an invitation and gets a code to share out of band (for example in chat). A second person, signed in with their own account, enters the code and joins the organization. The code then stops working. A member can see the organization's open invitations and revoke one before it is used.

**Why this priority**: Membership changes are part of the stated outcome. Emails are unverified (GOZ-0001), so a secret code shared out of band is the only safe way in this slice to prove that a particular person was invited.

**Independent Test**: As member A, create an invitation and copy its code. As account B, redeem it and confirm B now sees the organization. Try the same code again as account C and confirm it is refused. Create and revoke another invitation and confirm the revoked code is refused. Confirm an expired code is refused.

**Acceptance Scenarios**:

1. **Given** a member, **When** it creates an invitation, **Then** it receives a code that is shown only once, and the invitation is listed as open, with its creation and expiry time, to every member of that organization.
2. **Given** a valid open code, **When** a signed-in non-member redeems it, **Then** that account becomes a member, the invitation is used up, and the organization appears in the account's switcher.
3. **Given** a code that has been used, revoked or has expired, or a code that never existed, **When** anyone redeems it, **Then** the request is refused with one generic "invalid or expired invitation" response that does not say which of these is the case or which organization it was for.
4. **Given** a signed-in account that is already a member, **When** it redeems a valid code for that organization, **Then** it stays a member, nothing is duplicated, and the code is not used up.
5. **Given** an open invitation, **When** a member revokes it, **Then** the code can no longer be redeemed.
6. **Given** an anonymous visitor, **When** they open the redemption page or call redemption, **Then** they are sent to sign in (page) or refused as unauthenticated (API), and the code is not used up.
7. **Given** an invitation list, **When** it is shown, **Then** it never shows the code itself again.

---

### User Story 3 - See and manage the member list (Priority: P2)

A member opens the organization's member list and sees who belongs. Under the interim role-neutral rule, any member can remove any other member, and anyone can leave. Removal takes effect on the removed person's next request, including any session they already have open. The last member can neither leave nor be removed.

**Why this priority**: Completes "manage memberships". Removal that takes effect immediately is required so that membership is real authority, not a cached label.

**Independent Test**: With A and B as members, A removes B. In B's already-open browser, the next request for that organization is refused and the organization disappears from B's switcher. B is not signed out. A tries to leave as the last member and is refused.

**Acceptance Scenarios**:

1. **Given** a member, **When** it opens the member list, **Then** it sees every current member, with each member's email and join date, and nothing else about their accounts.
2. **Given** members A and B, **When** A removes B, **Then** on B's next request (in any session) B can no longer view or act in that organization, the organization disappears from B's switcher, and B's other organizations and sessions are unaffected.
3. **Given** a member that belongs to other organizations, **When** it leaves one, **Then** it loses access to that one only, and its active context moves to another organization or to "none".
4. **Given** an organization with a single member, **When** that member tries to leave or a request tries to remove it, **Then** the request is refused with an explanation, and the membership remains.
5. **Given** a removed member, **When** it redeems a new, valid invitation, **Then** it can rejoin.
6. **Given** a membership change, **When** it happens, **Then** it is recorded as a security event that names the actor, the affected account and the organization only by opaque identifiers.

---

### User Story 4 - Context is a preference, never authority (Priority: P2)

The selected organization is a convenience for the user interface. Every request that acts in an organization is checked against current membership, whatever context the client claims to have selected.

**Why this priority**: GOZ-0003 builds RLS on top of this slice. If a stored or client-supplied selection were trusted, a removed member or a tampered request could act in an organization it does not belong to.

**Independent Test**: As account B, which is not a member of organization X, send requests naming X as the selected context or target, including a tampered client-side selection. Confirm every request is refused as "not found" and nothing about X is revealed. Remove a member while its selected context still points at the organization, and confirm it is refused on the next request.

**Acceptance Scenarios**:

1. **Given** a selected context that names an organization the account does not belong to (tampered, stale or removed), **When** any organization-scoped request is made, **Then** it is refused as "not found", and the context falls back to one of the account's real organizations, or to "none".
2. **Given** an account with no organizations, **When** it signs in, **Then** it sees an empty state that offers to create an organization or redeem an invitation.
3. **Given** a cross-site request (not from the Gözleme web origin) that tries to create, rename, invite, redeem, revoke, remove, leave or switch, **When** it is received, **Then** it is rejected, and nothing changes.

---

### User Story 5 - Cumulative demo and the no-delegation boundary (Priority: P3)

The maintainer runs the cumulative demo. It drives the GOZ-0000 and GOZ-0001 scenarios and then the organizations walkthrough, and it captures screenshots and telemetry evidence automatically. The demo also proves that no administrative delegation exists: there is no way to grant another member any extra power, and every member has exactly the same abilities.

**Why this priority**: Required by the demo contract and by the stated boundary. It must not regress earlier specs.

**Independent Test**: Run the single demo command on a fresh stack. Confirm the earlier suites pass, two organizations are created and switched, an invitation is redeemed, a member is removed and loses access, and a member's attempt to grant a role, ownership or admin power has no endpoint, control or effect.

**Acceptance Scenarios**:

1. **Given** a fresh stack, **When** the demo command runs, **Then** the GOZ-0000, GOZ-0001 and GOZ-0002 walkthroughs pass, and screenshots of each organizations step are saved with only synthetic data.
2. **Given** the API and the UI, **When** they are inspected for role assignment, ownership transfer, administrator grants or delegated permissions, **Then** none exists, and attempts to send such a field or call such an operation are rejected or ignored without effect.
3. **Given** the demo's captured traces, metrics and logs, **When** they are searched for emails, display names of organizations, invitation codes and session values, **Then** none appears. Organizations, accounts and invitations appear only as opaque identifiers.

---

### Edge Cases

- Two members remove each other, or the last two members both leave, at the same moment: at least one member always remains, and the organization is never left with no members.
- Two accounts redeem the same code at the same moment: exactly one succeeds; the other gets the generic refusal.
- A code is entered with surrounding whitespace or different letter case: it is normalized before checking, if the code format is case-insensitive.
- Repeated guessing of invitation codes: redemption attempts per account are rate limited, and codes have enough entropy that guessing is not feasible within that limit.
- An account belongs to many organizations: the switcher still lists all of them in a stable order (by display name, then identifier).
- A member is removed while its context still points at that organization: the next request falls back without error.
- The database is unreachable: every organizations endpoint fails closed with "service unavailable", and the UI shows an honest degraded view without organization details (GOZ-0001 behaviour).
- An organization identifier that is not well-formed is supplied: it is refused with the same "not found" response as a well-formed one that does not exist.
- A display name contains markup or control characters: it is shown as text and never interpreted. Control characters are rejected.
- The account that created an organization is removed: the organization keeps working with its remaining members, because creation grants no special standing.

## Requirements *(mandatory)*

### Functional Requirements

**Organization identity and context**

- **FR-001**: Any signed-in account MUST be able to create an organization by giving a display name. The system MUST assign it a permanent, opaque, non-guessable identifier, and MUST make the creator its first member in the same step.
- **FR-002**: A display name MUST be 1–100 characters after trimming surrounding whitespace, MUST NOT contain control characters, and need not be unique.
- **FR-003**: Any member MUST be able to rename an organization. The identifier MUST never change.
- **FR-004**: The system MUST list, for the signed-in account, exactly the organizations it currently belongs to, in a stable order.
- **FR-005**: An account MUST be able to select one of its organizations as the active context, and MUST be able to have no active context. The selection MUST be treated as an untrusted preference: it MUST NOT be stored as authority in the session, and every organization-scoped request MUST re-check current membership in the database.
- **FR-006**: A request that names an organization the account does not belong to, a malformed identifier, or an identifier that does not exist MUST receive the same "not found" response, with no difference in content that reveals whether the organization exists.

**Invitations**

- **FR-007**: Any member MUST be able to create an invitation for its organization. The system MUST return a high-entropy code once, and MUST store only a one-way hash of it.
- **FR-008**: An invitation MUST expire 7 days after creation, MUST be redeemable at most once, and MUST be revocable by any member before it is used.
- **FR-009**: A signed-in account MUST be able to redeem a code to become a member. Redemption by an account that is already a member MUST leave the membership unchanged and MUST NOT use up the code.
- **FR-010**: A used, revoked, expired or unknown code MUST get one generic refusal that does not reveal which case applies or which organization the code belonged to.
- **FR-011**: Redemption attempts MUST be rate limited per account, and the limit MUST be exceeded long before a code could feasibly be guessed.
- **FR-012**: Members MUST be able to list their organization's open invitations (creation time, expiry, creator). The code MUST never be shown again after creation.
- **FR-013**: The system MUST NOT match, accept or offer an invitation because of an email address, and MUST NOT provide any lookup that reveals whether an account with a given email exists.

**Membership**

- **FR-014**: Any member MUST be able to see the organization's current members, showing each one's email and join date only.
- **FR-015**: Any member MUST be able to remove any other member, and any member MUST be able to leave. This is an interim role-neutral rule that GOZ-0004 replaces with roles.
- **FR-016**: The system MUST refuse to remove or let leave the last remaining member, including under concurrent requests.
- **FR-017**: Removal MUST take effect on the removed account's next request in every session, without signing it out and without affecting its other organizations.
- **FR-018**: Membership MUST be role-neutral: the system MUST NOT store or honour any role, ownership, administrator flag or delegated permission, and the creator MUST have no special standing.
- **FR-019**: Organizations, memberships and invitations MUST carry an explicit organization key, so that GOZ-0003 can enable row-level security on them without a breaking data change.

**Security, boundaries and protection**

- **FR-020**: Every new page MUST require a signed-in session (anonymous visitors are sent to sign in), and every new API operation MUST require authentication. No new operation is anonymous.
- **FR-021**: Every state-changing operation (create, rename, switch, invite, revoke, redeem, remove, leave) MUST reject cross-site requests.
- **FR-022**: When the database is unreachable, every new operation MUST fail closed with "service unavailable".
- **FR-023**: Organization creation, rename, invitation creation, revocation and redemption, removal and leaving MUST each be recorded as a security event that names accounts, organizations and invitations only by opaque identifiers.
- **FR-024**: The system MUST NOT provide account deletion, organization deletion, ownership transfer, roles, administrator grants, delegated administration, email delivery or "add by email". The existing account-listing boundary MUST stay absent.
- **FR-025**: Earlier boundary tests that this slice supersedes (the organizations endpoint and page returning "not found", and the home page having no navigation links) MUST be narrowed to what remains true. The table-inventory check MUST be extended with the new tables, not dropped.

**Observability and demo**

- **FR-026**: Organization creation, context switch, invitation and membership changes MUST each produce a trace span and a metric in the existing telemetry pipeline, joined to the single browser-to-database trace. Spans, attributes, metric labels and logs MUST contain only opaque identifiers, never emails, display names, codes or session values. Metric labels MUST have bounded cardinality.
- **FR-027**: The cumulative demo MUST add an organizations suite that runs after the earlier suites, uses idempotently seeded synthetic accounts and organizations, captures screenshots of each step and checks captured telemetry for secrets and personal data.

### Key Entities *(include if feature involves data)*

- **Organization**: A tenant. Permanent opaque identifier, display name, creation time and the identifier of the creating account (kept for audit only, with no authority). No deletion or lifecycle states in this slice.
- **Membership**: Links one account to one organization. Carries the organization key, the account identifier, the time joined and how the account joined (creator or invitation). One active membership per account and organization. No role.
- **Invitation**: Belongs to one organization. Stores the hash of its code, the creating member, creation and expiry time, and when and by whom it was used or revoked. Never stores the code itself.
- **Context preference**: The account's last selected organization. It is a convenience with no authority, re-validated on every request.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A signed-in person can create two organizations and switch between them in under 1 minute in total, with each action finishing in under 1 second on the local demo stack.
- **SC-002**: A second person can join through an invitation code in under 30 seconds from receiving the code.
- **SC-003**: 100% of tested cross-organization attempts (view, rename, invite, revoke, remove, list members and switch, by a non-member, a removed member and a tampered context) are refused with a response identical to "does not exist".
- **SC-004**: A removed member loses access on its very next request in 100% of tested sessions.
- **SC-005**: 100% of used, revoked, expired and unknown codes are refused with the same response.
- **SC-006**: 0 occurrences of emails, organization display names, invitation codes or session values in captured traces, metrics and logs from the demo run.
- **SC-007**: The cumulative demo runs from one command on a fresh stack, and all earlier suites stay green.

## Assumptions

- Accounts are the global principals from GOZ-0001. This slice adds no account fields.
- Showing co-members' emails in the member list to other members is acceptable, because members chose to join through a code they received out of band. Emails appear only there, never in URLs, telemetry or audit events. Screenshots use synthetic `@demo.gozleme.test` accounts only.
- Invitations expire after 7 days. This is a reasonable default for codes shared by hand and is configurable by an operator.
- An invitation admits exactly one person. Multi-use links are out of scope.
- Organization deletion and lifecycle states (R3-24) and delegated administration (R3-08) are post-MVP. Roles are GOZ-0004. RLS and transaction-scoped tenant context are GOZ-0003. Email delivery is GOZ-0040. Publisher namespaces and unique handles are GOZ-0005.
- Until GOZ-0003 enables row-level security, organization isolation in this slice is enforced by application authorization only. This spec does not claim RLS protection.
- Membership removal does not end the removed account's sessions, because ending sessions is reserved for "sign out everywhere" (GOZ-0001-IF-04).
