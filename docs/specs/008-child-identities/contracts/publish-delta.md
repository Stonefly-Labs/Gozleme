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

- One problem is reported for **every** entry of a duplicate group, the first one included, ordered by index. The cap
  is 50 problems, with `truncated`.
- `projection` is a new location `part`. Its pointer is into the `projection` that validate returns for the same body.
- No name or value is echoed.
- The body is the GOZ-0006 `Rejected` shape; the status is `422`.

## Publication order (GOZ-0007, updated)

`503 unavailable` → empty `404` → `403` → empty `404` → `409 suspended` → `429` → `415` → `413` → GOZ-0006 validation
(byte-identical to validate on rejection) → **`422 publish.duplicate_entry`** → `409 version_exists` → `503
unavailable` (no usable signing key, or an earlier revision of the capability without generation 1, research R6) →
`201`.

- The revision, its attestation, its generation 1, its new children and its child revision records commit together or
  not at all.
- The `201` body is unchanged.
- The span outcome for the refusal is `rejected`.
