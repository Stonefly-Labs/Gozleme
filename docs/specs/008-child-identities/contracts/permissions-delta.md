# Permissions Delta (GOZ-0008)

Applied to `docs/architecture/contracts/organization-permissions.md` during implementation.

- **No new action or resource.** The action table is unchanged: 22 actions over 6 resources.
- **27 → 32 scoped endpoints.** The new endpoints reuse the member-level `manifest.list` and `manifest.read` (spec
  Assumption).

## Endpoint → action (added rows)

| Endpoint | Action |
|---|---|
| `GET …/publishers/{publisherId}/capabilities/{capabilityId}/children` | `manifest.list` |
| `GET …/capabilities/{capabilityId}/children/{childId}` | `manifest.read` |
| `GET …/capabilities/{capabilityId}/revisions/{revisionId}/children` | `manifest.read` |
| `GET …/revisions/{revisionId}/projections` | `manifest.read` |
| `GET …/revisions/{revisionId}/projections/{generationId}` | `manifest.read` |

## Database writes (added row to the write table)

| Write | Enforced by |
|---|---|
| record children and generation 1 of a published revision | `capability_children_insert`, `revision_children_insert` and `projection_generations_insert` admit `publisher` holders while the publisher holds its handle claim; members insert non-duplicate children and generation 1 only; the operator (`is_operator()`) inserts install-step children and later generations |

## Order

- **Reads**: origin (`503`) → membership (`404`) → permission (`403`) → target (`404`: publisher, capability, revision,
  child or generation) → `200`.

## Boundary

- There is no create, update, replace or delete route for children or generations: `POST`, `PUT`, `PATCH` and `DELETE`
  on the child and projection routes answer `405`.
- There is no route for re-projection.
