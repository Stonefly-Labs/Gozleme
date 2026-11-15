# GOZ-0001 post-convergence spec impact analysis

**Source spec:** GOZ-0001, Local identity and session security
**Implementation revision:** analysed at `d2e5c6c`, re-analysed at `d7b9108` after a bounded correction (see "Corrective GOZ-0001 iteration") on `feature/goz-0001-local-identity`. The demo ran on a clean tree at `e474b69`: 19 of 19 recorded assertions passed, 18 Playwright tests plus the secret scan, and the evidence is in `7df8a43`. `git diff e474b69 d2e5c6c -- src web` is empty: after the demo, `d2e5c6c` adds only `SessionExpiryTests.cs` and documentation, so the demo evidence still applies to the code.
**Analyst:** Spec Impact Analyst (`analyze-spec-impact`), 2026-11-15
**Structured findings:** `docs/specs/001-local-identity/reviews/impact-findings.json` (22 findings; validates against `.gozleme/schemas/impact-findings.schema.json`)

## Scope inspected

- **API code:** `src/Gozleme.Api`:
  - `GozlemeApp.cs`, `Program.cs`, `DatabaseUnavailable.cs`;
  - `Identity/*`: endpoints, options, ticket store, principal validator, revoker, CSRF filter, telemetry, audit, operator commands, user;
  - `Persistence/GozlemeDbContext.cs` and migration `20261115000000_LocalIdentity`.
- **Web code:** `web/src`:
  - `lib/api.ts`, `lib/origin.ts`, `lib/session.ts`;
  - `app/page.tsx`, `app/api/auth/*`, `app/api/backend-status`, `app/api/live`, `app/api/otel/v1/traces`;
  - `telemetry/ui.ts`.
- **Deployment:** `compose.yaml` (api auth env, web probe `/api/live`, `seed` profile), `deploy/helm/gozleme` (values `auth.*`, README identity section, templates: api, web and migrate Job only, no ingress), `deploy/otel/collector.yaml` (cookie and session redaction).
- **Tests and demo:**
  - integration tests: boundary, CSRF, outage, no-external-identity, revocation, expiry, migration and roles;
  - `e2e/playwright.config.ts`, `e2e/tests/{auth.setup,evidence,identity-demo,bootstrap-demo}.ts`;
  - `scripts/demo/run-demo.sh`, `.github/workflows/ci.yml`;
  - `evidence/demo-run.json`.
- **Spec workspace:** spec.md (Clarifications, FR-001 to FR-038, FR-021a), plan.md, research.md R1 to R12, data-model.md, contracts/auth-api.openapi.yaml, contracts/web-routes.md, progress.yaml decisions.
- **Governance:** `architecture-baseline.md`, `security-invariants.md`, `consistency-review.md`, `docs/roadmap/mvp-vs-post-mvp.md`, `.gozleme/spec-registry.yaml`, `scripts/validate_docs.py`.
- **Downstream prompts:** every prompt from GOZ-0002 to GOZ-0039. Each depends on GOZ-0001 transitively through GOZ-0002, and I read each one's outcome, scope, boundary and earlier alignment sections against the code.

## Verified implementation facts that downstream specs consume

