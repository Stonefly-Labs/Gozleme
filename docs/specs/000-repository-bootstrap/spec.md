# Feature Specification: Repository Bootstrap and Executable Engineering Foundation

**Feature Branch**: `chore/goz-0000-bootstrap`

**Created**: 2026-11-14

**Status**: Draft

**Input**: User description: "GOZ-0000 — Build Repository bootstrap and executable engineering foundation as one bounded, runnable, tested vertical slice of the open-source Gözleme federated AI capability registry. User-visible outcome: Launch Compose and see Next.js dashboard reading real API and PostgreSQL status. In scope: Monorepo, .NET 10, Next.js, Compose, CI, Git rules, Claude skills and agents, docs and registry tooling. Important boundary: Do not implement product identity or catalog prematurely." (full prompt: `docs/prompts/spec-0000.md`, revision 1)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See a live system status dashboard after one local start command (Priority: P1)

A contributor or operator clones the repository and runs one documented command that starts the complete local stack (web dashboard, backend service and database) with no cloud account, hosted identity provider or hosted AI/embedding service. They open the dashboard in a browser and see the live status of the backend service and the database, including the applied database schema version, read from the running system rather than hard-coded.

**Why this priority**: This is the foundation every later spec builds on and demonstrates. Without a runnable cumulative stack, no later vertical slice can be demonstrated or reviewed.

**Independent Test**: From a clean checkout on a machine with only a container runtime installed, run the documented start command, wait for readiness, open the dashboard URL and confirm that it shows the backend as healthy and the database as reachable with a schema version that matches the applied migration.

**Acceptance Scenarios**:

1. **Given** a clean checkout and a running container runtime, **When** the contributor runs the documented start command, **Then** the web dashboard, backend service and database start and report ready without any network access to cloud services.
2. **Given** the stack is running, **When** the contributor opens the dashboard, **Then** it shows the backend service as healthy, the database as reachable, the current applied schema version, the service version/build identifier and the time the status was last checked.
3. **Given** the stack is running, **When** a client calls the backend's status endpoint directly, **Then** it returns a machine-readable status document whose values match what the dashboard displays.
4. **Given** the stack is started for the first time, **When** the backend starts, **Then** it applies the foundation database migration automatically and the migration history records it exactly once, even after restart.

---

### User Story 2 - See an honest degraded state when a dependency fails (Priority: P1)

When the database becomes unavailable, or the backend service itself is unreachable, the dashboard shows a clearly labelled degraded or unavailable state instead of stale "healthy" information or a blank page, and recovers automatically when the dependency returns.

**Why this priority**: The prompt explicitly requires a demonstrated failure state; an operator dashboard that cannot show failure is not trustworthy, and later specs rely on honest readiness reporting.

**Independent Test**: With the stack running, stop the database container; the backend readiness check and dashboard both show the database as unavailable within the refresh interval while the backend liveness remains healthy. Restart the database; the dashboard returns to healthy. Separately, stop the backend; the dashboard shows the backend as unreachable.

**Acceptance Scenarios**:

1. **Given** a healthy stack, **When** the database is stopped, **Then** within one refresh interval the dashboard shows the database as "unavailable" and the overall status as "degraded", and the backend readiness probe reports not-ready while its liveness probe stays alive.
2. **Given** the database was stopped, **When** it is started again, **Then** the dashboard returns to "healthy" without restarting the backend or the web dashboard.
3. **Given** a healthy stack, **When** the backend service is stopped, **Then** the dashboard shows the backend as "unreachable" and does not display stale database status as current.
4. **Given** any failure state, **When** the dashboard or status endpoint describes it, **Then** no connection strings, passwords, hostnames-with-credentials, stack traces or other secrets appear in the UI, the status response or logs.

---

### User Story 3 - Contribute safely through a governed repository (Priority: P2)

A contributor (human or coding agent) finds standard repository files — licence, readme, contributing guide, security policy, editor and line-ending rules, ignore rules, pull-request template, code-owner placeholder instructions — plus standard development commands (build, test, lint, start, stop, demo) and continuous-integration checks that run the same tests on every pull request. Branch and merge rules are documented, and any protected-main configuration is either applied with evidence or explicitly flagged for human verification; it is never claimed without proof.

