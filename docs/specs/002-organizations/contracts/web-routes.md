# Web routes and operator commands (GOZ-0002)

GOZ-0001 routes are unchanged (`docs/specs/001-local-identity/contracts/web-routes.md`).

## Pages (Next.js; each gates server-side with `getSession()`)

| Path | Access | Behaviour |
|---|---|---|
| `/` | Signed in | Status dashboard. Now has a signed-in header with the organization switcher and links to **Organizations** and **Join** |
| `/organizations` | Signed in | The caller's organizations (active marked), a create form, and an empty state that offers "Create" and "Join with a code". Anonymous visitors → `/login?returnTo=%2Forganizations` |
| `/organizations/[organizationId]` | Signed in, member | Name and rename, members (remove, leave) and invitations (create with the code shown once, revoke). Non-member, unknown or malformed ID → the same "Organization not found" page (HTTP 404) |
| `/join` | Signed in | Code entry. A generic "This invitation is invalid or has expired." on failure. Anonymous visitors → `/login?returnTo=%2Fjoin` |

Database outage: pages render the existing honest degraded view without organization data.

## Route handlers (same-origin; every non-GET handler calls `isSameOrigin()` before `forwardToApi()`)

| Route | Methods | Forwards to |
|---|---|---|
| `/api/organizations` | GET, POST | `/api/v1/organizations` |
| `/api/organizations/[organizationId]` | GET, PATCH | `/api/v1/organizations/{id}` |
| `/api/organizations/[organizationId]/select` | POST | `…/select` |
| `/api/organizations/[organizationId]/members` | GET | `…/members` |
| `/api/organizations/[organizationId]/members/[userId]` | DELETE | `…/members/{userId}` |
| `/api/organizations/[organizationId]/invitations` | GET, POST | `…/invitations` |
| `/api/organizations/[organizationId]/invitations/[invitationId]` | DELETE | `…/invitations/{invitationId}` |
| `/api/invitations/redeem` | POST | `/api/v1/invitations/redeem` |

Path segments are encoded with `encodeURIComponent` before forwarding. The web tier never
interprets IDs; the API decides.

## Browser telemetry flows (`traced()`, bounded)

`org_create`, `org_rename`, `org_select`, `invite_create`, `invite_revoke`, `invite_redeem`,
`member_remove`, `member_leave`. Only the outcome is recorded, never form values or codes.

## Operator command changes

`seed` additionally creates or restores `carol@demo.gozleme.test` and the fixed-ID **Seeded Demo
Org** (members alice and carol; no invitations). Output carries opaque IDs only. There are no new
commands and no HTTP exposure.