| Area | Fact at `d2e5c6c` (evidence) |
|---|---|
| Accounts | Global principals in `gozleme.users` (Identity user-only store, `IdentityUser<Guid>` with UUIDv7 ids). No tenant column, no RLS, no roles table (`GozlemeUser.cs`, `GozlemeDbContext.cs:16-17`, `IdentityServiceCollectionExtensions.cs:38-54`). Email is **unverified** (`:51`; contract `emailVerified: const false`). Self-registration is on by default (`AuthOptions.cs:11`). |
| Sessions | `gozleme_session` cookie (HttpOnly, `SameSite=Lax`, `Secure` except in Compose). It carries a 32-byte key, and the database stores only its SHA-256 plus the protected ticket in `auth_sessions` (`DbTicketStore.cs`). 30-minute sliding and 12-hour absolute expiry. The security stamp is checked on every request (`SessionPrincipalValidator.cs`). Claims are frozen at sign-in. Revocation deletes the rows and rotates the stamp (`SessionRevoker.cs`). |
| Key ring | Data Protection keys live in `gozleme.data_protection_keys` and are **unencrypted at rest** (R3, Helm README). |
| Authorization | `AddAuthorization()` has **no fallback policy**. Endpoints opt in with `.RequireAuthorization()`. Pages opt in with `getSession()`. There is no `proxy.ts` (recorded deviation). |
| CSRF | The web tier checks `Sec-Fetch-Site`/`Origin` for each route handler. The API checks `x-gozleme-client: web` with a **per-endpoint** filter on auth POSTs only, although R5 says it covers all unsafe `/api/v1/*` methods. `forwardToApi` always adds the header. |
| BFF | `forwardToApi` buffers text, uses a 5-second timeout, maps API 5xx to 503 and relays `set-cookie`. The API is cluster-internal: Helm has no ingress and Compose binds `127.0.0.1`. |
| Outage | A global middleware answers `503 {"error":"unavailable"}` when the database is unreachable, for protected and identity endpoints alike (maintainer decision). |
| Surfaces | `/api/v1/status` and web `/api/backend-status` require a session. `/health/*` and web `/api/live` are anonymous. There is no HTTP user listing, reset or unlock (FR-024). |
| Abuse controls | Lockout after 5 failures for 15 minutes. Per-client limits on sign-in (20/min) and registration (10/min), plus a 300/min global ceiling, all per instance and only for credential endpoints. `ForwardedHeaders` trusts `GOZLEME_TRUSTED_PROXIES`, and `X-Forwarded-For` can be spoofed without an overwriting ingress (R6). |
| Operator | One-shot `seed`, `reset-password` (stdin or file only) and `unlock` commands, run as `gozleme_app`. Seeded accounts are alice, bob and lockout `@demo.gozleme.test`. Helm has no seed job. |
| Telemetry | Spans `gozleme.auth.*`, `gozleme.operator.*` and browser `gozleme.ui.*`. Counter `gozleme.auth.attempts` with bounded labels. Audit events are **log records** in category `Gozleme.Security.Audit`, carrying opaque ids only. |
| Demo | Playwright projects chain `setup` → `goz-0000` → `goz-0001`. `specDirs` is in `e2e/tests/evidence.ts`. The runner defaults to GOZ-0001, and CI hard-codes `run-demo.sh GOZ-0001`. After the GOZ-0001 suite, bob's sessions are revoked and lockout@ is locked. |
| Boundary | The exact identity table list, 404s for catalog, capabilities, organizations, users, search and publish, and a single `Identity.Application` scheme are all asserted. |

## Findings

