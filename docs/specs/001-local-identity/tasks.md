---
description: "Task list for GOZ-0001 local identity and session security"
---

# Tasks: Local Identity and Session Security

**Input**: Design documents from `docs/specs/001-local-identity/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R12), data-model.md, contracts/auth-api.openapi.yaml, contracts/web-routes.md, quickstart.md

**Tests**: Required by the prompt (positive + negative security tests) and constitution VI.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: Setup

- [X] T001 Add `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 10.0.15 and `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.15 to `Directory.Packages.props` and `src/Gozleme.Api/Gozleme.Api.csproj`; verify `dotnet list package --vulnerable --include-transitive` is clean
- [X] T002 [P] Add `GOZLEME_REGISTRATION_ENABLED`, `GOZLEME_SECURE_COOKIES`, `GOZLEME_TRUSTED_PROXIES`, `GOZLEME_DEMO_PASSWORD` (throwaway `local-dev-demo-pass`) to `.env.example`

---

## Phase 2: Foundational (blocking)

- [X] T003 Create `GozlemeUser : IdentityUser<Guid>` (+ `CreatedAt`, `LastSignInAt`) and `AuthSession` entity in `src/Gozleme.Api/Identity/`
- [X] T004 Extend `GozlemeDbContext` with Identity user-only sets, `AuthSessions` and `IDataProtectionKeyContext` (`DataProtectionKeys`). Use explicit snake_case table/column names in schema `gozleme` per data-model.md: unique `normalized_email` and `normalized_user_name`; `auth_sessions.key_hash` bytea PK "SHA-256 (32 bytes)"; FK `user_id` on delete cascade; indexes on `user_id` and `expires_at`
- [X] T005 Generate migration `20261115000000_LocalIdentity` + snapshot in `src/Gozleme.Api/Persistence/Migrations/`; additive only
- [X] T006 `AuthOptions` (`Gozleme:Auth:*`, `Gozleme:Network:TrustedProxies`) with start-up validation of safe bounds in `src/Gozleme.Api/Identity/AuthOptions.cs`
- [X] T007 `AddGozlemeIdentity()` in `src/Gozleme.Api/Identity/IdentityServiceCollectionExtensions.cs`, covering:
  - Identity core, password and lockout options from `AuthOptions`, `SignInManager`, `SecurityStampValidator` interval 0;
  - Data Protection persisted to the DbContext with application name `gozleme`;
  - cookie `gozleme_session` (HttpOnly, SameSite=Lax, Secure per option, 30 min sliding), with API events returning 401/403 and never redirecting;
  - DbContext registration on the existing data source.
- [X] T008 `DbTicketStore : ITicketStore` in `src/Gozleme.Api/Identity/DbTicketStore.cs`, which:
  - generates a random 32-byte key and stores its SHA-256;
  - encrypts the ticket with Data Protection purpose `Gozleme.Identity.SessionTicket.v1`;
  - enforces the absolute 12 h `expires_at`, updates `last_seen_at` on renew and deletes on remove;
  - purges expired rows opportunistically in a bounded batch.
- [X] T009 `SessionRevoker` (delete all sessions for user + `UpdateSecurityStampAsync`) in `src/Gozleme.Api/Identity/SessionRevoker.cs`
- [X] T010 [P] `AuthTelemetry` (span names, `gozleme.auth.attempts` counter, bounded categories) and `SecurityAudit` LoggerMessage events (category `Gozleme.Security.Audit`, opaque `UserId` only) in `src/Gozleme.Api/Identity/`
- [X] T011 Wire `GozlemeApp`:
  - `ForwardedHeaders` with trusted proxies and `ForwardLimit=1`;
  - authentication/authorization middleware;
  - rate limiter policies `sign-in`, `register` (per client) plus a global auth ceiling, returning 429 + `Retry-After`;
  - `/api/v1/status` gets `RequireAuthorization()`; `/health/*` stay anonymous.
- [X] T012 [P] Test infrastructure, covering:
  - `GozlemeApiFactory` accepts config overrides;
  - new `tests/Gozleme.Api.IntegrationTests/Infrastructure/AuthClient.cs` handles cookie container, `x-gozleme-client` and register/sign-in helpers;
  - migration fixture applies `LocalIdentity`.
- [X] T013 Update the existing GOZ-0000 integration tests to the authenticated status endpoint (`StatusEndpointTests`, `DatabaseOutageTests`, `TelemetryTests`) and add an anonymous 401 assertion
- [X] T014 Narrow `tests/Gozleme.Api.IntegrationTests/BootstrapBoundaryTests.cs` (IF-01):
  - keep 404 for catalog/capabilities/organizations/users/search/publish and API `/login`;
  - replace `POST /api/v1/auth/login` with an assertion that it remains 404 (the endpoint is named `sign-in`);
  - change "zero tables" → "exactly the identity tables";
  - keep "only migration history outside schemas".
- [X] T015 Extend `MigrationAndRoleTests`: new tables owned by `gozleme_migrator`; `gozleme_app` has DML but no `CREATE` on schema `gozleme`; migration idempotent

**Checkpoint**: `dotnet test` green; migration applies; status requires auth.

---

## Phase 3: User Story 1 — Register, sign in, protected dashboard (P1) 🎯 MVP

**Goal**: Register/sign in through the web tier; `/` and status data reject anonymous access.
**Independent Test**: Anonymous `/` → `/login`; register → dashboard; sign out → sign in again; anonymous status APIs 401.

### Tests for User Story 1

- [X] T016 [P] [US1] Unit tests `tests/Gozleme.Api.Tests/PasswordValidatorTests.cs`:
  - "at least 12 and at most 128 characters";
  - rejects the email or its local part;
  - rejects common passwords;
  - no composition rules.
- [X] T017 [P] [US1] Integration tests `tests/Gozleme.Api.IntegrationTests/AuthFlowTests.cs`:
  - register 201 + cookie flags (HttpOnly, SameSite=Lax, Secure when configured);
  - session endpoint; email normalisation (case/whitespace) and the duplicate rule;
  - validation errors 400 with fields; oversized/control-char input 400, never 500;
  - sign-in issues a new session key every time (fixation);
  - session survives an API factory restart (shared DB key ring);
  - absolute expiry enforced (time-shifted `TimeProvider`).
- [X] T018 [P] [US1] Vitest `web/src/lib/returnTo.test.ts` (relative-only, rejects `//`, `/\`, absolute, encoded tricks) and `web/src/lib/origin.test.ts`

### Implementation for User Story 1

- [X] T019 [US1] `GozlemePasswordValidator` + embedded `common-passwords.txt` (source/licence header) in `src/Gozleme.Api/Identity/`
- [X] T020 [US1] `AuthEndpoints` in `src/Gozleme.Api/Identity/AuthEndpoints.cs`:
  - `GET options`, `POST register`, `POST sign-in`, `GET session` per `contracts/auth-api.openapi.yaml`;
  - `Cache-Control: no-store`; spans, counters and audit events;
  - `last_sign_in_at` update; dummy-hash timing equalisation for unknown emails.
- [X] T021 [US1] Web helpers:
  - `web/src/lib/api.ts` `forwardToApi()`: cookie, `x-forwarded-for`, `x-gozleme-client: web`, timeout; relays `set-cookie`; maps errors to a generic unavailable;
  - `web/src/lib/origin.ts`, `web/src/lib/returnTo.ts`, `web/src/lib/session.ts`.
- [X] T022 [US1] Route handlers `web/src/app/api/auth/{options,register,sign-in,session}/route.ts` with the same-origin check on POST
- [X] T023 [US1] Pages:
  - `web/src/app/login/page.tsx` + `web/src/components/SignInForm.tsx`: generic error, `returnTo`, register link only when enabled, aria-live errors;
  - `web/src/app/register/page.tsx` + `web/src/components/RegisterForm.tsx`: field errors, confirm password, closed state;
  - signed-in visitors redirect to `/`.
- [X] T024 [US1] Protect the dashboard:
  - ~~`web/src/proxy.ts`: cookie-presence redirect;~~ *Implementation note: not added — the server-side `getSession()` gate already redirects with `returnTo`, and a cookie-presence check would add a second, weaker source of truth;*
  - `web/src/app/page.tsx`: server-side `getSession()` gate + `AccountBar`.
- [X] T025 [US1] `web/src/app/api/backend-status/route.ts` forwards the cookie and maps 401 → `{reachable:true, authenticated:false}` *(implemented as `session: "expired"`; API 503 → `session: "unverifiable"` per FR-021a)*. `StatusDashboard` shows a "Session expired — sign in again" state with no status data. Update `web/src/lib/status.ts` types and tests.
- [X] T026 [US1] Anonymous `web/src/app/api/live/route.ts`; switch the Compose web healthcheck and Helm web probes (`deploy/helm/gozleme/templates/web.yaml`) to `/api/live`
- [X] T027 [P] [US1] Vitest tests for route handlers (cookie relay, origin rejection 403, 401 mapping) and forms (accessible labels, error rendering) under `web/src/`

**Checkpoint**: US1 demonstrable manually per quickstart.

---

## Phase 4: User Story 2 — Sign out and sign out everywhere (P1)

**Goal**: Server-side session end; revoke all sessions.
**Independent Test**: Two contexts; sign-out affects one; sign-out-all affects both; replayed cookie anonymous.

- [X] T028 [P] [US2] Integration tests `tests/Gozleme.Api.IntegrationTests/SessionRevocationTests.cs`: sign-out deletes the row and replayed cookie → 401; sign-out-all revokes the other session; sign-out without the client header → 403; sign-out idempotent when anonymous (204)
- [X] T029 [US2] `POST sign-out` and `POST sign-out-all` in `AuthEndpoints` (spans/counters/audit)
- [X] T030 [US2] Web routes `web/src/app/api/auth/{sign-out,sign-out-all}/route.ts` and `AccountBar` buttons (`web/src/components/AccountBar.tsx`) returning to `/login`
- [X] T031 [P] [US2] Vitest tests for `AccountBar` and the sign-out routes

---

## Phase 5: User Story 3 — Brute-force and enumeration resistance (P2)

**Goal**: Lockout, rate limits, indistinguishable failures, no PII in telemetry.
**Independent Test**: Compare failure responses; 6th attempt locked; 429 beyond rate.

- [X] T032 [P] [US3] `tests/Gozleme.Api.IntegrationTests/EnumerationTests.cs`: wrong password, unknown email and locked account give byte-identical status + body; duplicate registration gives the generic 409
- [X] T033 [P] [US3] `tests/Gozleme.Api.IntegrationTests/LockoutAndRateLimitTests.cs`:
  - 5 failures lock and the correct password is refused; expiry (TimeProvider) unlocks and success resets the count;
  - lockout revokes existing sessions;
  - unknown emails create no rows;
  - per-client 429 + `Retry-After`; distinct forwarded clients are independent only from trusted proxies; global ceiling.
- [X] T034 [P] [US3] `tests/Gozleme.Api.IntegrationTests/CsrfTests.cs`: missing/incorrect `x-gozleme-client` → 403 on every unsafe auth endpoint
- [X] T035 [P] [US3] `tests/Gozleme.Api.IntegrationTests/IdentityTelemetryTests.cs`: auth spans and counter labels present with bounded categories; captured spans, metrics and logs contain none of the test email/password/cookie/session key; audit events carry the opaque id only
- [X] T036 [US3] Implement lockout → revoke, the rate limiter configuration, generic failure mapping and the locked-out category in telemetry only (`AuthEndpoints`, `GozlemeApp`)
- [X] T037 [US3] Harden `web/src/app/api/otel/v1/traces/route.ts` (IF-20): same-origin check + in-memory per-client token bucket (429); extend `route.test.ts`
- [X] T038 [P] [US3] Add a cookie/session attribute guard to the redaction processor in `deploy/otel/collector.yaml`

---

## Phase 6: User Story 4 — Operator controls (P2)

**Goal**: Registration toggle; CLI reset/unlock; no HTTP admin surface.
**Independent Test**: Registration closed → refused; reset/unlock commands work, audited, sessions revoked.

- [X] T039 [P] [US4] `tests/Gozleme.Api.IntegrationTests/OperatorCommandTests.cs`:
  - reset via stdin and via file: old password fails, new works, sessions revoked, lockout cleared;
  - policy failure exit 3; unknown email exit 2 and no row created; unlock;
  - output contains no email/password; `--password <value>` rejected (exit 64).
- [X] T040 [P] [US4] Registration-closed integration test: register → 403 `registration_closed`; `options.registrationEnabled=false`; sign-in still works
- [X] T041 [US4] `OperatorCommands` (`seed`, `reset-password`, `unlock`) in `src/Gozleme.Api/Identity/OperatorCommands.cs`, wired in `src/Gozleme.Api/Program.cs`. Seed uses the deterministic accounts `alice@`, `bob@` and `lockout@demo.gozleme.test` with `GOZLEME_DEMO_PASSWORD`, and is idempotent (re-sets password, unlocks, revokes sessions).
- [X] T042 [US4] Compose: `seed` service (profile `demo`, after `migrate`); api env `Gozleme__Auth__*`/`Gozleme__Network__TrustedProxies` from `GOZLEME_*`. Helm: `auth.registrationEnabled`, `auth.secureCookies: true`, `network.trustedProxies` values → api env; README documents operator commands and the key-at-rest limitation.

---

## Phase 7: User Story 5 — Cumulative demo and the no-external-identity boundary (P3)

**Goal**: Spec-parameterised cumulative demo; GOZ-0000 scenarios signed in; boundary proven.
**Independent Test**: `make demo SPEC=GOZ-0001` passes from clean checkout and writes evidence.

- [X] T043 [P] [US5] `tests/Gozleme.Api.IntegrationTests/NoExternalIdentityTests.cs`:
  - no authentication scheme other than the Identity application cookie is registered;
  - `/signin-oidc`, `/signin-google`, `/api/v1/auth/external/*`, `/.well-known/openid-configuration` → 404;
  - `options.externalProviders` empty; `user_logins` stays empty.
- [X] T044 [US5] Generalise `scripts/demo/run-demo.sh` and `scripts/demo/write_demo_record.py`:
  - `SPEC` argument resolved through the registry to the spec directory; cumulative Playwright projects up to SPEC;
  - per-spec screenshot dirs via env; record at `<spec>/evidence/demo-run.json` with per-suite results;
  - runs `docker compose --profile demo run --rm seed`.
  Update `Makefile` (`demo SPEC=`) and the CI `e2e-demo` job.
- [X] T045 [US5] `e2e/playwright.config.ts` projects `setup` (`e2e/tests/auth.setup.ts` → storageState as alice), `goz-0000`, `goz-0001`; `e2e/tests/evidence.ts` resolves screenshot dirs; update `e2e/tests/bootstrap-demo.spec.ts` to use storageState and a schema-version assertion matching the latest migration, keeping all four scenarios plus boundary/observability (narrowing the `/login` and "sign in" text assertions per IF-01)
- [X] T046 [US5] `e2e/tests/identity-demo.spec.ts`, which does the following, with screenshots:
  1. anonymous redirect, plus status API 401;
  2. register new synthetic user → dashboard;
  3. sign out + cookie replay anonymous;
  4. sign in with `returnTo`;
  5. two-context sign-out-everywhere;
  6. lockout on `lockout@` (generic message identical for unknown email);
  7. closed registration (recreate api with `GOZLEME_REGISTRATION_ENABLED=false`, then restore);
  8. no external sign-in option and 404 on OIDC paths;
  9. sign-in trace in Jaeger with no email/cookie in span attributes;
  10. axe on all pages;
  11. secret/PII scan of page text and compose logs.
- [X] T047 [US5] Run `make demo SPEC=GOZ-0001`; commit evidence under `docs/specs/001-local-identity/evidence/` (sanitised synthetic data only)

---

## Phase 8: Polish & Cross-Cutting

- [X] T048 [P] Docs: `docs/engineering/local-development.md` (accounts, seed, operator commands, auth config), `docs/engineering/demo-contract.md` (runner usage), `README.md` quick start sign-in note, `deploy/helm/gozleme/README.md`
- [X] T049 Full gates: `make lint`, `make test`, `make validate`, `make helm-lint`, gitleaks; record results in `progress.yaml`
- [X] T050 Measure SC-001 timing (register < 60 s, sign-in < 15 s) in the demo record

---

## Dependencies & Execution Order

- Setup → Foundational (T003–T015) blocks all stories.
- US1 (T016–T027) is the MVP. US2 depends on the US1 endpoints/web helpers. US3 depends on US1 sign-in/register. US4 depends on Foundational + US1 (validator). US5 depends on US1–US4.
- Polish comes last.

## Parallel Opportunities

- T001/T002; T010/T012; all `[P]` test tasks within a story (different files).
- US3 tests T032–T035 in parallel; US4 tests T039/T040 in parallel with US3 implementation.

## Implementation Strategy

MVP first: Foundational + US1 gives a protected dashboard with register/sign-in. Then US2 (sign-out), US3 (hardening), US4 (operator), US5 (cumulative demo + evidence). Each checkpoint runs `dotnet test` + `pnpm test`.
