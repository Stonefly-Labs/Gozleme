# Quickstart: Organizations and Membership (GOZ-0002)

Validates the slice end to end on the local Compose stack. Contracts:
[`contracts/organizations-api.openapi.yaml`](contracts/organizations-api.openapi.yaml),
[`contracts/web-routes.md`](contracts/web-routes.md). Data: [`data-model.md`](data-model.md).

## Prerequisites

Docker with Compose v2, .NET 10 SDK, Node (see `.nvmrc`) with pnpm. No cloud services are needed.

## Automated

```bash
make test           # .NET unit + integration (Testcontainers) and web Vitest
make demo           # cumulative: goz-0000 → goz-0001 → goz-0002 (latest spec with a Playwright project)
make demo SPEC=goz-0002
make validate       # registry, prompts, register sync
```

`make demo` writes `docs/specs/002-organizations/evidence/demo-run.json` and saves screenshots to
`evidence/screenshots/`. Earlier suites' screenshots go to `evidence/screenshots/regression-goz-0000/`
and `regression-goz-0001/`.

## Manual walkthrough (expected observations)

1. `make up`, then `docker compose --profile demo run --rm seed`. Sign in at http://localhost:3000 as
   `alice@demo.gozleme.test` with `GOZLEME_DEMO_PASSWORD`.
2. Open **Organizations**. **Seeded Demo Org** is listed. Create **Demo Org Alpha** and **Demo Org Beta**.
   Each becomes active on creation, and the switcher lists all three.
3. Switch to Alpha with the switcher. The header and the Alpha page show Alpha as active. Reloading keeps it active.
4. On Alpha, **Create invitation**. The code is shown once. Reload: the invitation is listed without its code.
5. In a private window, sign in as `bob@demo.gozleme.test`, open **Join** and enter the code. Bob lands
   on Alpha. Entering the same code again as carol shows "This invitation is invalid or has expired."
6. As alice, remove bob on Alpha's member list. In bob's window, the next navigation to Alpha shows
   "Organization not found", Alpha leaves bob's switcher, and bob stays signed in.
7. As alice, try to leave Seeded Demo Org after removing carol. You are refused: "An organization must keep at least one member."

## Negative API checks (curl against the API, as the web tier calls it)

- Any `/api/v1/organizations…` request without a session → `401`.
- Bob requests `GET /api/v1/organizations/{alphaId}` after removal → `404` with an empty body,
  byte-identical to a random UUID and to the string `not-a-uuid`.
- `POST /api/v1/organizations` without `x-gozleme-client: web` → `403`.
- `POST /api/v1/invitations/redeem` with a used, revoked, expired or random code → the identical `400 {"error":"invalid_invitation"}`.
  The 11th attempt within a minute → `429`.
- `PATCH /api/v1/organizations/{id}/members/{userId}` and `…/members/{userId}/role` → `404/405`
  (the no-delegation boundary).

## Observability

Jaeger (http://localhost:16686), service `gozleme-api`: spans `gozleme.org.create`, `gozleme.org.select`,
`gozleme.org.invite_redeem` and `gozleme.org.member_remove`, joined to `gozleme-web` and `gozleme-web-browser`
spans in one trace. No span, metric or log contains an email, a display name or a code.
