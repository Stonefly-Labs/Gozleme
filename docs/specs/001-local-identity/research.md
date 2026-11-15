# Research: Local Identity and Session Security (GOZ-0001)

All Technical Context unknowns resolved below. Versions verified against nuget.org and the
installed toolchain on 2026-11-15 (`dotnet --version` 10.0.404; Next.js 16.4.6 in `web/`).

## R1 — Identity framework and store

- **Decision**: ASP.NET Core Identity *core* (`AddIdentityCore<GozlemeUser>()` + `SignInManager`,
  `UserManager`, password hasher, lockout) with `Microsoft.AspNetCore.Identity.EntityFrameworkCore`
  10.0.15 user-only stores on the existing `GozlemeDbContext` (no roles: RBAC is GOZ-0004). Key type
  `Guid` (opaque, v7 for index locality). Identity tables live in schema `gozleme` with explicit
  snake_case names configured in `OnModelCreating`.
- **Rationale**: The prompt names ASP.NET Core Identity. The framework provides a vetted PBKDF2
  hasher (v3 format: HMAC-SHA512, 100,000 iterations by default in .NET 8+), security stamps,
  lockout and a normalizer. One DbContext keeps migrations in one place (IF-10).
- **Alternatives**: A fully custom user store was rejected because it re-implements security code.
  Full `AddIdentity<,>` was rejected because it registers roles plus a default cookie/external
  scheme set and UI we do not want. A separate identity DbContext was rejected because it would
  split migration history.
- **Note**: The user-only store requires the user, user-claim, user-login and user-token entity sets.
  The `user_logins` table therefore exists but stays empty. It is never written, because no external
  login provider is registered (FR-028). The boundary test asserts that no external authentication
  scheme is registered.

## R2 — Session mechanism: cookie + server-side ticket store

- **Decision**: Use ASP.NET Core cookie authentication (`IdentityConstants.ApplicationScheme`) with
  `CookieAuthenticationOptions.SessionStore` set to a database-backed `ITicketStore`
  (`gozleme.auth_sessions`). The cookie carries only a random 256-bit session key, protected by
  Data Protection. The row stores `SHA-256(key)`, the user id, the ticket bytes (encrypted with a
  dedicated Data Protection purpose), `created_at`, `expires_at` (absolute) and `last_seen_at`.
  - Sign-out: `RemoveAsync` deletes the row, so a replayed cookie finds no ticket and is anonymous (US2-AS2).
  - Sign-out-everywhere, operator reset and lockout transitions: delete all of the user's rows and
    rotate the security stamp.
  - `SecurityStampValidator` with `ValidationInterval = 0` re-checks the stamp on every request as a
    second line of defence.
  - Sliding 30-minute idle expiry: `ExpireTimeSpan = 30 min`, `SlidingExpiration = true`.
  - Absolute 12-hour expiry: `expires_at` is fixed at sign-in and enforced in `RetrieveAsync` and
    `OnValidatePrincipal`.
  - Session fixation (FR-010): every sign-in creates a new key/row; there is no pre-auth session.
- **Rationale**: Stateless cookies cannot satisfy "replayed cookie after sign-out is rejected"
  (FR-012, SC-003). A server-side store makes revocation exact and immediate. Hashing the key at rest
  means a database read does not yield usable cookies.
- **Alternatives**: Stateless cookie + security-stamp-only revocation was rejected because
  single-session sign-out would not revoke a captured cookie. JWT bearer tokens in the browser were
  rejected: the spec forbids script-readable tokens, and JWTs need a deny-list anyway.
  Redis/distributed cache was rejected because PostgreSQL is the only allowed stateful dependency
  (no new infrastructure).
- **Cost**: Two indexed single-row queries per authenticated request (session row + stamp). This is
  acceptable at MVP scale. Expired rows are purged opportunistically on sign-in (bounded `DELETE …
  WHERE expires_at < now() LIMIT`-style batch). There is no background job.

## R3 — Durable Data Protection keys (IF-13, FR-013)

