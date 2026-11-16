# Local development

Implemented by GOZ-0000; local accounts added by GOZ-0001. Only Docker with Compose v2 is required to run the stack; no hosted
Azure, AWS, OIDC or embedding provider is used.

| Task | Command |
|---|---|
| Start stack and wait for health | `make up` (`docker compose up -d --build --wait`) |
| Dashboard (sign-in required) | <http://localhost:3000> → `/login`; create an account at `/register` |
| API status / liveness / readiness | `curl localhost:8080/api/v1/status`, `/health/live`, `/health/ready` |
| Migrations | Applied automatically by the one-shot `migrate` service as `gozleme_migrator`; rerun with `docker compose run --rm migrate` |
| Seed data | `docker compose --profile demo run --rm seed` creates the synthetic accounts `alice@`, `bob@` and `lockout@demo.gozleme.test` with `GOZLEME_DEMO_PASSWORD` (idempotent; resets their passwords and lockout) |
| Operator account recovery | `docker compose run --rm -T --no-deps api reset-password --email <email> --password-stdin < pw.txt` (never as an argument; ends that account's sessions) and `docker compose run --rm --no-deps api unlock --email <email>` — there is no HTTP admin surface |
| Close registration | `GOZLEME_REGISTRATION_ENABLED=false docker compose up -d --no-deps api` |
| Traces (Jaeger) | <http://localhost:16686> — services `gozleme-web-browser`, `gozleme-web`, `gozleme-api`, `gozleme-migrator` |
| Metrics (Prometheus format) | <http://localhost:8889/metrics> |
| Viewer for logs, traces, metrics (Grafana) | <http://localhost:3001> (anonymous read-only; Explore → Loki/Jaeger/Prometheus) |
| Stored logs (Loki) | Grafana Explore, or `curl -G localhost:3100/loki/api/v1/query_range --data-urlencode 'query={service_name="gozleme-api"}'` |
| Stored metrics (Prometheus) | <http://localhost:9090> |
| Container logs | `make logs` (JSON with TraceId/SpanId) |
| Send telemetry from a process outside Compose | `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` (gRPC) or `:4318` (HTTP) |
| Tests | `make test` (backend unit + Testcontainers integration, frontend unit) |
| Lint | `make lint` |
| Governance validation | `make validate` |
| Autonomous demo | `make demo SPEC=GOZ-0003` → replays every earlier demo, then this spec's; writes `docs/specs/003-tenant-rls/evidence/` (earlier specs' screenshots under `screenshots/regression-goz-NNNN/`) |
| Logs / status | `make logs`, `make ps` |
| Stop / reset | `make down`; `make reset` deletes the database volume |

Database roles: `deploy/postgres/init/01-roles.sh` creates `gozleme_migrator` (database owner,
DDL) and `gozleme_app` (serving role, `NOSUPERUSER NOBYPASSRLS`) on first volume initialization.
If you change role passwords in `.env` after the volume exists, run `make reset`.

## Accounts and sessions (GOZ-0001)

Sign-in uses local email/password accounts only; no external identity provider is configured or
contacted. Sessions live in PostgreSQL (`gozleme.auth_sessions`, keyed by a SHA-256 of the
`gozleme_session` cookie value), so sign-out and "sign out everywhere" take effect immediately.
Five failed sign-ins lock an account for 15 minutes. While the database is unreachable the
dashboard shows an "unable to verify your session" view and the API answers
`503 {"error":"unavailable"}` (fail closed). `GOZLEME_SECURE_COOKIES` must stay `true` outside
plain-HTTP local development, and `GOZLEME_TRUSTED_PROXIES` lists the only networks whose
`X-Forwarded-For` is honoured for rate limiting.

The local PostgreSQL image is `pgvector/pgvector:pg17` so the optional extension is available;
it is never created or required. Set `GOZLEME_POSTGRES_IMAGE=postgres:17-alpine` to run without it.

## Organizations and membership (GOZ-0002)

Any signed-in account can create an organization at `/organizations` and becomes its first
member. An organization has a permanent opaque id and an editable display name that need not be
unique. The header switcher lists only your organizations; the selection is a stored preference
(`memberships.selected_at`), never authority: every organization page and API call re-checks
membership in PostgreSQL, so removing a member takes effect on their next request without signing
them out. A non-member, an unknown id and a malformed id all get the same "not found".

