# Contract: Registry origin, identifiers and namespace (v1)

Pinned by `NamespaceRulesTests` (unit tests, run under `InvariantGlobalization=true`) and by the table `CHECK`s.
Changing anything here is a contract change.

## Origin

- Configured as `Gozleme:Registry:Origin` and recorded once by the migrator (`gozleme.registry_origin`). It never changes afterwards.
- Form: lowercase ASCII host name with labels of `[a-z0-9-]`, no label starting or ending with `-`, labels 1–63 characters, `.` between labels, at most 253 characters in total. It has no scheme, path, port, user information or trailing dot.
- A missing or invalid configured value, or one that differs from the recorded value, makes the migrator and the API refuse to proceed. Nothing is rewritten.
- The origin is not a secret and is shown wherever canonical IDs are shown.

## Identifiers

| Kind | Form | Stored |
|---|---|---|
| Publisher ID | `gz://<origin>/pub/<uuid>` | `publishers.id` (UUIDv7) |
| Canonical capability ID | `gz://<origin>/cap/<uuid>` | `capabilities.id` (UUIDv7) |
| Friendly alias | `<handle>/<capability-name>` | `alias_reservations.alias` |

- `<uuid>` is lowercase, hyphenated and 36 characters long.
- An identifier never embeds an organization ID, a handle, a capability name or a display name.
- Identifiers are never reassigned or reused. An alias is reserved to its capability forever, including after its handle is reclaimed.
- The publisher ID form is a maintainer decision (2026-11-17), as permanent as the capability ID form (Q4). In API responses both are named `canonicalId`.
- A handle resolves to a publisher **only through its held claim** (`handle_claims.status = 'held'`). `publishers.handle` is not unique: after a reclaim, a suspended publisher and the new holder show the same handle. The database requires a held claim's handle to equal its publisher's handle.
- Alias resolution (alias → canonical ID) is **not offered in v1**. `gozleme_app` may only insert into `alias_reservations`. A later spec that resolves aliases must add a read path, which changes the pinned `InstanceClaims` privileges and needs security review.

## Handles and capability names

- One rule: `^[a-z][a-z0-9]*(-[a-z0-9]+)*$`, which allows lowercase ASCII letters, digits and single hyphens, starts with a letter and does not end with a hyphen.
- Handles are 2–39 characters and capability names 2–64.
- Input is compared ordinally and is never trimmed, case-folded, normalized or transliterated. Anything else gives `400 validation_failed`.
- Handles are unique across the installation among live entries (held, blocked, earmarked). A capability name is unique within its publisher. Aliases are unique across the installation, forever.

## Reserved handles (fixed list)

`admin`, `api`, `capabilities`, `capability`, `gozleme`, `operator`, `publisher`, `publishers`,
`registry`, `root`, `security`, `support`, `system`, `www`

These handles are refused as a validation error for every caller, including operator reservations. The
list is separate from the operator's blocked and earmarked reservations.

## Child IDs (GOZ-0008)

Additive section (GOZ-0005-IF-12). Maintainer decision Q1, 2026-11-19, option A.

- Form: `gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>`. It is anchored on the canonical capability ID above.
- `<kind>` is the projection v1 entry kind: `tool`, `resource`, `resource-template`, `prompt`, `skill`, `operation`,
  `webhook` or `channel`.
- Only the child UUID (UUIDv7) is stored. The ID never embeds a native name, title, alias, handle, organization ID,
  entry position, revision, sequence or projection generation.
- Children have no friendly alias and no alias reservation.
- Match key, duplicates and the rest of the contract:
  [`docs/specs/008-child-identities/contracts/child-identity.md`](../../008-child-identities/contracts/child-identity.md).
