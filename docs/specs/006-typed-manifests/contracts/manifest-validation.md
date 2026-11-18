# Contract: Manifest validation v1 (GOZ-0006)

This contract is portable and versioned. A breaking change needs a new envelope version, projection version
or problem-catalogue version. Adding a category is a contract change (FR-012). The endpoint is defined in
[manifest-api.openapi.yaml](./manifest-api.openapi.yaml), the envelope in
[manifest-envelope-v1.schema.json](./manifest-envelope-v1.schema.json), the formats in
[native-formats.md](./native-formats.md), and the projection in [projection-v1.md](./projection-v1.md).

## Request

`POST /api/v1/organizations/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}/manifests/validate`

The body is `application/json`, at most 1 MiB, and is a manifest envelope v1:

```json
{
  "envelopeVersion": 1,
  "family": "api",
  "capability": "gz://registry.demo.gozleme.test/cap/0193a000-0000-7000-8000-000000000011",
  "version": "1.4.0",
  "title": "Optional publisher title",
  "summary": "Optional publisher summary",
  "native": {
    "format": "openapi",
    "formatVersion": "3.1",
    "encoding": "yaml",
    "document": "openapi: 3.1.1\ninfo:\n  title: Forecasts\n  version: 2.0.0\npaths: {}\n"
  }
}
```

- `native.document` is always a string. Its native bytes are that string encoded as UTF-8, and the digest covers exactly those bytes.
- A nested JSON value in place of the string is `malformed`.
- `capability` is the target's canonical ID or its alias `<handle>/<name>` (FR-011).

## Order of checks

1. `503 {"error":"unavailable"}`: the database is unreachable, or the registry origin is not yet verified.
2. Empty `404`: a malformed or unknown organization, or a non-member.
3. `403 {"error":"forbidden"}`: the caller's roles lack `manifest.validate`.
4. Empty `404`: the publisher or capability ID is malformed, unknown or belongs to another organization, or the capability belongs to another publisher.
5. `409 {"error":"suspended"}`: the capability's publisher holds no handle claim.
6. `429 {"error":"rate_limited"}` with `Retry-After`: the account's validation budget is spent (30 per minute by default, `Gozleme:Manifests:ValidatePerMinute`; in memory, per API instance).
7. `415 {"error":"unsupported_media_type"}` unless `Content-Type` is `application/json`; `413 {"error":"too_large"}` over 1 MiB.
8. `200` with the validation result.

The body is not read before step 7, and it is never parsed before step 8.

## Result

```json
{ "outcome": "accepted",
  "envelope": { "envelopeVersion": 1, "family": "api", "capability": "gz://…", "version": "1.4.0",
                "native": { "format": "openapi", "formatVersion": "3.1", "encoding": "yaml" } },
  "native": { "format": "openapi", "formatVersion": "3.1", "encoding": "yaml",
              "document": "<the submitted string, unchanged>",
              "digest": "sha256:<64 lowercase hex>", "byteLength": 74 },
  "projection": { "projectionVersion": "1", "adapter": "api/1", "…": "see projection-v1.md" } }
```

```json
{ "outcome": "rejected",
  "problems": [
    { "category": "reserved_claim", "rule": "reserved.vocabulary",
      "location": { "part": "native", "pointer": "/info/x-verified" },
      "message": "This field asserts a reserved security claim, which a manifest cannot make." } ],
  "truncated": false }
```

- `location` takes one of three forms:
  - `{part, pointer}`: `part` is `envelope` or `native`, and `pointer` is RFC 6901 into that part's parsed tree. For `SKILL.md` it points into the front matter.
  - `{part, line, column}` (1-based, in Unicode scalar values): for `malformed`, and for secrets found in a `SKILL.md` body or in a YAML comment (OpenAPI/AsyncAPI YAML, `SKILL.md` front matter). Two `malformed` rules are located by envelope pointer instead: `parse.not-string` (`/native/document` is not a string) and `parse.syntax` for an encoding the format does not allow (maintainer decision 2026-11-17). For the envelope, the position is in the request body; for the native document, it is in the native string.
  - `null`: for `too_large`.
- A pointer never contains a value matched by the secret patterns. When a member name matches, the pointer is the parent object's (rule `secret.name`).
- `message` and `rule` come from the fixed catalogue below. No response, log or span contains a submitted value.

## Categories (closed, in contract order)