**Why this priority**: Required for all future contribution, but the running dashboard (P1) is the demonstrable outcome.

**Independent Test**: Run each documented development command locally and see it succeed; open a pull request from an allowed branch and see the CI checks execute the backend tests, frontend tests, documentation/registry validation and container build.

**Acceptance Scenarios**:

1. **Given** a fresh checkout, **When** the contributor runs the documented build and test commands, **Then** backend and frontend unit and integration tests execute and pass.
2. **Given** a pull request from a `chore/*`, `feature/*` or `bugfix/*` branch, **When** CI runs, **Then** it runs backend tests, frontend tests, registry/documentation validation (including generated register in sync), branch-name validation and container image builds, and reports pass/fail.
3. **Given** a pull request from any other branch name, **When** CI runs branch-name validation, **Then** the check fails with an explanatory message.
4. **Given** the repository's GitHub settings, **When** the bootstrap is delivered, **Then** documentation states exactly which protected-main rules are required, records whether they were verified as applied (with evidence) or are pending human configuration, and never states they are enforced without verification.

---

### User Story 4 - Agent workflows and roadmap tooling are functional (Priority: P3)

A coding agent working on later specs finds functional skills and agent definitions, an authoritative project manifest and spec registry, and tooling that validates the registry and regenerates the human-readable spec register deterministically.

**Why this priority**: Already largely present from prior grounding work; this spec verifies they function and wires their validation into CI.

**Independent Test**: Run the registry validation and register generation commands; confirm validation passes and regenerating the register produces no diff.

**Acceptance Scenarios**:

1. **Given** the registry and manifest, **When** the validation command runs, **Then** schemas validate, prompt paths exist, IDs are unique and dependencies are acyclic.
2. **Given** an unchanged registry, **When** the register is regenerated, **Then** the output is byte-identical to the committed human register.

---

### User Story 5 - Follow one request end to end through telemetry (Priority: P1)

An operator or contributor opens the local trace viewer and sees a single distributed trace for a dashboard refresh that threads through the web tier, the backend and the database, alongside request metrics and correlated structured logs. Every later spec extends this same telemetry thread rather than adding observability afterwards.

**Why this priority**: Decided by the maintainer on 2026-11-14 (GOZ-0000-IF-11): observability is foundational and must run through all Gözleme work from the first slice.

**Independent Test**: With the stack running, load the dashboard, then query the local trace store for the web service and find one trace containing web, backend and database spans; query the local metrics endpoint and find backend request and status-check metrics.

**Acceptance Scenarios**:

1. **Given** the stack is running, **When** the dashboard refreshes, **Then** one trace links the web route, the backend status request and the database probe query, viewable in the local trace viewer.
2. **Given** the stack is running, **When** the metrics endpoint is scraped, **Then** backend HTTP request duration and status-check counts (tagged by outcome) are present.
3. **Given** the database is stopped, **When** status is checked, **Then** the probe span is marked as an error and the status-check metric records an unavailable outcome.
4. **Given** any telemetry, **When** it is inspected, **Then** it contains no passwords, connection strings or credential material.
5. **Given** the telemetry collector or trace store is down, **When** the dashboard is used, **Then** the application remains healthy; telemetry loss never affects health or requests.

---

### Edge Cases

- Database not yet accepting connections when the backend starts: the backend retries/waits and reports not-ready rather than crashing permanently; once the database is ready, migrations apply and readiness turns healthy.
- Migration applied twice (restart, or two backend instances): migration history remains a single record; no duplicate schema changes.
- Backend reachable but returns malformed or slow status: the dashboard shows "unknown/unreachable" after a bounded timeout rather than hanging.
- Dashboard rendered while backend is down at first load: the page still renders with the unreachable state, not an error page.
- Requests to product endpoints that do not exist yet (identity, organizations, catalog, search): return "not found" and no placeholder data.
- Optional vector extension absent from the database image: the stack still starts and reports healthy; the status reports the optional extension as "not installed / not enabled" without degrading overall health.
- Port conflicts on the host: documented configuration allows overriding host ports without editing source.

