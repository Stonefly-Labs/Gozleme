# Implementation Plan: Local Identity and Session Security

**Branch**: `feature/goz-0001-local-identity` | **Date**: 2026-11-15 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `docs/specs/001-local-identity/spec.md`

## Summary

Add local email/password accounts to the cumulative stack with ASP.NET Core Identity (core only)
on the existing `GozlemeDbContext`. Sessions use the cookie scheme with a PostgreSQL-backed
server-side ticket store, so sign-out, sign-out-everywhere and operator reset revoke exactly and
immediately. Data Protection keys persist to PostgreSQL. The Next.js tier stays the only
browser-facing origin. It forwards cookies to the API through new `/api/auth/*` route handlers,
protects the status dashboard at `/` server-side and adds an anonymous `/api/live` probe. The
controls are:

- lockout (5 failures → 15 min);
- per-client and global rate limits;
- Origin/Fetch-Metadata checks plus a custom-header CSRF defence;
- generic failure responses;
- operator-only `reset-password` / `unlock` / `seed` one-shot commands.

Telemetry adds outcome spans and bounded counters with no identity, and the audit log carries
opaque ids. The demo runner becomes spec-parameterised and cumulative; GOZ-0000 scenarios run
signed in. No external identity provider is registered.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK 10.0.404 via `global.json` `latestPatch`); TypeScript 5.9 on Node 24; Python 3 for governance scripts

**Primary Dependencies**:
- Existing: ASP.NET Core 10 minimal APIs; EF Core 10 + Npgsql 10.0.3; OpenTelemetry .NET 1.21; Next.js 16.4.6 + React 19.3.
- New: `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 10.0.15 and `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.15.
- In the shared framework (no package): `Microsoft.AspNetCore.RateLimiting`, `ForwardedHeaders`, cookie authentication.
- No new npm runtime dependencies.

**Storage**: PostgreSQL 17, schema `gozleme`: new tables `users`, `user_claims`, `user_logins`, `user_tokens`, `auth_sessions`, `data_protection_keys` via migration `20261115000000_LocalIdentity`

**Testing**: xUnit v3 + Testcontainers (integration: auth flows, revocation, lockout, rate limit, CSRF, enumeration parity, telemetry no-secret, migration/roles, operator commands); Vitest (route handlers, origin check, forms); Playwright (cumulative demo with `storageState` setup project, axe)

**Target Platform**: Linux containers (Docker Compose locally; Kubernetes via Helm)

**Project Type**: Web application (API + Next.js BFF) in the existing monorepo

**Performance Goals**: Sign-in p95 < 500 ms locally (PBKDF2 dominates); session validation adds ≤ 2 indexed queries per protected request; SC-001 (register < 60 s, sign-in < 15 s end-to-end)

**Constraints**: Offline; no external IdP; no script-readable tokens; no secrets/PII in telemetry, logs or screenshots; API runs as `gozleme_app` (DML only, never migrates); read-only root FS in Helm; migrations backward compatible with GOZ-0000 release

**Scale/Scope**: MVP single tenant-less user base; 1–N API replicas sharing DB-backed sessions/keys; 3 new pages (sign-in, register, dashboard header), ~6 new API endpoints, 3 new CLI modes

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Assessment | Status |
|---|---|---|
| I. Registry, not gateway | Identity only; no proxying of AI/API traffic | PASS |
| II. Portable baseline | .NET 10 / Next.js / PostgreSQL only. No managed IdP, secret store or cache. Data Protection keys live in PostgreSQL. Package versions verified current on 2026-11-15 | PASS |
| III. Permanent identity | User ids are permanent opaque GUIDs, never reused. No federation authority is implied (invariant 10: operator password recovery ≠ federation authority recovery, documented) | PASS |
| IV. Tenant isolation (NON-NEGOTIABLE) | Accounts are global principals, not tenant-owned (FR-034). No tenant data is stored, and RLS stays scheduled for GOZ-0003. `gozleme_app` stays `NOBYPASSRLS`. No secrets in traces/screenshots (FR-031, SC-006). No user-listing endpoint | PASS |
| V. Contracts and bounded slices | Auth API v1 contract in `contracts/`; status contract v1 unchanged in shape (only auth required). Native Spec Kit artifacts only. No OIDC, MFA, roles or email built speculatively | PASS |
| VI. Demo-first | Cumulative spec-parameterised demo, deterministic seed separate from migrations, real screenshots, negative tests; GOZ-0000 scenarios kept | PASS |
| VII. Protected main | Feature branch; human merge only | PASS |
| VIII. Converge → impact → hand-off | Planned; new email/password-reset MVP spec proposed for human approval via PR (roadmap scope change) | PASS (approval pending on PR) |

