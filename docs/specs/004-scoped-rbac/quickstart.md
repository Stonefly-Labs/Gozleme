# Quickstart: Scoped RBAC Permission Foundation

## Prerequisites

- Docker with Compose, .NET 10 SDK, Node 24 (the same as GOZ-0003). No new services, packages or configuration keys.
- An existing local volume needs **no** `make reset`: no database role is added. `make up` runs the migrator, which applies `20261118000000_ScopedRbac`. Every existing member then holds `admin` and `member` (option B).

## Run the automated checks

```bash
make test       # .NET unit + Testcontainers integration (real PostgreSQL 17) and Vitest
make lint
make validate
make demo       # cumulative Playwright run; goz-0004 is now the highest project
```

Key suites: `RbacIsolationTests` (database matrix as the app and owner logins),
`OrganizationPermissionContractTests` (endpoint → action pin, role table equals
`contracts/permissions.md`), `RoleAssignmentTests` (API matrix, next-request effect, last admin,
revocation on demotion), `RbacMigrationTests` (pre-existing rows read `{admin,member}`), plus the
updated GOZ-0002/0003 suites.

## Demo walkthrough (what `make demo` drives in the browser)

1. `docker compose --profile demo run --rm seed` (the demo runs it): **Seeded Demo Org** has alice `{admin, member}` and carol `{member}`.
2. carol opens the organization page. She sees role badges, and no rename form, invitation panel or Remove buttons.
3. carol sends the rename request directly. The response is `403 {"error":"forbidden"}` and the name is unchanged.
4. alice ticks **admin** for carol and saves. The member list shows carol as `admin, member`.
5. carol, still signed in, renames the organization. It succeeds.
6. alice clears **admin** for carol. carol's next rename gets `403`, and the page shows "You no longer have permission to do that."
7. alice, now the only admin, tries to clear her own **admin**. She gets `409 last_admin` and the message "An organization needs at least one admin."
8. Demotion revokes invitations: carol (made admin again) creates an invitation, alice demotes carol, and the invitation disappears. Redeeming its code gives the generic invalid-invitation refusal.

Evidence lands in `docs/specs/004-scoped-rbac/evidence/` (`demo-run.json`, `rbac-evidence.json`,
`screenshots/`, and `screenshots/regression-goz-000N/` for earlier suites).

## Manual database spot check (optional)

Using the GOZ-0003 pattern (`e2e/tests/compose.ts` `psql` helper, token signed in Node), as
`gozleme_app` with carol's context:

```sql
UPDATE gozleme.memberships SET roles = '{admin,member}' WHERE user_id = '<carol>';   -- ERROR 42501
UPDATE gozleme.organizations SET display_name = 'x' WHERE id = '<demo org>';         -- UPDATE 0
```

With alice's context, changing carol's roles affects 1 row. Roll each transaction back.

## Expected telemetry

Jaeger (`:16686`, service `gozleme-api`) shows `gozleme.authz.decision` spans under `gozleme.org.*`
with action, resource type and `allow`/`deny`. Prometheus has `gozleme_authz_decisions_total`. Neither
contains a name, an email, a code, a signed context or an account id as a label.