| ID | Sev | Finding | Affected specs | Disposition |
|---|---|---|---|---|
| GOZ-0001-IF-01 | medium | The API CSRF header check is opt-in per endpoint, while R5 claims it is global | all (0002–0039) | **corrected in GOZ-0001 at `d7b9108`**; prompts updated |
| GOZ-0001-IF-02 | high | Authorization is not deny-by-default; endpoints and pages opt in one at a time | all | **corrected in GOZ-0001 at `d7b9108`** (API); pages still gate per page; prompts updated |
| GOZ-0001-IF-03 | high | Identity tables are global, and the GOZ-0003 prompt wrongly expects GOZ-0001 tenant tables to retrofit; tenant context must not touch session resolution | 0002, 0003, 0004 | applied to prompts |
| GOZ-0001-IF-04 | high | Ticket claims are frozen at sign-in, and stamp rotation means signing out everywhere | 0002, 0003, 0004, 0018, 0023 | applied to prompts |
| GOZ-0001-IF-05 | high | Unverified email plus open registration means invitations, namespaces and SoD cannot rely on email | 0002, 0004, 0005, 0021 | applied to prompts |
| GOZ-0001-IF-06 | high | Email delivery, verification and reset have no spec; the maintainer chose a new MVP spec after GOZ-0002 | 0002, 0004, 0005, 0021, 0035, 0039, 0040 | **approved 2026-11-15**: registered as GOZ-0040 after GOZ-0002 |
| GOZ-0001-IF-07 | high | External OIDC is MVP (`mvp-vs-post-mvp.md:3`) with no owning spec | 0002, 0004, 0039 | **maintainer chose an MVP spec 2026-11-15**: drafted as `spec-0041.md`, PROPOSED pending approval of the draft and placement |
| GOZ-0001-IF-08 | medium | The BFF helper is text-only, with a 5-second timeout | 0009, 0010, 0011, 0028, 0038 | applied to prompts |
| GOZ-0001-IF-09 | medium | Demo chaining, leftover seeded-account state and the hard-coded current spec | all | applied to prompts |
| GOZ-0001-IF-10 | medium | Boundary assertions that later specs narrow selectively | 0002, 0005, 0007, 0012, 0013, 0029 | applied to prompts |
| GOZ-0001-IF-11 | medium | Identity audit events exist only as retention-bounded logs | 0002, 0004, 0023, 0035, 0039 | applied to prompts |
| GOZ-0001-IF-12 | high | Restoring a backup resurrects revoked sessions, and backups hold the unencrypted key ring | 0035, 0039 | applied to prompts |
| GOZ-0001-IF-13 | medium | No non-browser authentication exists, and `x-gozleme-client` is not authentication | 0028, 0029, 0031–0033, 0036 | applied to prompts |
| GOZ-0001-IF-14 | medium | Two instances on 127.0.0.1 collide on the `gozleme_session` cookie | 0029, 0031–0033, 0036 | applied to prompts |
| GOZ-0001-IF-15 | medium | Rate limiting covers only credential endpoints, per instance, keyed on a spoofable address | 0009, 0013, 0015, 0038 | applied to prompts |
| GOZ-0001-IF-16 | medium | Accepted residuals the release gate must state | 0039 | applied to prompts |
| GOZ-0001-IF-17 | low | Fail-closed outage handling is global; streaming endpoints need care | all | applied to prompts |
| GOZ-0001-IF-18 | low | Identity data-model facts for foreign-key consumers; no account deletion | 0002, 0004, 0021, 0035 | applied to prompts |
| GOZ-0001-IF-19 | low | research.md R5 and R12 differ from the code | 0001 | **corrected in GOZ-0001 at `d7b9108`** |
| GOZ-0001-IF-20 | low | The key ring must not protect signing keys or provider credentials | 0007, 0016, 0029 | applied to prompts |
| GOZ-0001-IF-21 | low | Sandbox and playground must not receive session material | 0037, 0038 | applied to prompts |
| GOZ-0001-IF-22 | low | Anonymous discovery of public records is undecided | 0012, 0013 | applied to prompts |

The evidence (file:line), recommendation and affected specs for each finding are in `impact-findings.json`.

## Prompt edits applied (within authority)

**All 38 future prompts, GOZ-0002 to GOZ-0039,** get a new section, `### Identity and session conventions (GOZ-0001-IF-01, IF-02, IF-09, IF-11, IF-17, 2026-11-15)`, with provenance. It covers:
- protected by default, with the current lack of a fallback policy;
- both CSRF layers;
- fail-closed outages;
- demo project chaining and seeded-account state;
- opaque-id audit and telemetry.

**Spec-specific sections,** headed `### Implementation alignment (GOZ-0001-IF-…, 2026-11-15)`, were added to 25 prompts:

| Prompt | Findings |
|---|---|
| 0002 | IF-03, 04, 05, 06, 10, 18, and the global guard from IF-01/02 |
| 0003 | IF-03 (corrects the GOZ-0000-IF-03 retrofit sentence), 04 |
| 0004 | IF-02, 04, 05 |
| 0005 | IF-05, 10 |
| 0007 | IF-10, 20 |
| 0009 | IF-08, 15 |
| 0010, 0011 | IF-08 |
| 0012 | IF-10, 22 |
| 0013 | IF-10, 15, 22 |
| 0015 | IF-15 |
| 0016 | IF-20 |
| 0018 | IF-04 |
| 0021 | IF-05, 18 |
| 0023 | IF-04, 11 |
| 0028 | IF-08, 13 |
| 0029 | IF-13, 14, 20 |
| 0031, 0032, 0033, 0036 | IF-13, 14 |
| 0035 | IF-11, 12, 18 |
| 0037 | IF-21 |
| 0038 | IF-08, 15, 21 |
| 0039 | IF-09, 11, 12, 16 |

**Roadmap Steward:** bump `promptRevision` from 2 to 3 for all 38 prompts, GOZ-0002 to GOZ-0039.

No outcome, scope, boundary, prerequisite, dependency, invariant or MVP commitment changed, and the DAG is unchanged. The edits were applied by a script and are idempotent: they are skipped if a `GOZ-0001-IF-` marker is already present.