`malformed`, `too_large`, `unsupported_version`, `unsupported_family`, `unsupported_format`, `unknown_field`,
`reserved_claim`, `secret_value`, `identity_mismatch`, `external_reference`, `invalid_value`.

## Stages

1. Envelope parse (`malformed`, `too_large`: stop).
2. `envelopeVersion` (`unsupported_version`: stop); `family` (`unsupported_family`: stop); `native.format` and `formatVersion` (`unsupported_format`: stop); `native.encoding` (`malformed`: stop).
3. The remaining envelope checks, collected: `unknown_field`, `reserved_claim`, `invalid_value` (SemVer, lengths), `identity_mismatch`, `secret_value`.
4. Native parse (`malformed`, `too_large`: stop).
5. Native checks, collected: version consistency, schema, reserved claims, secrets, external references, entry cap.
6. If there are no problems, the projection is built.

Problems are de-duplicated by `(part, pointer, category, rule)`. Every distinct rule at a pointer is
reported, so a reserved envelope name is both `unknown_field` and `reserved_claim` (maintainer decision,
2026-11-17: all problems found are reported, spec edge case "Several problems at once"). They are ordered by part
(`envelope` first), then by pointer segment by segment (array indices numerically, names ordinally), then by
category in contract order, then by rule. The first 50 are returned, and `truncated` is `true` when more
exist.

## Bounds

| Bound | Value | Outcome |
|---|---|---|
| Request body | 1 MiB | `413 {"error":"too_large"}` |
| Envelope without `native.document` | 16 KiB | `too_large` |
| Native document | 512 KiB (UTF-8 bytes) | `too_large` |
| Nesting depth (each part) | 64 | `too_large` |
| Nodes per native tree | 50,000 | `too_large` |
| Member name length | 1,024 characters | `too_large` |
| Entries per family | 1,000 | `too_large` |
| Problems returned | 50 | `truncated: true` |

## Parse rules

These apply to the envelope and the native document:
- **JSON**: RFC 8259 with no comments and no trailing commas. A duplicate member name in any object is `malformed`. A leading U+FEFF in a native document is kept in the bytes and skipped by the parser.
- **YAML** (OpenAPI and AsyncAPI only): one document, YAML 1.2 core schema scalars. Anchors, aliases, explicit tags (including `!!str`), merge keys (`<<`), complex keys and duplicate keys are all `malformed`.
- **`SKILL.md`**: an optional BOM, then a `---` line, YAML front matter (the YAML rules above) and a closing `---` line. The body is kept but not parsed.
- **Invalid text**: invalid UTF-8 (a lone surrogate escape in the native string) and control characters other than TAB, LF and CR outside JSON strings are `malformed`.
- **Encoding mismatch**: a document that does not parse in its declared encoding, or an encoding the format does not allow (for example `yaml` for `a2a-agent-card`), is `malformed`.

## Rule catalogue (v1)

Each rule has one fixed English message. The authoritative list is `src/Gozleme.Api/Manifests/ManifestRules.cs`,
which `ManifestRuleCatalogueTests` pins to this table.

| Rule | Category |
|---|---|
| `parse.syntax`, `parse.duplicate-key`, `parse.yaml-feature`, `parse.encoding`, `parse.not-string`, `parse.front-matter`, `parse.empty` | `malformed` |
| `bound.body`, `bound.envelope`, `bound.native`, `bound.depth`, `bound.nodes`, `bound.name`, `bound.number`, `bound.entries` | `too_large` |
| `envelope.version` | `unsupported_version` |
| `envelope.family` | `unsupported_family` |
| `envelope.format`, `native.version-mismatch` | `unsupported_format` |
| `envelope.unknown-field`, `native.unknown-field` | `unknown_field` |
| `reserved.vocabulary`, `reserved.a2a-signatures`, `reserved.mcp-registry-meta`, `reserved.mcp-namespace` | `reserved_claim` |
| `secret.field`, `secret.pattern`, `secret.name` | `secret_value` |
| `identity.capability` | `identity_mismatch` |
| `ref.external`, `ref.dialect` | `external_reference` |
| `envelope.required`, `envelope.semver`, `envelope.length`, `native.schema`, `native.consistency`, `native.meta-key`, `ref.unresolved` | `invalid_value` |

## Determinism and storage

- **Deterministic**: the same body against the same capability gives a byte-identical result (FR-015).
- **Stateless**: nothing is stored in the database, in logs, telemetry or audit records, or in files (FR-017).
- **No audit event**: validation writes none (FR-023).
