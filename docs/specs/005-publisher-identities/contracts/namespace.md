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