## Requirements *(mandatory)*

### Functional Requirements

**Runnable stack and status**

- **FR-001**: The repository MUST provide a single documented command that starts the web dashboard, backend service and database locally with deterministic configuration and no dependency on cloud services, hosted identity providers or hosted AI/embedding providers.
- **FR-002**: The backend MUST expose a liveness signal (process is up) separately from a readiness signal (dependencies, including the database, are usable).
- **FR-003**: The backend MUST expose a machine-readable, versioned status document containing: overall status (healthy/degraded/unavailable), service name, service version/build identifier, per-dependency status for the database (reachable, latency, applied schema version, optional vector extension availability), and the check timestamp.
- **FR-004**: The status document and dashboard MUST NOT include secrets, credentials, connection strings, internal stack traces or host-level configuration details.
- **FR-005**: The web dashboard MUST obtain status by calling the running backend (not from build-time constants) and refresh it periodically at an interval of no more than 10 seconds, with a visible "last checked" time.
- **FR-006**: The dashboard MUST visually distinguish healthy, degraded, unavailable and backend-unreachable states using text labels (not color alone) and MUST meet basic accessibility expectations (semantic structure, keyboard reachable, sufficient contrast, live status announced politely).
- **FR-007**: The backend MUST apply a foundation database migration on startup (or via a documented migration command used by the start flow) and record it in migration history; the migration MUST be idempotent across restarts and MUST NOT create any product identity, organization or catalog tables.
- **FR-008**: The backend MUST connect to the database with an ordinary, non-superuser application role that cannot bypass row-level security; migrations MAY run with a separate migration role. This establishes the role separation later tenant-isolation specs rely on.
- **FR-009**: When the database is unavailable the backend MUST stay alive, report not-ready and degraded status, and recover automatically when the database returns.
- **FR-010**: The stack MUST start and report healthy when the optional vector database extension is unavailable.
- **FR-011**: The stack MUST be packaged for deployment as container images and provide a Kubernetes deployment chart structure (templates for web, backend and configuration) that renders without errors; full production hardening is out of scope.

**Boundary (do not implement product identity or catalog)**

- **FR-012**: The bootstrap MUST NOT implement user accounts, login, sessions, organizations, memberships, catalog entries, publishing, search or policy features. Requests to such paths MUST return "not found", and the database MUST contain no tables for those domains after migration. An automated test MUST assert this boundary.

**Engineering foundation**

- **FR-013**: The repository MUST contain: MIT licence, readme with quick start, contributing guide, security policy, editor config, line-ending attributes, ignore rules, pinned SDK and package-manager versions, a solution file, a pull-request template matching the project PR policy, and code-owner placeholder instructions that do not name invented owners.
- **FR-014**: The repository MUST provide standard development commands for build, test, lint/format check, start, stop, logs, reset and demo, documented in the readme.
- **FR-015**: CI MUST run on pull requests and execute: backend unit and database integration tests (against a real disposable database), frontend unit tests, lint/type checks, registry and documentation validation including generated-register sync, branch-name validation, chart render check, container image builds and secret scanning.
- **FR-016**: Protected-main requirements MUST be documented; if repository permissions allow, the rules MAY be applied, but the delivered documentation MUST record their verification status truthfully and require a human owner to confirm enforcement.
- **FR-017**: Agent skills and agent definitions under the project's agent directories, the project manifest and the spec registry MUST remain functional; registry validation and register generation MUST be runnable as standard commands and in CI.

**Demo and evidence**

- **FR-018**: An automated demo MUST start the stack, wait for readiness, drive the dashboard in a real browser, assert healthy state values against the status endpoint, induce the database failure state and assert the degraded display, restore the database and assert recovery, assert the identity/catalog boundary, and capture sanitized screenshots of the healthy and degraded dashboard.
- **FR-019**: The demo MUST record run metadata (commit, branch, environment, start/end timestamps, commands, result, evidence paths) and MUST NOT record success if any assertion fails or the stack/browser is unavailable.

**Observability (foundational, GOZ-0000-IF-11)**

