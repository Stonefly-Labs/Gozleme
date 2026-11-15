# Feature Specification: Local Identity and Session Security

**Feature Branch**: `feature/goz-0001-local-identity`

**Created**: 2026-11-15

**Status**: Draft

**Input**: User description: "GOZ-0001 — Build Local identity and session security as one bounded, runnable, tested vertical slice of the open-source Gözleme federated AI capability registry. User-visible outcome: Register, sign in and sign out; protected dashboard rejects anonymous access. In scope: ASP.NET Core Identity local authentication, session and account lifecycle. Important boundary: No external OIDC yet." (full prompt: `docs/prompts/spec-0001.md`, revision 2, including implementation-alignment bullets GOZ-0000-IF-01/02/03/08/10/11/13/20)

## Clarifications

### Session 2026-11-15

Maintainer decisions taken before drafting (recorded verbatim in intent; see `progress.yaml`):

- Q: Which dashboard becomes the protected surface? → A: The existing status dashboard at `/` and its data sources (the backend status endpoint and the web tier's same-origin status proxy) require sign-in. Health probes stay anonymous. The web container's health check moves to a new anonymous web liveness check. The GOZ-0000 demo scenarios sign in with a seeded synthetic account.
- Q: Registration policy for a fresh deployment? → A: Open self-registration by default; an operator setting can disable it.
- Q: Account-lifecycle features in this slice? → A: Temporary lockout after repeated failed sign-ins, and "sign out everywhere". Change password and self-service account deletion are not in this slice.
- Q: Sign-in identifier? → A: Email address, unverified in this slice. Email delivery, verification and password reset are needed later. They are not on the roadmap today, so a new MVP spec will be proposed for maintainer approval and scheduled after GOZ-0002.
- Q: (raised during implementation) When the database is down and a session therefore cannot be verified, what does the protected status surface do? → A: Fail closed. The backend answers "service unavailable" and never treats the session as valid. The dashboard shows an honest degraded view ("database unavailable, your session can't be verified") with no version, schema or other status details, which is the same information the anonymous readiness probe already exposes. The GOZ-0000 degraded and recovery scenarios are narrowed to this view.
- Q: How do users recover before password reset exists? → A: An operator-only command-line password reset and unlock, not reachable over HTTP, recorded in the security audit log with an opaque account identifier.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Register, sign in and reach the protected dashboard (Priority: P1)

A new person opens Gözleme in a browser, creates a local account with an email address and password, is signed in, and sees the system status dashboard. A returning person signs in with the same credentials. A person who is not signed in and tries to open the dashboard is sent to the sign-in page. A program that calls the protected status data without a session is refused.

**Why this priority**: This is the user-visible outcome of the spec and the prerequisite for every organization, catalog and policy feature that follows. Without an authenticated principal, nothing later can be scoped to a person.

**Independent Test**: On a fresh stack, open `/` anonymously and confirm the redirect to sign-in. Register a new synthetic account, confirm the dashboard shows live status and the signed-in account's email. Sign out, sign in again with the same credentials and reach the dashboard. Call the protected status endpoints without a session and confirm they are rejected without status data.

**Acceptance Scenarios**:

1. **Given** an anonymous browser, **When** it requests the dashboard at `/`, **Then** it is redirected to the sign-in page, and the redirect target is preserved only as a same-site relative path.
2. **Given** an anonymous client, **When** it calls the backend status endpoint or the web tier's status proxy, **Then** it receives an "unauthenticated" response that contains no status, version or database information.
3. **Given** registration is enabled, **When** a person submits a valid, not-yet-registered email address and a password meeting the policy, **Then** an account is created, the person is signed in, and the dashboard shows live status and identifies the signed-in account.
4. **Given** an existing account, **When** the person signs in with the correct email (any letter case, surrounding whitespace ignored) and password, **Then** they reach the dashboard. If they arrived from a protected page, they return to that page.
5. **Given** a registration attempt with an invalid email, a password that fails the policy, or mismatched confirmation, **When** it is submitted, **Then** it is rejected with an accessible, field-level explanation, and no account is created.
6. **Given** a signed-in session, **When** the person reloads the page or the backend service restarts, **Then** the session remains valid until it is signed out or expires.
7. **Given** health probes, **When** an anonymous client calls the backend liveness and readiness probes or the web liveness check, **Then** they respond as before. Container and orchestrator health checks keep working without credentials.

---

### User Story 2 - Sign out, and sign out everywhere (Priority: P1)

A signed-in person signs out and their session stops working at once. A person who signed in on several browsers can, from any one of them, end all of their sessions.

**Why this priority**: Sign-out is part of the stated outcome. Revoking every session is the minimum session-lifecycle control a person needs after using a shared or lost device.

**Independent Test**: Sign in from two separate browser contexts. Sign out in one and confirm only that context loses access. Sign in again, choose "sign out everywhere" in one context and confirm that both contexts now get the sign-in page and that a replay of a captured session cookie is rejected.

**Acceptance Scenarios**:

1. **Given** a signed-in session, **When** the person signs out, **Then** the session is ended on the server, the browser no longer holds a usable session, and the next dashboard request redirects to sign-in.
2. **Given** a session cookie captured before sign-out, **When** it is replayed after sign-out, **Then** the request is treated as anonymous.
3. **Given** the same account signed in from two browsers, **When** the person chooses "sign out everywhere" in one, **Then** both sessions are invalid on their next request, and the action is recorded as a security event.
4. **Given** a sign-out or sign-out-everywhere request that does not come from the Gözleme web origin (cross-site request), **When** it is received, **Then** it is rejected and the session is unaffected.

---

### User Story 3 - Resist password guessing without revealing which accounts exist (Priority: P2)

Someone attempting to guess passwords or discover registered emails gets the same generic answers whether or not an account exists, is slowed by rate limiting, and causes the targeted account to be temporarily locked after repeated failures.

**Why this priority**: Local password accounts are only acceptable with these controls. The prompt requires negative security tests.

**Independent Test**: Submit a wrong password for an existing account and a sign-in for a non-existent email and compare the responses (same message, same status, no account-specific detail). Exceed the failure threshold and confirm the account is locked, including against the correct password, until the lockout period ends. Exceed the per-client request rate and confirm throttling.

**Acceptance Scenarios**:

1. **Given** a wrong password for an existing account or any password for an unknown email, **When** sign-in is attempted, **Then** both produce the same generic failure message and response status.
2. **Given** an account, **When** 5 consecutive failed sign-ins occur, **Then** the account is locked for 15 minutes. During lockout even the correct password is refused with the same generic failure message, and a successful sign-in after lockout resets the failure count.
3. **Given** one client, **When** it exceeds the sign-in or registration request rate limit, **Then** further attempts are refused with a "too many requests" response until the window passes, without consuming account lockout attempts for unknown emails.
4. **Given** a registration attempt for an email that is already registered, **When** it is submitted, **Then** it fails with a generic message that does not confirm whether that email is registered.
5. **Given** any sign-in, registration, lockout or sign-out event, **When** logs, traces and metrics are inspected, **Then** they contain no email addresses, passwords, password hashes, session cookies or session identifiers. At most they carry an opaque internal account identifier and a bounded failure category.

---

### User Story 4 - Operator controls: close registration and recover an account (Priority: P2)

An operator can turn self-registration off for a deployment. An operator with shell access to the deployment can reset a forgotten password or clear a lockout for one account without any HTTP-exposed admin surface.

**Why this priority**: Operators need open registration off for non-demo deployments. Until email-based password reset exists, the operator command is the only recovery path.

**Independent Test**: Start the stack with registration disabled and confirm the registration page and endpoint refuse new accounts while sign-in still works. Run the operator reset command for a seeded account and confirm the old password fails, the new one works and the account's existing sessions are ended.

**Acceptance Scenarios**:

1. **Given** registration is disabled by operator configuration, **When** anyone opens the registration page or submits a registration, **Then** registration is refused with a clear "registration is closed" message, no account is created, and the sign-in page offers no registration link.
2. **Given** an operator runs the password-reset command for an account with a new password supplied through a non-echoing input or secret file, **When** it completes, **Then** the new password works, the old one fails, all of that account's existing sessions are ended and any lockout is cleared.
3. **Given** an operator runs the unlock command for a locked account, **When** it completes, **Then** the account can sign in again immediately.
4. **Given** either operator command, **When** it runs, **Then** it is recorded as a security audit event with the opaque account identifier and outcome, and never echoes the password or prints the account email.
5. **Given** any HTTP client, **When** it attempts to reach password-reset, unlock or user-listing functionality over HTTP, **Then** no such endpoint exists.

---

### User Story 5 - Cumulative demo and the no-external-identity boundary (Priority: P3)

A reviewer runs one documented demo command for this spec and sees an autonomous browser walkthrough: anonymous rejection, registration, sign-in, sign-out, sign-out-everywhere, lockout, closed registration and the trace of a sign-in in the trace viewer, with screenshots. All earlier GOZ-0000 demo scenarios still pass, now signed in as a seeded synthetic account. The demo also shows that no external identity provider is offered or required.

**Why this priority**: Required for evidence-backed delivery. It depends on stories 1–4 working.

**Independent Test**: Run the demo for GOZ-0001 from a clean checkout. It starts the stack, migrates, seeds synthetic accounts, runs the GOZ-0000 and GOZ-0001 browser suites and writes a run record and screenshots under this spec's evidence directory.

**Acceptance Scenarios**:

1. **Given** a clean checkout, **When** the reviewer runs the demo for this spec, **Then** prior specs' demo suites and this spec's suite run in order and the run record reports each result.
2. **Given** the default configuration, **When** the stack runs, **Then** it starts and authenticates local accounts with no network access to any external identity provider. The sign-in page shows no external/single-sign-on option, and requests to typical external sign-in or callback paths return "not found".
3. **Given** the demo seed command, **When** it runs twice, **Then** it produces the same synthetic accounts without duplicates and without changing the database schema.

---

### Edge Cases

- Email case and whitespace: `Alice@Example.test ` and `alice@example.test` are the same account. Registering the second form of an existing email is a duplicate.
- Very long or malformed input (oversized email or password fields, control characters, non-UTF-8): rejected with validation errors, never a server error, and never logged verbatim.
- Open-redirect attempt: a sign-in "return to" value that is absolute, protocol-relative or points off-site is ignored and the user lands on the dashboard.
- Already signed in: visiting sign-in or registration while signed in goes to the dashboard rather than creating a second account or session.
- Session expiry: an idle or absolute-expired session behaves as anonymous. A dashboard that was already open shows a "session expired, sign in again" state instead of status data or a generic error.
- Concurrent sign-out-everywhere during an in-flight request: the next request after revocation is anonymous. No request that starts after revocation is served as the user.
- Backend unreachable or database down while signed out: sign-in reports a generic "service unavailable, try again" message, distinct from "wrong credentials", and leaks no internals.
- Database down while signed in: the session cannot be verified, so it is not honoured (fail closed). The dashboard shows "Degraded: database unavailable" and states that the session can't be verified, without version, schema or other status data. It recovers to the full signed-in dashboard when the database returns, without signing in again. Backend unreachable while signed in still shows the GOZ-0000 "unreachable" state.
- Lockout of an email that does not exist: the response matches that of an existing locked account. No account row is created.
- Registration disabled after accounts exist: existing accounts still sign in.
- Operator reset for an unknown email: the command fails with a non-zero exit and does not create an account.
- Browser telemetry from the sign-in and registration pages: it carries no form values. The telemetry ingestion route accepts only same-origin, bounded, rate-limited submissions.

## Requirements *(mandatory)*

### Functional Requirements

**Accounts and registration**

- **FR-001**: The system MUST let a person create a local account with an email address and a password when registration is enabled.
- **FR-002**: The system MUST normalize email addresses (trim, case-insensitive comparison) and enforce one account per normalized email.
- **FR-003**: The system MUST enforce a password policy of at least 12 and at most 128 characters, with no mandatory character-composition rules. It MUST reject a password equal to the email address or one on a bundled list of common passwords.
- **FR-004**: The system MUST store only salted, adaptive one-way password hashes. Plaintext passwords MUST never be stored, logged, traced or returned.
- **FR-005**: The system MUST record email addresses as unverified in this slice and MUST NOT present them as verified anywhere.
- **FR-006**: Operators MUST be able to disable self-registration through deployment configuration (Compose environment and Helm values). Registration MUST default to enabled.
- **FR-007**: Registration of an already-registered email MUST fail with a generic message that does not confirm the email is registered.

**Sign-in and sessions**

- **FR-008**: The system MUST authenticate a person by email and password and establish a session only after successful verification.
- **FR-009**: A session MUST be carried by a cookie that is HTTP-only, scoped to the Gözleme web origin, `SameSite` restricted, and marked `Secure` whenever the site is served over HTTPS. No session or bearer token may be readable by page scripts or stored in browser storage.
- **FR-010**: The system MUST issue a new session identifier on every successful sign-in (session fixation protection).
- **FR-011**: Sessions MUST expire after 30 minutes of inactivity (sliding) and after an absolute maximum of 12 hours, whichever comes first.
- **FR-012**: Sessions MUST be validated on the server on every protected request against current account security state. Signing out, signing out everywhere and an operator password reset MUST take effect on the next request.
- **FR-013**: Sessions MUST remain valid across backend restarts and across multiple backend replicas. The keys that protect session cookies MUST be stored durably outside the container filesystem and MUST NOT be checked into the repository or placed in chart values.
- **FR-014**: State-changing identity actions (register, sign in, sign out, sign out everywhere) MUST reject cross-site requests (origin verification and/or anti-forgery token).
- **FR-015**: A sign-in "return to" target MUST be accepted only if it is a same-site relative path. Otherwise the person lands on the dashboard.

**Sign-out and lifecycle**

- **FR-016**: The system MUST let a signed-in person sign out, ending that session on the server and clearing the browser cookie.
- **FR-017**: The system MUST let a signed-in person sign out everywhere, invalidating every session for their account.
- **FR-018**: After 5 consecutive failed sign-ins, the system MUST lock the account for 15 minutes. While locked, sign-in MUST fail generically even with the correct password. A successful sign-in MUST reset the failure counter.
- **FR-019**: Sign-in and registration MUST be rate limited per client address. Throttled requests receive a "too many requests" response. Limits MUST be operator-configurable with safe defaults.
- **FR-020**: Sign-in failures for wrong password, unknown email and locked account MUST be indistinguishable to the caller in message and status.

**Protected surfaces**

- **FR-021**: The status dashboard at `/`, the backend status endpoint and the web tier's status proxy MUST require an authenticated session. Anonymous browser navigation MUST redirect to sign-in. Anonymous API calls MUST receive an "unauthenticated" response without status data.
- **FR-021a**: When the database is unreachable, the system MUST NOT treat any session as valid. Protected requests and identity actions MUST receive a "service unavailable" response with no status data and no internals. The dashboard MUST present this as a degraded, database-unavailable state.
- **FR-022**: Backend liveness and readiness probes MUST remain anonymous. The web tier MUST expose an anonymous liveness check, which container health checks MUST use.
- **FR-023**: The signed-in dashboard MUST show which account is signed in and offer "sign out" and "sign out everywhere".
- **FR-024**: The system MUST NOT expose any HTTP endpoint that lists users or that resets passwords or unlocks accounts.
- **FR-025**: Browser access to the backend MUST remain same-origin through the web tier (no cross-origin resource sharing; backend not browser-exposed). Session authentication MUST work through this pattern.

**Operator recovery**

- **FR-026**: The system MUST provide operator-only one-shot commands, run in the deployment the same way as the existing migration command, to (a) reset an account's password and (b) clear an account's lockout. Both MUST be identified by email.
- **FR-027**: The reset command MUST take the new password through a non-echoing prompt, standard input or a secret file (never a command-line argument), apply the password policy, clear lockout and invalidate all existing sessions for the account.

**Boundary: no external identity**

- **FR-028**: The system MUST NOT offer, configure, call or depend on any external OpenID Connect, OAuth or SAML identity provider in this slice. Typical external sign-in and callback paths MUST return "not found", and the sign-in page MUST show no external sign-in option.

**Security events, observability and privacy**

- **FR-029**: The system MUST emit a structured security audit event for registration, sign-in success, sign-in failure (by category), lockout, sign-out, sign-out-everywhere, registration refused, operator reset and operator unlock. Each event carries only an opaque internal account identifier (when known), the outcome, a bounded category and the trace correlation.
- **FR-030**: The system MUST instrument registration, sign-in, sign-out and sign-out-everywhere with trace spans and outcome counters (by bounded failure category) through the existing single telemetry pipeline. It MUST preserve the single browser → web → backend → database trace.
- **FR-031**: Emails, passwords, password hashes, cookies, session identifiers and anti-forgery tokens MUST NOT appear in span names, attributes, events, metric labels, logs, URLs, error responses or screenshots.
- **FR-032**: The browser telemetry ingestion route MUST accept only same-origin submissions within the existing size bounds and MUST be rate limited per client. It MAY accept submissions from anonymous pages (sign-in, registration) because those pages are instrumented too.

**Persistence, compatibility and demo**

- **FR-033**: Identity data MUST be created through a new forward, backward-compatible schema migration in the `gozleme` schema, applied only by the existing migration command, and accessed at runtime only through the least-privileged application role.
- **FR-034**: Accounts in this slice are global principals, not tenant-owned rows. No organization or tenant data is stored. Tenant scoping and row-level security arrive in GOZ-0002/GOZ-0003.
- **FR-035**: A reusable, deterministic, idempotent synthetic seed command, separate from schema migrations, MUST create the demo accounts. Seed credentials MUST be throwaway local values supplied through environment configuration (defaulted in the example environment file), never real credentials.
- **FR-036**: The demo runner MUST take the spec as a parameter, write each spec's run record and screenshots to that spec's evidence directory, and run prior specs' demo suites cumulatively. GOZ-0000's four status scenarios MUST keep passing while signed in as a seeded account.
- **FR-037**: GOZ-0000 boundary tests MUST be narrowed only where this spec supersedes them (sign-in page, authentication endpoints, presence of identity tables). The remaining assertions (for example, no user-listing endpoint) MUST be kept, and a new boundary test for "no external identity" MUST be added.
- **FR-038**: All new pages MUST meet the accessibility bar established in GOZ-0000: labelled form fields, keyboard operability, visible focus, announced errors and zero automated accessibility violations.

### Key Entities *(include if feature involves data)*

- **Account (local user)**: A global person principal. It has an opaque permanent internal identifier, a normalized unique email (unverified), a password hash, a security stamp that changes on password reset and sign-out-everywhere, a failed-attempt counter, a lockout end time, and creation and last-sign-in times. It holds no organization membership in this slice.
- **Session**: The authenticated browser state bound to one account and the account's current security stamp, with sliding and absolute expiry. It is represented only by a protected, HTTP-only cookie and validated server-side.
- **Session protection key ring**: Durable cryptographic keys used to protect session cookies and anti-forgery tokens. They are shared by all backend replicas, survive restarts and never leave the server side.
- **Security audit event**: An append-only record of an identity event, with time, event type, outcome, bounded category, opaque account identifier (optional) and trace correlation. It holds no personal data and no secrets.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A new person can register and reach the protected dashboard in under 60 seconds, and a returning person can sign in in under 15 seconds on the local stack.
- **SC-002**: 100% of protected pages and status endpoints reject anonymous access in automated tests. 100% of health probes remain reachable anonymously.
- **SC-003**: After sign-out, sign-out-everywhere or an operator reset, 100% of affected sessions (including replayed cookies) are rejected on their next request.
- **SC-004**: In automated tests, responses for wrong-password, unknown-email and locked-account sign-ins are identical in message and status, and duplicate registration does not reveal account existence.
- **SC-005**: The 6th consecutive failed sign-in for an account is refused due to lockout. A correct sign-in after the 15-minute lockout succeeds.
- **SC-006**: A scan of all logs, traces and metrics captured during the demo finds zero emails, passwords, cookies or session identifiers.
- **SC-007**: Sessions survive a backend restart: a session created before the restart is still valid after it.
- **SC-008**: The cumulative demo (GOZ-0000 + GOZ-0001 suites) passes autonomously from a clean checkout with no external identity provider or cloud service reachable, and produces sanitized screenshots of the sign-in, registration, dashboard, sign-out, lockout and closed-registration states.
- **SC-009**: All new pages report zero automated accessibility violations.

## Assumptions

- Email addresses are unverified identifiers in this slice. Email delivery, email verification and self-service password reset belong to a new MVP spec, proposed in this spec's pull request for maintainer approval and scheduled after GOZ-0002. Until then, recovery is operator-only (FR-026).
- Change password and self-service account deletion are deferred. Deletion interacts with organization membership (GOZ-0002) and retention rules.
- Multi-factor authentication, external OIDC (planned later in the roadmap), passkeys and "remember me" persistent sessions are out of scope.
- Administrative roles do not exist yet (RBAC is GOZ-0004). Operator recovery is therefore a deployment-shell command, not an in-app admin feature.
- Accounts are global, so security invariant 1 (tenant-owned rows under RLS) does not apply to the account table in this slice. GOZ-0003 introduces RLS for tenant-owned data.
- Local Compose is served over plain HTTP on `127.0.0.1`. Cookies are therefore not marked `Secure` locally, but they are under HTTPS deployments. This is documented, not hidden.
- Rate limiting is per backend instance in this slice. Distributed rate limiting across replicas is not claimed. Account lockout is stored in the database and therefore applies across replicas.
- Defaults (12-character minimum password, 5 failures → 15-minute lockout, 30-minute idle / 12-hour absolute session) follow current NIST SP 800-63B and OWASP session-management guidance and are operator-tunable within safe bounds.
