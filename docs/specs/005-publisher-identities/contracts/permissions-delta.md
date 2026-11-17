# Contract delta: Organization permissions (GOZ-0005)

This is applied to the living `docs/architecture/contracts/organization-permissions.md` in this slice
(GOZ-0004-IF-13). `OrganizationPermissionContractTests` pins the result.

## New resources

`publisher`, `capability`

## Role table rows (appended in this order)

```text
publisher.list           publisher     allow  allow     allow   allow      allow
publisher.read           publisher     allow  allow     allow   allow      allow
publisher.register       publisher     allow  deny      deny    deny       deny
publisher.rename         publisher     allow  deny      deny    deny       deny
capability.list          capability    allow  allow     allow   allow      allow
capability.read          capability    allow  allow     allow   allow      allow
capability.create        capability    deny   deny      deny    allow      deny
capability.rename        capability    deny   deny      deny    allow      deny
```

`admin` no longer holds every action. Its row in the code becomes an explicit list (research R9).
`publisher` now permits more than `member` for the first time (maintainer Q5). No other role changes
meaning.

## Endpoint → action rows

| Endpoint | Action |
|---|---|
| `GET …/{organizationId}/publishers` | `publisher.list` |
| `POST …/{organizationId}/publishers` | `publisher.register` |
| `GET …/{organizationId}/publishers/{publisherId}` | `publisher.read` |
| `PATCH …/{organizationId}/publishers/{publisherId}` | `publisher.rename` |
| `GET …/publishers/{publisherId}/capabilities` | `capability.list` |
| `POST …/publishers/{publisherId}/capabilities` | `capability.create` |
| `GET …/publishers/{publisherId}/capabilities/{capabilityId}` | `capability.read` |
| `PATCH …/publishers/{publisherId}/capabilities/{capabilityId}` | `capability.rename` |

## Database enforcement rows

| Write | Database rule |
|---|---|
| register a publisher | `publishers_insert` and `handle_claims_insert` admit admins only, via `account_has_role(org,'admin')` |
| rename a publisher | `publishers_update` admits admins, only while the publisher holds its handle claim |
| create or rename a capability | `capabilities_insert` and `capabilities_update` admit `publisher` holders, only while the publisher is active |
| change a handle, name, id, organization or publisher; delete anything | refused (guards, revoked `DELETE`/`TRUNCATE`) for every login |
| reserve, release or reclaim a handle | the owner login only, through the operator commands; `handle_claims_guard` limits it to one-way transitions |

Reads stay member-level in the database.
