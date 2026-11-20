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

0. **Origin verified (publisher and capability endpoints, GOZ-0005).** Until the API has verified the recorded registry origin, these endpoints answer `503 {"error":"unavailable"}`, the same body as a database outage, before any membership check. It reflects installation state and reveals nothing about a tenant.
1. **Membership first.** A malformed organization id, an unknown organization and a non-member all get the identical empty `404` with `Cache-Control: no-store`. A non-member never sees `403`.
2. **Permission.** The caller's roles are read from the database in the request's transaction. The permitted actions are the union of each held role's actions. If the action is not permitted, the answer is `403 {"error":"forbidden"}` and nothing changes.
3. **Rule checks.** Validation (`400`), last member / last admin (`409`), then the change. Publisher and
   capability endpoints (GOZ-0005) check validation (`400`), then the target (`404`), then suspension (`409`).
   Publisher registration takes one permit from the caller's hourly budget after the permission check and
   before validation; an exhausted budget answers `429 {"error":"rate_limited"}`, so a non-member never sees `429`.
   For `manifest.validate` (GOZ-0006) the steps after permission are: target (`404`), suspension (`409`), the
   account's validation budget, shared through PostgreSQL (`429 {"error":"rate_limited"}`), media type and size
   (`415`/`413`), then validation: `200` when accepted; when rejected, the result body with `400` (malformed), `413`
   (too large) or `422` (any other problem) (GOZ-0006 maintainer decisions 2026-11-18, "shared-rate-limit" and
   "result-status-codes"). A non-member never sees `403`, `409` or `429`.
   For `manifest.publish` (GOZ-0007) the steps after permission are: target (`404`), suspension (`409
   {"error":"suspended"}`), the account's publication budget (`429`), media type and size (`415`/`413`), GOZ-0006
   validation unchanged (a rejection answers exactly as validate), version uniqueness by SemVer precedence (`409
   {"error":"version_exists"}`), signing key available (`503`), then `201`; GOZ-0008 adds the publish-time rules
   `publish.duplicate_entry` and `publish.nul_character` (`422`, one body: duplicates, then projection U+0000, then
   envelope U+0000) after validation and before version uniqueness, and two more `503 {"error":"unavailable"}` causes
   after version uniqueness: the rollout gap (an earlier revision of the capability without generation 1, maintainer
   ruling A7) and a child-table refusal at insert or in the completeness checks (maintainer ruling F4). Reads (`manifest.list`,
   `manifest.read`) check the target (`404`: publisher, capability, revision, child or generation), then answer `200`.

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
manifest.validate        manifest      deny   deny      deny    allow      deny
manifest.publish         manifest      deny   deny      deny    allow      deny
manifest.list            manifest      allow  allow     allow   allow      allow
manifest.read            manifest      allow  allow     allow   allow      allow
```

GOZ-0005 (contracts/permissions-delta.md): resources `publisher` and `capability`. `admin` no longer holds
every action. `publisher` is the first role that permits more than `member` (maintainer Q5).

GOZ-0006 (contracts/permissions-delta.md): `manifest.validate` on the existing `capability` resource, held by
`publisher` only (maintainer Q2). Validation stores nothing, so it adds no database enforcement row. 19 actions
over 5 resources; 18 scoped endpoints.

GOZ-0007 (contracts/permissions-delta.md, maintainer Q4): new resource `manifest`. `manifest.validate` moves to it
with its role row unchanged; `manifest.publish` is held by `publisher` only and also needs a held handle claim (`409
suspended` in the handler, and the insert policies below); `manifest.list` and `manifest.read` are member-level. 22
actions over 6 resources; 27 scoped endpoints.

GOZ-0008 (contracts/permissions-delta.md): no new action or resource. The five child and projection-generation
reads reuse the member-level `manifest.list` and `manifest.read`. 22 actions over 6 resources; 32 scoped endpoints.
There is no write route on children or generations (`POST`, `PUT`, `PATCH`, `DELETE` answer `405`) and no route for
re-projection.

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
| `POST …/publishers/{publisherId}/capabilities/{capabilityId}/manifests/validate` | `manifest.validate` |
| `POST …/publishers/{publisherId}/capabilities/{capabilityId}/revisions` | `manifest.publish` |
| `GET …/publishers/{publisherId}/capabilities/{capabilityId}/revisions` | `manifest.list` |
| `GET …/capabilities/{capabilityId}/revisions/{revisionId}` | `manifest.read` |
| `GET …/revisions/{revisionId}/native` · `/projection` · `/statement` · `/signature` | `manifest.read` |
| `GET …/publishers/{publisherId}/signing-keys` | `manifest.read` |
| `GET …/publishers/{publisherId}/signing-keys/{keyId}/public-key` | `manifest.read` |
| `GET …/publishers/{publisherId}/capabilities/{capabilityId}/children` | `manifest.list` |
| `GET …/capabilities/{capabilityId}/children/{childId}` | `manifest.read` |
| `GET …/capabilities/{capabilityId}/revisions/{revisionId}/children` | `manifest.read` |
| `GET …/revisions/{revisionId}/projections` | `manifest.read` |
| `GET …/revisions/{revisionId}/projections/{generationId}` | `manifest.read` |

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
| publish a revision (revision, attestation, first signing key) | `manifest_revisions_insert`, `revision_attestations_insert` and `publisher_signing_keys_insert` admit `publisher` holders, only while the publisher holds its handle claim; a member inserts key generation 1 only |
| add a successor signing key; retire a key | `is_operator()` only (the owner login with no account context), through `signing-key-succession` |
| record children and generation 1 of a published revision | `capability_children_insert`, `revision_children_insert` and `projection_generations_insert` admit `publisher` holders while the publisher holds its handle claim, and only for the revision the caller publishes in this transaction (`published_by = current_account_id() AND published_at = now()`, maintainer ruling F2); members insert non-duplicate children and generation 1 only; the operator (`is_operator()`) inserts install-step children and later generations (`projection-regenerate`) |
| change or delete a child, child record or projection generation | refused for every login, including the owner (`immutable_row_guard`); `gozleme_app` holds `SELECT, INSERT` only |
| change or delete a revision, attestation, signing key or retirement | refused for every login, including the owner (`immutable_row_guard`); `gozleme_app` holds `SELECT, INSERT` only |

Reads stay member-level at the database. The application withholds the invitation list from
non-admins.

## Telemetry

Span `gozleme.authz.decision`: `gozleme.authz.action`, `gozleme.authz.resource_type`,
`gozleme.authz.outcome` (`allow` | `deny`). Counter `gozleme.authz.decisions` with the same three
labels. No names, emails, codes, account ids or roles appear in either.
