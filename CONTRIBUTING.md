# Contributing to Gözleme

Thanks for helping. Gözleme is built spec-by-spec; please read the
[constitution](.specify/memory/constitution.md) and [AGENTS.md](AGENTS.md) first.

## Workflow

1. Branch from `main` using `chore/*`, `feature/*` or `bugfix/*` (lowercase, e.g.
   `feature/goz-0015-local-semantic-search`). CI rejects other names.
2. Feature work follows the installed Spec Kit lifecycle (specify → plan → tasks → implement)
   for the relevant prompt in `docs/prompts/`. Do not add custom Spec Kit templates.
3. Keep the cumulative stack runnable: `make up` must still produce a healthy dashboard, and
   existing demos must keep passing (`make demo`).
4. Run `make lint test validate` before pushing.
5. Open a PR using the template. Include tests, demo evidence and real, sanitized screenshots for
   visual changes.
6. A human maintainer reviews and merges. Agents never merge, enable auto-merge or push to `main`.

## Standards

- Tenant isolation and fail-closed authorization are non-negotiable: security-relevant features
  need negative tests (cross-tenant, unauthorized, stale/revoked).
- Never commit secrets. Local credentials live in `.env` (git-ignored); only throwaway defaults
  appear in `.env.example`.
- Pin dependency versions (`Directory.Packages.props`, exact npm versions, lockfiles).
- Do not create empty frameworks or speculative abstractions; build the smallest secure slice.

## Reporting security issues

Do not open public issues for vulnerabilities — see [SECURITY.md](SECURITY.md).