Post-design re-check (after Phase 1): unchanged, **PASS**. No complexity exceptions.

## Project Structure

### Documentation (this feature)

```text
docs/specs/001-local-identity/
├── spec.md
├── plan.md               # this file
├── research.md           # R1–R12 decisions
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── auth-api.openapi.yaml     # API ↔ web contract (v1)
│   └── web-routes.md             # browser-facing routes, probes, CLI modes
├── checklists/requirements.md
├── progress.yaml
├── evidence/                     # demo-run.json, screenshots/ (created by demo)
├── reviews/                      # impact analysis (post-convergence)
└── tasks.md                      # /speckit-tasks
```

### Source Code (repository root)

```text
src/Gozleme.Api/
├── GozlemeApp.cs                 # + AddGozlemeIdentity(), forwarded headers, rate limiter, auth endpoints, status RequireAuthorization
├── Program.cs                    # + seed | reset-password | unlock modes
├── Identity/                     # new module (only what this spec needs)
│   ├── GozlemeUser.cs
│   ├── IdentityServiceCollectionExtensions.cs   # Identity core, cookie, ticket store, DataProtection, options
│   ├── AuthOptions.cs                           # Gozleme:Auth:* (registration, cookies, limits)
│   ├── AuthEndpoints.cs                         # /api/v1/auth/*
│   ├── DbTicketStore.cs                         # ITicketStore over auth_sessions
│   ├── AuthSession.cs                           # entity
│   ├── SessionRevoker.cs                        # revoke-all + stamp rotation
│   ├── GozlemePasswordValidator.cs + common-passwords.txt (embedded)
│   ├── WebClientHeaderFilter.cs                 # x-gozleme-client CSRF layer
│   ├── AuthTelemetry.cs                         # spans/counter names, categories
│   ├── SecurityAudit.cs                         # LoggerMessage audit events
│   └── OperatorCommands.cs                      # seed / reset-password / unlock
├── Persistence/
│   ├── GozlemeDbContext.cs       # + identity sets, AuthSessions, IDataProtectionKeyContext
│   └── Migrations/20261115000000_LocalIdentity.cs (+ snapshot)
tests/Gozleme.Api.IntegrationTests/
├── Infrastructure/AuthClient.cs  # helper: register/sign-in with cookie + header
├── AuthFlowTests.cs  SessionRevocationTests.cs  LockoutAndRateLimitTests.cs
├── CsrfTests.cs  EnumerationTests.cs  OperatorCommandTests.cs  NoExternalIdentityTests.cs
├── IdentityTelemetryTests.cs  BootstrapBoundaryTests.cs (narrowed)  StatusEndpointTests.cs (auth)
tests/Gozleme.Api.Tests/PasswordValidatorTests.cs, ReturnUrlTests.cs
web/src/
├── proxy.ts                      # cookie-presence redirect for protected pages
├── lib/api.ts                    # forwardToApi(): cookie/xff/header, set-cookie relay
├── lib/origin.ts                 # same-origin check (+ tests)
├── lib/returnTo.ts               # safe relative path (+ tests)
├── lib/session.ts                # server-side getSession()
├── app/page.tsx                  # server-side session gate → StatusDashboard + AccountBar
├── app/login/page.tsx  app/register/page.tsx  components/{SignInForm,RegisterForm,AccountBar}.tsx
├── app/api/auth/{sign-in,register,sign-out,sign-out-all,session,options}/route.ts
├── app/api/live/route.ts
├── app/api/backend-status/route.ts   # forwards cookie; 401 → unauthenticated
└── app/api/otel/v1/traces/route.ts   # + origin check, rate limit
e2e/tests/
├── auth.setup.ts                 # signs in seeded alice → storageState
├── bootstrap-demo.spec.ts        # GOZ-0000 (signed in; screenshot dir from env)
├── identity-demo.spec.ts         # GOZ-0001 scenarios
└── evidence.ts                   # per-spec screenshot dir resolution
scripts/demo/run-demo.sh, write_demo_record.py   # SPEC parameter, cumulative
compose.yaml, .env.example, deploy/helm/gozleme/{values.yaml,templates/*.yaml,README.md}, deploy/otel/collector.yaml
docs/engineering/local-development.md, docs/engineering/demo-contract.md (runner usage)
```