People join only through single-use invitation codes (`/join`): a member creates one, shares it
out of band, and it works once. Only a SHA-256 hash of each code is stored; codes are revocable
and expire. Membership is role-neutral until GOZ-0004: any member may rename, invite, revoke,
remove any other member or leave, and the last member can neither leave nor be removed. There is
no "add by email", no organization deletion and no administrative delegation.

| Setting | Default | Bounds |
|---|---|---|
| `Gozleme__Organizations__InvitationExpiryHours` | `168` (7 days) | 1–720 |
| `Gozleme__Organizations__RedeemPerMinute` (per account) | `10` | 1–100 |

The demo `seed` also creates `carol@demo.gozleme.test` and **Seeded Demo Org** (fixed id
`0193a000-0000-7000-8000-000000000002`, members alice and carol); every `seed` run restores it.

## Tenant row-level security (GOZ-0003)

Organization data is protected twice: the application membership check, then PostgreSQL
row-level security, enabled and **forced** (so it binds the owner login too) on
`organizations`, `memberships` and `invitations`. Each organization request runs in one
transaction that carries a short-lived account context, signed by the API
(`v1.<account>.<expires>.<hmac>`, 5 minutes). The database verifies it with a key held in
`gozleme_private`, which `gozleme_app` cannot read. A missing, forged, tampered or expired
context sees no tenant rows. The full contract is in
`docs/specs/003-tenant-rls/contracts/tenant-context.md`.

| Setting | Default (Compose) | Notes |
|---|---|---|
| `GOZLEME_CONTEXT_KEY` → `Gozleme__Rls__ContextKey` | `local-dev-context-key-not-a-secret-0001` | ≥ 32 characters; needed by `migrate`, `api` and `seed`; not by `reset-password`/`unlock` |

- **Existing volumes** need no `make reset` (no role changes). The next `make up` runs the migration and provisions the key.
- **Rotation**: change the key, re-run `docker compose up -d migrate`, then restart the API. In between, organization data is unreachable (fail closed), and the API logs audit event 1203 at startup.
- **Spot check**: `docker compose exec postgres psql -U gozleme_app -d gozleme -c "SELECT count(*) FROM gozleme.organizations"` returns `0`.
- **Not protected against**: SQL injected into a request (it runs with that request's own context), or compromised owner, superuser or context-key credentials.
- **Demo tenants**: `seed` acts as a seeded member of each demo organization, never with wider rights. It also creates `dave@demo.gozleme.test` and **Seeded Partner Org** (`0193a000-0000-7000-8000-000000000003`, member dave), the second tenant.

### Adding a tenant-owned table

The application role is granted DML on every new `gozleme` table automatically, so a new tenant-owned table must:

1. carry `organization_id uuid NOT NULL` with a foreign key to `gozleme.organizations(id)`;
2. in the **same** migration, `ENABLE` and `FORCE ROW LEVEL SECURITY` and create per-command policies of the form `CASE WHEN gozleme.rls_internal() THEN true ELSE organization_id IN (SELECT gozleme.account_organizations()) END`;
3. be added to `TableClassification.TenantOwned` (`src/Gozleme.Api/Persistence/TableClassification.cs`).

Global account-level tables go to `TableClassification.GlobalIdentity`. `RlsCatalogTests` fails on any unclassified or unprotected table, any extra `SECURITY DEFINER` function in `gozleme`, and any `PUBLIC` execute grant. Data migrations on tenant tables must sign a context; owner rights are not enough under FORCE.

## Observability (foundational)

OpenTelemetry runs through every tier from GOZ-0000 on (maintainer decision, GOZ-0000-IF-11):
browser → Next.js → API → PostgreSQL share one trace. The local collector
(`deploy/otel/collector.yaml`) strips credential-like attributes and values, exports traces to
Jaeger, metrics to a Prometheus scrape endpoint (stored by Prometheus) and logs to Loki; Grafana
views all three with log ↔ trace links. Local retention is 7 days (`make reset` deletes the
volumes). Jaeger keeps traces in memory locally, so traces (and log → trace links) do not survive a
Jaeger restart (GOZ-0000-IF-26). Grafana, Loki, Prometheus and Jaeger are unauthenticated and bound
to 127.0.0.1 for local use only; never expose them or reuse this setup in production (IF-25). The collector and Jaeger
are never health dependencies; stopping them does not affect the app. After editing the collector
config run `docker compose restart otel-collector`. Every new spec must instrument its code paths
(spans/metrics via the `Gozleme.Api` ActivitySource/Meter) and assert them in tests and the demo.