**New file, proposed only:** `docs/prompts/spec-0040.md`, GOZ-0040, Email delivery, verification and self-service password reset.
- It is marked **PROPOSED — pending human approval**, with Release MVP and prerequisites GOZ-0001 and GOZ-0002.
- It is **not** in `.gozleme/spec-registry.yaml`, and no other spec's dependencies were changed.
- `make validate` still passes, because the validator checks only registered prompts.
- Registering it will require changing `scripts/validate_docs.py:52-53`, which expects exactly 40 specs numbered 0 to 39.

## Items requiring human decision

1. **GOZ-0001-IF-06: approve or reject GOZ-0040** (new MVP scope). If approved, decide its ordering:
   - The maintainer's stated choice is "after GOZ-0002".
   - GOZ-0002 invitations cannot safely match on unverified email, so either GOZ-0002 uses single-use invitation codes or controlled local add (which its updated prompt already requires), or the email spec moves before GOZ-0002.
   - Later prompts that could use verified email: GOZ-0004 (role assignment by address), GOZ-0021 (reviewer notifications), GOZ-0035 (withdrawal notices), GOZ-0039 (SMTP in the portability matrix), and any OIDC spec (account linking).
2. **GOZ-0001-IF-07: external OIDC ownership.** `docs/roadmap/mvp-vs-post-mvp.md:3` lists "multi-org local/OIDC identities" as MVP, but no spec owns OIDC. Either add an MVP OIDC spec, which would depend on verified email for account linking, or move OIDC to post-MVP. Both are committed-scope changes. No prompt was edited for this.
3. **Noted, no approval needed now:**
   - **IF-11:** whether identity and membership audit events must be durable. This is deferred to GOZ-0023, which will put the decision to the maintainer.
   - **IF-22:** whether public records are discoverable anonymously. GOZ-0012 decides, and the default is fail-closed.

## Corrective GOZ-0001 iteration (applied after this analysis)

drive-speckit judged IF-01/IF-02 a gap in GOZ-0001 itself, not only a downstream convention: Constitution IV requires every boundary to deny by default, and R5 already promised the global check. Bounded correction at `d7b9108`:

- Every `/api/v1` endpoint maps on one route group with `.RequireAuthorization()` and `WebClientHeaderFilter` (unsafe methods only). `options`, `register`, `sign-in` and `sign-out` opt out with `.AllowAnonymous()`.
- `EndpointAuthorizationTests` fails on any endpoint with no authorization decision and pins the anonymous list. It was checked to fail on a deliberately unprotected endpoint.
- A global `FallbackPolicy` was rejected: it also applies to unmatched paths, turning required 404s (FR-028, boundary tests) into 401s.
- IF-19: R5 is now true of the code; R12 was rewritten, and the runner (and CI) default to the highest-numbered spec with a Playwright project.
- The IF-01/IF-02 bullets in all 38 prompts and the CI bullet of IF-09 were rewritten to describe the corrected behaviour; spec-0002's "introduce a global guard" bullet and spec-0004's fallback-policy bullet were removed as done.

Re-analysis at `d7b9108`: no other finding changes. The demo was re-run because `src/` changed (see `evidence/demo-run.json`).

## Maintainer decisions (2026-11-15)

- **IF-06:** approved. GOZ-0040 is registered (MVP, prerequisites GOZ-0001 and GOZ-0002, `promptRevision` 1); `scripts/validate_docs.py` now accepts contiguous specs beyond the original 40.
- **IF-07:** add an MVP spec for external OIDC. Drafted as `docs/prompts/spec-0041.md`, marked PROPOSED and not registered: the maintainer approves the draft and its prerequisites (proposed GOZ-0001 and GOZ-0040) before the steward registers it.

## Implementation defects

None found that need a code fix before the PR. I checked:
- session fixation (a new key on every sign-in);
- sliding renewal capped at the absolute expiry;
- revocation by row deletion plus stamp rotation;
- lockout that also revokes sessions;
- enumeration parity, including the dummy hash for unknown and locked accounts;
- fail-closed outage handling, which `DatabaseOutageTests` covers;
- no secrets in operator command output.

## Not asserted

- I did not run the test suites or the demo again. Demo results come from `evidence/demo-run.json`, and test results come from the `progress.yaml` gates (114 .NET tests).
- Helm has never been installed on a live cluster.
- This analysis did not commit, push, merge, switch branches or edit the registry.