**Structure Decision**: Keep the single API host project and add one `Identity/` module folder
(IF-10: only the modules this spec needs). The web tier adds route handlers and pages under the
existing App Router tree. There are no new projects or packages beyond the two NuGet packages.

## Key Design Points

1. **Auth API (v1)** (`contracts/auth-api.openapi.yaml`):
   - `GET /api/v1/auth/options` (anonymous: `registrationEnabled`)
   - `POST /api/v1/auth/register`
   - `POST /api/v1/auth/sign-in`
   - `POST /api/v1/auth/sign-out`
   - `POST /api/v1/auth/sign-out-all`
   - `GET /api/v1/auth/session` (who am I: opaque id + email for the signed-in user only)
   All unsafe methods require `x-gozleme-client: web`, and all are rate limited. Responses carry
   `Cache-Control: no-store`.
2. **Configuration** (`Gozleme:Auth:*`, env `Gozleme__Auth__*`; Compose maps `GOZLEME_REGISTRATION_ENABLED`, `GOZLEME_SECURE_COOKIES`, `GOZLEME_TRUSTED_PROXIES`):
   - `RegistrationEnabled` (default `true`)
   - `SecureCookies` (default `true`; Compose `false`)
   - `IdleTimeoutMinutes` (30) and `AbsoluteTimeoutHours` (12)
   - `Lockout:MaxFailedAttempts` (5) and `Lockout:Minutes` (15)
   - `RateLimit:SignInPerMinute` (20), `RateLimit:RegisterPerMinute` (10) and `RateLimit:GlobalPerMinute` (300)
   - `Gozleme:Network:TrustedProxies`
   Values are validated at start-up within safe bounds (for example lockout attempts 3–10, idle
   5–120 min), and the API fails fast otherwise.
3. **Status gating**: `/api/v1/status` gets `RequireAuthorization()`. The status contract shape is
   unchanged. `/health/*` stay anonymous. The web healthcheck uses `/api/live`. Helm web probes move
   from `/` to `/api/live`.
4. **Migration**: additive only, so the GOZ-0000 API image keeps working against the migrated
   schema (pre-upgrade compatibility). Default privileges from Foundation grant `gozleme_app` DML on
   the new tables. A test asserts that `gozleme_app` has no DDL rights and that the tables are owned
   by `gozleme_migrator`.
5. **Telemetry**: spans and counter per research R10. `TelemetryTests` patterns are extended with a
   no-secret scan of all captured spans, metrics and log records for the test email, password,
   cookie value and session key.

## Compatibility, Dependencies and Unsupported Modes

- **Breaking for anonymous status consumers**: `/api/v1/status` and `/api/backend-status` now return
  401 without a session. Probes must use `/health/*` (API) and `/api/live` (web). This is documented
  in the quickstart and Helm README.
- **Not supported / not claimed in this slice**:
  - email verification, password reset by email, change password, account deletion, MFA,
    passkeys, remember-me;
  - external OIDC/SAML/OAuth;
  - admin UI;
  - distributed rate limiting;
  - encrypted-at-rest Data Protection keys by default;
  - `Secure` cookies on plain-HTTP local Compose.

## Complexity Tracking

No constitution violations; table intentionally empty.
