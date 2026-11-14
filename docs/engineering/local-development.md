# Local development

Implemented by GOZ-0000. Only Docker with Compose v2 is required to run the stack; no hosted
Azure, AWS, OIDC or embedding provider is used.

| Task | Command |
|---|---|
| Start stack and wait for health | `make up` (`docker compose up -d --build --wait`) |
| Dashboard | <http://localhost:3000> |
| API status / liveness / readiness | `curl localhost:8080/api/v1/status`, `/health/live`, `/health/ready` |
| Migrations | Applied automatically by the one-shot `migrate` service as `gozleme_migrator`; rerun with `docker compose run --rm migrate` |
| Seed data | None yet — GOZ-0000 has no product entities. Later specs add deterministic synthetic seeds. |
| Tests | `make test` (backend unit + Testcontainers integration, frontend unit) |
| Lint | `make lint` |
| Governance validation | `make validate` |
| Autonomous demo | `make demo` → `docs/specs/000-repository-bootstrap/evidence/` |
| Logs / status | `make logs`, `make ps` |
| Stop / reset | `make down`; `make reset` deletes the database volume |

Database roles: `deploy/postgres/init/01-roles.sh` creates `gozleme_migrator` (database owner,
DDL) and `gozleme_app` (serving role, `NOSUPERUSER NOBYPASSRLS`) on first volume initialization.
If you change role passwords in `.env` after the volume exists, run `make reset`.

The local PostgreSQL image is `pgvector/pgvector:pg17` so the optional extension is available;
it is never created or required. Set `GOZLEME_POSTGRES_IMAGE=postgres:17-alpine` to run without it.
