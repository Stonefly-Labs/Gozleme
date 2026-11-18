# Contract delta: Organization permissions (GOZ-0006)

This is applied to the living `docs/architecture/contracts/organization-permissions.md` in this slice
(GOZ-0004-IF-13). `OrganizationPermissionContractTests` pins the result.

## New resources

None. The action is on the existing `capability` resource (agent choice disclosed in `progress.yaml`).

## Role table row (appended)

```text
manifest.validate        capability    deny   deny      deny    allow      deny
```

`publisher` only (maintainer Q2). `admin` does not hold it. No other role changes meaning.

## Endpoint → action row

| Endpoint | Action |
|---|---|
| `POST …/publishers/{publisherId}/capabilities/{capabilityId}/manifests/validate` | `manifest.validate` |

## Decision procedure addition

For `manifest.validate`, the steps after permission are:
1. target (`404`);
2. suspension (`409`);
3. the account's validation budget, shared through PostgreSQL across API instances (maintainer decision 2026-11-18, "shared-rate-limit", T099) (`429 {"error":"rate_limited"}`);
4. media type and size (`415`/`413`);
5. validation (`200` with an `accepted` or `rejected` outcome).

A non-member never sees `403`, `409` or `429`.

## Database enforcement rows

None. Validation reads only the existing `capabilities`, `publishers` and `handle_claims` rows under the
existing member-level read policies, and writes nothing (FR-017). No role-gated write exists to mirror in
PostgreSQL.

## Counts after GOZ-0006

- 19 actions over 5 resources.
- `OrganizationPermissionContractTests` expects **18** scoped endpoints.
