# Contract: Organization actions, resources and built-in roles (v1)

The authoritative action/resource contract for organization-scoped operations (FR-003). The code in
`src/Gozleme.Api/Organizations/OrganizationPermissions.cs` must render exactly the table in the
fenced block below; `OrganizationPermissionContractTests` fails otherwise (FR-006). Later specs extend
this contract by adding actions and role rows in their own slices. They never edit a role's meaning
silently. GOZ-0018 replaces the evaluation behind the same decision point.

_Living contract. Introduced by GOZ-0004; moved here from `docs/specs/004-scoped-rbac/contracts/permissions.md`
by maintainer decision GOZ-0004-IF-13 (option B, 2026-11-16). Specs that add organization actions edit
this file in their own slice. The broader authorization architecture is `docs/architecture/authorization-contract.md`._

## Decision procedure

1. **Membership first.** A malformed organization id, an unknown organization and a non-member all get the identical empty `404` with `Cache-Control: no-store`. A non-member never sees `403`.
2. **Permission.** The caller's roles are read from the database in the request's transaction. The permitted actions are the union of each held role's actions. If the action is not permitted, the answer is `403 {"error":"forbidden"}` and nothing changes.
3. **Rule checks.** Validation (`400`), last member / last admin (`409`), then the change. Publisher and
   capability endpoints (GOZ-0005) check validation (`400`), then the target (`404`), then suspension (`409`).

Inputs are exactly `(the caller's roles in this organization, action)`. There are no conditions,
attributes, per-user or per-resource grants, deny rules or policy documents. A role is held only
through a membership in that organization and grants nothing anywhere else.

## Built-in roles

`admin`, `approver`, `member`, `publisher`, `reviewer`. Every membership holds `member`. No role implies
another. Users cannot create, rename or edit roles.

## Role table

```text
action                   resource      admin  approver  member  publisher  reviewer
organization.read        organization  allow  allow     allow   allow      allow
organization.select      organization  allow  allow     allow   allow      allow
organization.rename      organization  allow  deny      deny    deny       deny
membership.list          membership    allow  allow     allow   allow      allow
membership.leave         membership    allow  allow     allow   allow      allow
membership.remove        membership    allow  deny      deny    deny       deny
membership.roles.change  membership    allow  deny      deny    deny       deny
invitation.list          invitation    allow  deny      deny    deny       deny
invitation.create        invitation    allow  deny      deny    deny       deny
invitation.revoke        invitation    allow  deny      deny    deny       deny
publisher.list           publisher     allow  allow     allow   allow      allow
publisher.read           publisher     allow  allow     allow   allow      allow
publisher.register       publisher     allow  deny      deny    deny       deny
publisher.rename         publisher     allow  deny      deny    deny       deny
capability.list          capability    allow  allow     allow   allow      allow
capability.read          capability    allow  allow     allow   allow      allow
capability.create        capability    deny   deny      deny    allow      deny
capability.rename        capability    deny   deny      deny    allow      deny
```

GOZ-0005 (contracts/permissions-delta.md): resources `publisher` and `capability`. `admin` no longer holds
every action. `publisher` is the first role that permits more than `member` (maintainer Q5).

Account-level operations, outside this table and open to every signed-in account: create an
organization (the creator holds `admin` and `member`) and redeem an invitation (the redeemer holds
`member`).

## Endpoint → action

| Endpoint | Action |
|---|---|
| `GET /api/v1/organizations/{organizationId}` | `organization.read` |
| `POST …/{organizationId}/select` | `organization.select` |
| `PATCH …/{organizationId}` | `organization.rename` |
| `GET …/{organizationId}/members` | `membership.list` |
| `DELETE …/{organizationId}/members/{userId}` (`userId` = caller) | `membership.leave` |
| `DELETE …/{organizationId}/members/{userId}` (another member) | `membership.remove` |
| `PUT …/{organizationId}/members/{userId}/roles` | `membership.roles.change` |
| `GET …/{organizationId}/invitations` | `invitation.list` |
| `POST …/{organizationId}/invitations` | `invitation.create` |
| `DELETE …/{organizationId}/invitations/{invitationId}` | `invitation.revoke` |
| `GET …/{organizationId}/publishers` | `publisher.list` |
| `POST …/{organizationId}/publishers` | `publisher.register` |
| `GET …/{organizationId}/publishers/{publisherId}` | `publisher.read` |
| `PATCH …/{organizationId}/publishers/{publisherId}` | `publisher.rename` |
| `GET …/publishers/{publisherId}/capabilities` | `capability.list` |
| `POST …/publishers/{publisherId}/capabilities` | `capability.create` |
| `GET …/publishers/{publisherId}/capabilities/{capabilityId}` | `capability.read` |
| `PATCH …/publishers/{publisherId}/capabilities/{capabilityId}` | `capability.rename` |

The `DELETE …/members/{userId}` endpoint declares both actions and picks one from the target.

## Database enforcement (second, independent layer)

| Write | Database rule |
|---|---|
| rename organization | `organizations_update` admits admins only |
| create / revoke / delete invitations | invitation policies admit admins only (code holders may still claim one open invitation) |
| change roles; remove another member; add another account | `memberships_guard` requires admin |
| raise own roles | refused (an own-row change is still a role change and needs admin) |
| move a membership; rewrite `joined_*`; touch another's `selected_at` | refused |
| remove or demote the last admin | refused (serialized per organization) |
| redeem an invitation | own row with `{member}` only |
| register a publisher | `publishers_insert` and `handle_claims_insert` admit admins only, via `account_has_role(org,'admin')` |
| rename a publisher | `publishers_update` admits admins, only while the publisher holds its handle claim |
| create or rename a capability | `capabilities_insert` and `capabilities_update` admit `publisher` holders, only while the publisher is active |
| change a handle, name, id, organization or publisher; delete anything | refused (guards, revoked `DELETE`/`TRUNCATE`) for every login |
| reserve, release or reclaim a handle | the owner login only, through the operator commands; `handle_claims_guard` limits it to one-way transitions |

Reads stay member-level at the database. The application withholds the invitation list from
non-admins.

## Telemetry

Span `gozleme.authz.decision`: `gozleme.authz.action`, `gozleme.authz.resource_type`,
`gozleme.authz.outcome` (`allow` | `deny`). Counter `gozleme.authz.decisions` with the same three
labels. No names, emails, codes, account ids or roles appear in either.