- **Decision**: `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.15,
  `PersistKeysToDbContext<GozlemeDbContext>()` into `gozleme.data_protection_keys`, with
  `SetApplicationName("gozleme")`. Keys are created on demand by the API (`gozleme_app` has DML via
  default privileges).
- **Rationale**: Helm runs read-only root filesystems and multiple replicas. The database is
  already the durable shared store. No new secret store is mandatory (constitution II).
- **Limitation (documented)**: Key XML is stored unencrypted at rest in PostgreSQL, so database read
  access implies the ability to forge cookies. This is the same trust boundary as the session table.
  Operators can add `ProtectKeysWith*` (certificate) later. That is not configured by default, so
  Compose stays secret-free. This is recorded in `deploy/helm/gozleme/README.md` and the quickstart.

## R4 — Web tier (BFF) integration and cookies through the same-origin pattern (IF-02, FR-025)

- **Decision**: The browser talks only to Next.js. New route handlers under `web/src/app/api/auth/*`
  forward requests to the API (`GOZLEME_API_URL`, server-side):
  - They forward the `cookie` header, `x-forwarded-for`, W3C trace context (automatic) and a fixed
    `x-gozleme-client: web` header.
  - They relay the API's `set-cookie` header(s) verbatim.
  - The cookie is host-only on the web origin: `gozleme_session`, `HttpOnly`, `SameSite=Lax`,
    `Path=/`, and `Secure` when `Gozleme:Auth:SecureCookies` is true (Helm default `true`, Compose
    `false`, since local Compose is plain HTTP on 127.0.0.1).
  - `/api/backend-status` forwards the cookie to the API and maps API 401 to its own 401
    `{"reachable":true,"authenticated":false}` without status data.
  - Page protection: the `/` server component calls `GET /api/v1/auth/session` with the forwarded
    cookie and calls `redirect('/login?returnTo=/')` when it gets 401. A Next 16 `proxy.ts`
    short-circuits requests with no session cookie at all. That is an optimisation only; the server
    check is authoritative.
  - The new anonymous liveness route `GET /api/live` returns `{"status":"live"}` and is used by the
    Compose and Helm web probes (FR-022).
- **Rationale**: This keeps the GOZ-0000 rule (no CORS, API not browser-exposed) intact, keeps
  cookies out of scripts, and stores no session state in the web tier.
- **Alternatives**: A web-tier session (e.g. iron-session) was rejected because it would duplicate
  authority and require a second key ring. Exposing the API to the browser with CORS was rejected
  because it violates IF-02.

## R5 — Cross-site request protection (FR-014)

- **Decision**: Two layers.
  1. Web route handlers for unsafe methods accept the request only if `Sec-Fetch-Site` is
     `same-origin`, or (when absent) `Origin` equals the request's own origin (`GOZLEME_PUBLIC_ORIGIN`
     if set, otherwise derived from `Host`/`x-forwarded-proto`). Otherwise they return 403.
  2. API identity endpoints and all unsafe `/api/v1/*` methods require the custom header
     `x-gozleme-client: web` (403 otherwise). A browser cannot add a custom header cross-site
     without a CORS preflight, and the API grants none.
  Combined with `SameSite=Lax` and POST-only state changes, no antiforgery token round-trip is needed.
- **Rationale**: This follows the OWASP CSRF cheat sheet's "Fetch Metadata / Origin verification"
  and "custom request header" defences for JSON APIs. It is simpler than synchronising ASP.NET
  antiforgery tokens through a BFF.
- **Alternatives**: ASP.NET antiforgery tokens were rejected because they need an extra token
  endpoint and cookie and add nothing over the two layers above for a JSON-only BFF.

## R6 — Brute-force controls (FR-018 – FR-020)

- **Lockout**: Identity lockout with `MaxFailedAccessAttempts = 5`,
  `DefaultLockoutTimeSpan = 15 min` and `AllowedForNewUsers = true`, stored in `users` and therefore
  effective across replicas. On transition to locked, all sessions are revoked (security stamp
  rotated). Unknown emails do not create rows. A dummy hash verification equalises timing for
  unknown emails (`PasswordHasher.VerifyHashedPassword` against a static hash).
- **Rate limiting**: The built-in `Microsoft.AspNetCore.RateLimiting` uses a fixed window, policy
  `auth`, partitioned by client address. Defaults are sign-in 20/min and registration 10/min per
  client, plus a global per-instance ceiling of 300/min across auth endpoints as a backstop. All are
  configurable under `Gozleme:Auth:RateLimit:*`. A throttled request gets 429 with `Retry-After`.
- **Client address**: `ForwardedHeaders` (`X-Forwarded-For`) with `ForwardLimit = 1` and trusted
  proxies from `Gozleme:Network:TrustedProxies` (CIDR list). The default is loopback only, which
  means every client shares the web pod's address and the effect fails safe (more restrictive).
  Compose sets the private ranges so the web container is trusted.
- **Limitation (documented)**: Next.js preserves a client-supplied `X-Forwarded-For`
  (`base-server.js`: `x-forwarded-for ??= socket.remoteAddress`). When the web tier is exposed
  directly without a trusted ingress that overwrites the header, per-client limits are therefore
  spoofable. Account lockout and the global ceiling still bound guessing. Distributed (cross-replica)
  rate limiting is not claimed.

## R7 — Account-enumeration resistance (FR-007, FR-020)

- Sign-in returns `401 {"error":"invalid_credentials"}` for wrong password, unknown email and
  locked account alike. A locked account is never revealed to the caller. The bounded failure
  category (including `locked_out`) is recorded only in telemetry and audit events, keyed by the
  opaque id.
- Duplicate registration returns `409 {"error":"registration_failed"}`. It is the same body as
  other non-field registration failures, and the response does not say "already exists". The
  response time is equalised by hashing the password before the uniqueness check.
- **Accepted residual**: A 409 is still observable to a determined prober who also sees 201 for
  new emails. Without email delivery this cannot be eliminated. It is documented and deferred to
  the proposed email spec (verification-first registration).

## R8 — Password policy (FR-003)

- Length 12–128 (Identity `RequiredLength = 12`, custom validator for max). Identity's
  `RequireDigit/Lowercase/Uppercase/NonAlphanumeric` are all false (NIST SP 800-63B-4 §3.1.1.2).
- `GozlemePasswordValidator` rejects:
  - passwords equal to the email or its local part (case-insensitive);
  - passwords in a bundled list of common passwords, a small embedded resource of ≥ 12-character
    common passwords plus their lowercase forms, from the public-domain SecLists list (licence
    recorded in the file header).
  There is no network breach-corpus lookup (offline requirement).

## R9 — Operator commands and seed (FR-026, FR-027, FR-035)

- New one-shot modes on the API binary, alongside `migrate`/`healthcheck`:
  - `seed`: creates or resets the deterministic synthetic demo accounts from
    `GOZLEME_DEMO_PASSWORD` (default `local-dev-demo-pass` in `.env.example`; ≥ 12 chars and not on
    the common list). Accounts: `alice@demo.gozleme.test`, `bob@demo.gozleme.test`,
    `lockout@demo.gozleme.test`. It is idempotent: an existing account gets its password re-set,
    lockout cleared and sessions revoked, so every demo run starts from the same state.
  - `reset-password --email <e> (--password-stdin | --password-file <path>)`: never takes the
    password as an argument value. It applies the password policy, rotates the stamp, clears
    lockout and deletes sessions. Exit codes: 0 ok, 2 unknown account, 3 policy failure, 1 other.
  - `unlock --email <e>`: clears lockout and the failure count. Exit codes as above.
- These commands use the app connection string (`gozleme_app`, DML only) and never DDL. They emit
  a `gozleme.operator.*` span and a security audit log event with the opaque id. Stdout reports the
  outcome without the email.
- Compose adds a `seed` service under profile `demo` (`docker compose --profile demo run --rm seed`).
  The demo runner calls it after `up`. Helm ships no seed job.
- **Alternatives**: HTTP admin endpoints were rejected because there is no RBAC until GOZ-0004 and
  FR-024 forbids them. Seeding through EF `HasData` migrations was rejected: IF-08 requires seed
  data separate from migrations, and demo credentials would end up in the schema history.

## R10 — Observability (IF-11, FR-029 – FR-032)

- **Spans** (via `GozlemeTelemetry.ActivitySource`):
  - names: `gozleme.auth.register`, `gozleme.auth.sign_in`, `gozleme.auth.sign_out`,
    `gozleme.auth.sign_out_all`, `gozleme.operator.seed|reset_password|unlock`;
  - attributes: `gozleme.auth.outcome` (`success|failure`) and `gozleme.auth.failure_category`
    (`invalid_credentials|locked_out|validation|duplicate|registration_closed|rate_limited|unavailable`).
- **Counter**: `gozleme.auth.attempts` (labels `gozleme.auth.operation`, `gozleme.auth.outcome`,
  `gozleme.auth.failure_category`), so cardinality is bounded (≤ 4 × 2 × 8).
- **No user identifier on spans or metrics.** The opaque user id appears only in security audit log
  events (`Gozleme.Security.Audit` category, event names `gozleme.audit.*`), which are needed for
  incident response. This is the documented choice required by the prompt.
- **Privacy guards**:
  - Login/register bodies are JSON, so there are no credentials in URLs.
  - `returnTo` is a relative path only.
  - EF parameterises email lookups, so `db.query.text` has no values.
  - The ASP.NET Core and HttpClient instrumentation do not record headers by default. That default
    is kept: the `cookie`/`set-cookie` headers are never added to enrichment.
  - The collector redaction processor gains a `gozleme_session` / `cookie` attribute-key guard as
    defence in depth.
- **Browser ingestion route (IF-20)**: anonymous pages are instrumented, so the route stays
  unauthenticated. It now requires a same-origin check (R5 layer 1) and an in-memory per-client
  token bucket (default 60 requests/min, keyed by the first `x-forwarded-for` entry, same spoofing
  caveat as R6), on top of the existing 256 KiB / content-type bounds.

## R11 — Protected status surfaces and GOZ-0000 compatibility (IF-01, IF-02)

- `GET /api/v1/status` gets `.RequireAuthorization()`. Cookie authentication events are overridden
  so API requests get bare `401`/`403` (never 302 redirects).
- `/health/live` and `/health/ready` stay anonymous.
- The dashboard client treats `401` from `/api/backend-status` as "session expired": it shows a
  sign-in link and no status.
- The four GOZ-0000 demo scenarios sign in as `alice@demo.gozleme.test` through a Playwright setup
  project that stores `storageState`, so it is one sign-in per run, not per test.
- `BootstrapBoundaryTests` narrowing:
  - Removed: `POST /api/v1/auth/login` → now `POST /api/v1/auth/sign-in` exists; `/login` is a web
    route, not an API route, so it is kept in the API 404 set.
  - Kept: `GET /api/v1/users` 404, catalog/org/search/publish 404.
  - Changed: "schema has zero tables" → "schema has exactly the identity tables". "Only migration
    history outside schemas" is kept.
  - Added: `NoExternalIdentityTests`.

## R12 — Demo runner generalisation (IF-08)

- `scripts/demo/run-demo.sh [SPEC]` (default: latest registered spec with a demo; `make demo SPEC=GOZ-0001`).
  - Spec → directory comes from `.gozleme/spec-registry.yaml` / `docs/specs/NNN-*`.
  - Cumulative: it runs every Playwright project up to and including the requested spec
    (`goz-0000`, `goz-0001`, …) in order.
  - Each spec's screenshots are written to its own `evidence/screenshots`, using a directory
    resolved from `GOZLEME_DEMO_SPEC_DIR_<NNNN>` passed by the runner, so there are no hard-coded paths.
  - The run record goes to the requested spec's `evidence/demo-run.json`, with per-suite results.
- CI job `e2e-demo` calls `make demo SPEC=GOZ-0001`.
