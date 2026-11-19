# Publish Contract Delta: Duplicate Entries (GOZ-0008, maintainer Q2)

This delta changes GOZ-0007 publication (`POST …/capabilities/{capabilityId}/revisions`). GOZ-0006 validation, envelope
v1, projection v1, the adapters and the GOZ-0006 rule catalogue (40 rules) are **unchanged** (FR-005a, FR-021).

## The documented difference

Validate (`…/manifests/validate`) can answer `200 accepted` for a body that publish refuses with `422`, because the
duplicate rule is a publish-time rule (GOZ-0006-IF-07: `a2a/1` does not check skill `id` uniqueness, and `api/1` does
not check `operationId` uniqueness or a derived `"<METHOD> <path>"` that equals an `operationId`).

## Publish rule catalogue v1

| Rule | Category | Location | Message |
|---|---|---|---|
| `publish.duplicate_entry` | `invalid_value` | `{part: "projection", pointer: "/entries/<i>"}` | Two or more projection entries have the same kind and native name. |
| `publish.nul_character` | `invalid_value` | `{part: "projection", pointer: <the string>}` | A projection string contains the character U+0000. |

`publish.nul_character` (maintainer ruling F3 and the "nul-pointer" decision, 2026-11-19): PostgreSQL `text` and
`jsonb` cannot hold U+0000, which JSON allows as `\u0000` and validate accepts. Without the rule such a publication
answered an unhandled `500`. One problem is reported for every string of the projection that holds U+0000, in document
order, at that string's exact pointer, for example `/title`, `/description`, `/entries/3/nativeName`,
`/entries/3/title` or `/attributes/transports/0`. When both rules apply, the duplicate problems come first, then the
U+0000 problems, in one `422` body capped at 50 problems.

- `publish.duplicate_entry`: one problem is reported for **every** entry of a duplicate group, the first one included, ordered by index. The cap
  is 50 problems, with `truncated`.
- `projection` is a new location `part`. Its pointer is into the `projection` that validate returns for the same body.
- No name or value is echoed.
- The body is the GOZ-0006 `Rejected` shape; the status is `422`.

## Publication order (GOZ-0007, updated)

`503 unavailable` → empty `404` → `403` → empty `404` → `409 suspended` → `429` → `415` → `413` → GOZ-0006 validation
(byte-identical to validate on rejection) → **`422 publish.duplicate_entry` / `publish.nul_character`** → `409
version_exists` → `503 unavailable` (no usable signing key, an earlier revision of the capability without generation
1, research R6, or a child-table refusal, below) → `201`.

- The revision, its attestation, its generation 1, its new children and its child revision records commit together or
  not at all.
- The `201` body is unchanged.
- **Child-table refusal** (maintainer ruling F4, 2026-11-19): when the database refuses a child row during publication,
  either at insert (`23505` match index, `42501` guard, entry check or insert policy) or in the completeness checks
  (`23000` from `capability_children_recorded` or `manifest_revisions_children`), the answer is `503
  {"error":"unavailable"}` and nothing is stored. Publication runs the completeness checks itself before it returns
  (`SET CONSTRAINTS … IMMEDIATE`), so a refusal is answered there and never surfaces as a `500` at commit. Text the
  database cannot hold (`22P05`, `22021`) answers the same `503`; `publish.nul_character` refuses it first. These are
  defence in depth: an assignment computed by publication always satisfies the checks.
- The span outcome for the refusal is `rejected`.
