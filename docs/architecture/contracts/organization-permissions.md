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
3. **Rule checks.** Validation (`400`), last member / last admin (`409`), then the change.

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
```

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

Reads stay member-level at the database. The application withholds the invitation list from
non-admins.

## Telemetry

Span `gozleme.authz.decision`: `gozleme.authz.action`, `gozleme.authz.resource_type`,
`gozleme.authz.outcome` (`allow` | `deny`). Counter `gozleme.authz.decisions` with the same three
labels. No names, emails, codes, account ids or roles appear in either.