- **FR-020**: Every runtime component (backend service, one-shot migrator, web tier) MUST emit traces, metrics and logs through OpenTelemetry, identified by service name, version and deployment environment, using standard semantic conventions.
- **FR-021**: Trace context MUST propagate from the web tier to the backend to the database so a dashboard refresh forms one distributed trace.
- **FR-022**: The default local stack MUST include an OpenTelemetry collector and a local trace viewer/store and expose metrics for scraping, all offline; operators MUST be able to point export at any OTLP endpoint by configuration. Export failure MUST NOT affect health, readiness or request handling.
- **FR-023**: Telemetry MUST NOT contain secrets (passwords, connection strings, tokens); the collector MUST additionally redact credential-like attributes as defence in depth.
- **FR-024**: The backend MUST provide a project instrumentation pattern (named activity source and meter) used for domain operations, demonstrated by a status-probe span and a status-check counter; later specs extend it.
- **FR-025**: Automated tests MUST assert the expected spans/metrics are produced and are secret-free, and the autonomous demo MUST assert a cross-service trace and metrics in the running stack.

### Key Entities

- **Service Status**: A point-in-time report of the backend's health; attributes: overall status, service name, version/build, check timestamp, list of dependency statuses, contract version.
- **Dependency Status**: Health of one dependency (initially the database); attributes: name, status, latency, applied schema version, optional-capability flags (vector extension available/enabled). Never carries credentials.
- **Migration History Record**: The database's own record of applied schema migrations; the latest record defines the reported schema version.
- **Demo Run Record**: Metadata of one automated demo execution and the paths of its evidence (assertion results, screenshots).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From a clean checkout with only a container runtime and the documented prerequisites, a contributor reaches a healthy dashboard using one start command in under 5 minutes on first build and under 60 seconds on subsequent starts.
- **SC-002**: 100% of displayed status values (overall status, database reachability, schema version, service version) match the backend status endpoint at the time of the check in automated demo assertions.
- **SC-003**: After the database is stopped, the dashboard shows the degraded state within 15 seconds; after it is restarted, the dashboard returns to healthy within 30 seconds, with no manual restart of other components.
- **SC-004**: Zero secrets or credentials appear in screenshots, status responses or captured logs, verified by automated scanning of demo evidence.
- **SC-005**: All documented development commands succeed on a clean checkout, and CI completes all required checks on a pull request.
- **SC-006**: Automated tests confirm that 0 identity, organization or catalog endpoints or tables exist after bootstrap.
- **SC-007**: Regenerating the human spec register from the registry produces zero differences.
- **SC-008**: In the automated demo, a dashboard refresh yields a single trace spanning web, backend and database within 30 seconds, and zero telemetry attributes contain credential material.

## Assumptions

- The technology baseline is fixed by the ratified constitution (Principle II): .NET 10 backend, Next.js/TypeScript frontend with pnpm, PostgreSQL, Docker Compose locally and Helm for Kubernetes. This spec names the stack only where the deliverable is the stack itself.
- "Simulated failure state" is demonstrated by actually stopping the database (and separately the backend) container in the demo, which yields a real rather than mocked failure; no fault-injection switch is added to production code.
- The database image used locally includes the optional vector extension where available, but the system must not require it; the bootstrap only reports whether it is available and does not enable or use it.
- Contributors have a container runtime with Compose support; local SDKs (.NET, Node/pnpm) are needed only for running tests or developing outside containers.
- GitHub repository rulesets may not be configurable with the agent's credentials; in that case they are documented as pending human configuration and verification.
- Code owners are not known; the code-owner file contains placeholder instructions only.
- Existing governance documents, Spec Kit installation, skills, agents, manifest and registry from prior PRs are kept and extended, not replaced; Spec Kit is not reinstalled.
- Observability is foundational (maintainer decision 2026-11-14, GOZ-0000-IF-11): OpenTelemetry traces, metrics and logs are in scope from this spec onward. Browser-side (client) telemetry and long-term metric/log storage dashboards are out of scope for GOZ-0000; local logs go to the collector's console output and container logs.
- No seed data is needed beyond what the migration itself creates, since no product entities exist yet; the demo's deterministic fixture is the stack configuration itself.
