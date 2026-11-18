# Permissions Delta (GOZ-0007, maintainer Q4)

Applied to `docs/architecture/contracts/organization-permissions.md` during implementation. The contract tests and
pinned counts change deliberately: **19 → 22 actions** and **18 → 27 scoped endpoints**.

## New resource `manifest`

```text
action                   resource      admin  approver  member  publisher  reviewer
manifest.validate        manifest      deny   deny      deny    allow      deny      (moved from capability; unchanged)
manifest.publish         manifest      deny   deny      deny    allow      deny
manifest.list            manifest      allow  allow     allow   allow      allow
manifest.read            manifest      allow  allow     allow   allow      allow
```

`manifest.publish` additionally requires a held claim for the publisher. This is checked in the handler (`409
suspended`) and in the insert policies of `manifest_revisions`, `revision_attestations` and `publisher_signing_keys`.

## Endpoint → action

| Endpoint | Action |
|---|---|
| `POST …/publishers/{publisherId}/capabilities/{capabilityId}/manifests/validate` | `manifest.validate` |
| `POST …/publishers/{publisherId}/capabilities/{capabilityId}/revisions` | `manifest.publish` |
| `GET …/publishers/{publisherId}/capabilities/{capabilityId}/revisions` | `manifest.list` |
| `GET …/publishers/{publisherId}/capabilities/{capabilityId}/revisions/{revisionId}` | `manifest.read` |
| `GET …/revisions/{revisionId}/native` · `/projection` · `/statement` · `/signature` | `manifest.read` |
| `GET …/publishers/{publisherId}/signing-keys` | `manifest.read` |
| `GET …/publishers/{publisherId}/signing-keys/{keyId}/public-key` | `manifest.read` |

## Order after permission

- **Publish**: target (`404`) → suspension (`409 suspended`) → publication budget (`429`) → media type (`415`) → size
  (`413`) → GOZ-0006 validation (`400`/`413`/`422` with the result body) → version (`409 version_exists`) → signing
  key (`503`) → `201`.
- **Reads**: target (`404`) → `200`.

## Boundary

These routes do not exist and answer `404` for every method, pinned by a boundary test:
- `…/signing-keys/rotate`, `/replace`, `/transfer`, `/delegate`;
- `POST /api/v1/publish`.

There is no update or delete route for revisions, attestations or keys (`405` on the revision route).
