# Gözleme

Gözleme is an open-source (MIT), self-hostable, federated, multi-organization **AI capability
registry** for MCP servers/tools/resources/prompts, A2A agents and skills, packaged skills,
models, APIs and AsyncAPI/event capabilities. It is a registry, **not** a production API
gateway or AI request broker.

> **Status:** foundation. The running stack is a status dashboard over a .NET 10 API and
> PostgreSQL (GOZ-0000), with local accounts (GOZ-0001), organizations with single-use
> invitations (GOZ-0002), database row-level security (GOZ-0003) and built-in organization roles
> enforced by the API and the database (GOZ-0004). The catalog and every other product feature
> arrive through the specs in
> [`docs/roadmap/spec-register.md`](docs/roadmap/spec-register.md).

## Quick start

Prerequisite: Docker with Compose v2. No cloud account, hosted identity provider or AI/embedding
service is needed.

```bash
make up                      # docker compose up -d --build --wait
open http://localhost:3000   # dashboard
open http://localhost:16686  # traces (Jaeger): browser → web → API → PostgreSQL
open http://localhost:3001   # Grafana: stored logs (Loki), traces, metrics (Prometheus)
curl -s http://localhost:8080/api/v1/status
make down                    # or `make reset` to also delete the database volume
```

Override host ports with `GOZLEME_WEB_PORT`, `GOZLEME_API_PORT`, `GOZLEME_DB_PORT` (copy
`.env.example` to `.env`). Local credentials in `.env.example` are throwaway values for a
disposable database.

## Development commands

| Command | Purpose |
|---|---|
| `make bootstrap` | Install pnpm packages and Python governance deps (`.venv`) |
| `make build` | Build backend (Release) and dashboard |
| `make test` | Backend unit + Testcontainers integration tests, frontend unit tests |
| `make lint` | `dotnet format --verify-no-changes`, ESLint, TypeScript |
| `make up` / `down` / `logs` / `ps` / `reset` | Run the Compose stack |
| `make validate` | Registry/manifest schemas, prompts, generated register sync, branch rules |
| `make helm-lint` | Lint and render the Helm chart |
| `make demo SPEC=GOZ-NNNN` | Cumulative autonomous Playwright demo with screenshots and run record |

Toolchain for working outside containers: .NET SDK per [`global.json`](global.json), Node 24
([`.nvmrc`](.nvmrc)) with pnpm via Corepack, Python 3.

## Repository layout

| Path | Contents |
|---|---|
| `src/Gozleme.Api` | .NET 10 ASP.NET Core modular-monolith host (status API, migrations) |
| `tests/` | xUnit unit and Testcontainers integration tests |
| `web/` | Next.js dashboard |
| `e2e/` | Playwright autonomous demo |
| `deploy/` | Postgres role bootstrap, OpenTelemetry collector config, Helm chart |
| `docs/` | Architecture, decisions, engineering process, spec prompts, Spec Kit specs |
| `.gozleme/` | Authoritative machine-readable manifest and spec registry |
| `.claude/`, `.specify/` | Agent skills/agents and the installed Spec Kit |

## Observability

OpenTelemetry is foundational: browser, web, API and migrator emit traces (and the .NET
services metrics and logs) over OTLP to a local collector, with Jaeger and a Prometheus scrape
endpoint in the default offline stack; logs are stored in Loki, metrics in Prometheus, and
Grafana views everything with log ↔ trace links. See
[`docs/engineering/local-development.md`](docs/engineering/local-development.md#observability-foundational).

## How work happens

Specs are driven through the installed Spec Kit by the `drive-speckit` workflow
([`docs/engineering/agent-workflow.md`](docs/engineering/agent-workflow.md)). All changes land via
pull requests from `chore/*`, `feature/*` or `bugfix/*` branches; only a human maintainer merges
([`docs/engineering/branching-and-pr-policy.md`](docs/engineering/branching-and-pr-policy.md)).
The project constitution is [`.specify/memory/constitution.md`](.specify/memory/constitution.md).

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## Source fidelity

The design history confirms **95 decisions**, but verbatim source text for every early Round 2
decision was not available. [`docs/decisions/decision-register.md`](docs/decisions/decision-register.md)
marks unavailable per-decision source text explicitly rather than inventing it.

## License

[MIT](LICENSE)
