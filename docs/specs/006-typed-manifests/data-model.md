# Data Model: Typed Manifests for Capability Families

**Storage**: none. Validation is stateless (Q1, FR-017, FR-027). The slice adds no table, column, migration,
function or grant. Every entity below is an in-memory value that lives for one request, or a bundled,
read-only contract file.

## Read from existing tables (GOZ-0005)

| Table | Read | Why |
|---|---|---|
| `memberships` | roles of the caller | `OrganizationAccess.AuthorizeAsync` (`manifest.validate`) |
| `publishers` | `id`, `organization_id`, `handle` | target lookup, alias for the identity check |
| `capabilities` | `id`, `organization_id`, `publisher_id`, `name` | target lookup, canonical ID and alias |
| `handle_claims` | held claim for the publisher | active versus suspended (`PublisherStore.IsActiveAsync`) |
| `registry_origin` | verified origin | canonical ID rendering (`RegistryOriginReader`) |

These are all existing member-level reads under the existing forced RLS policies. Nothing is written.

## Entities (in memory)

### ManifestEnvelope (v1)

Contract: [contracts/manifest-envelope-v1.schema.json](./contracts/manifest-envelope-v1.schema.json).

| Field | Type | Rules |
|---|---|---|
| `envelopeVersion` | int | `1`; anything else is `unsupported_version` |
| `family` | enum | `mcp`, `a2a`, `model`, `skill`, `api`, `event`; else `unsupported_family` |
| `capability` | string | must equal the target's canonical ID or alias; else `identity_mismatch` |
| `version` | string | SemVer 2.0.0 syntax; else `invalid_value` |
| `title`, `summary` | string? | 1–100 and 1–500 characters; secret patterns apply |
| `native.format`, `native.formatVersion` | string | must be a row of the family's format matrix; else `unsupported_format` |
| `native.encoding` | enum | allowed for the format; else `malformed` |
| `native.document` | string | ≤ 512 KiB of UTF-8; its bytes are the native bytes |

Any other member is `unknown_field`, and a reserved name is `reserved_claim`.

### NativeDocument

| Field | Notes |
|---|---|
| `bytes` | the UTF-8 encoding of `native.document`, unchanged (a BOM is kept) |
| `digest` | `sha256:` followed by lowercase hex over `bytes` |
| `tree` | a `ManifestNode` (see below), or the front-matter tree for `SKILL.md` |

### ManifestNode

An immutable parsed tree. Each node is one of object (ordered members), array, string, number (kept as
source text), boolean or null. Every node carries:
- `pointer`: RFC 6901;
- `line`, `column`: its start in the source text.

The envelope and the native document each have their own tree. Bounds: depth 64, 50,000 nodes, member names
of at most 1,024 characters.

### ProtocolAdapter

| Field | Notes |
|---|---|
| `id` | `mcp/1`, `a2a/1`, `api/1`, `event/1`, `skill/1`, `model/1` |
| `formats` | `(format, formatVersion, encodings)` rows ([contracts/native-formats.md](./contracts/native-formats.md)) |
| `schema` | the bundled strict overlay for each format version |
| `extensionPaths` | where extension members are allowed and preserved |
| `reservedFields`, `secretFields` | adapter-listed native locations (research R6, R7) |
| `Project(tree, envelope)` | builds the projection v1, pure |

Adapters are stateless singletons with no I/O. A new native version is a new `formats` row or a new adapter
`id`, never an edit to an existing one.

### ValidationProblem

`{category, rule, location, message}`. The category comes from the closed set of 11, and the rule and
message come from the fixed catalogue. `location` is `{part, pointer}`, `{part, line, column}` or `null`.
No field ever holds submitted content, except the pointer's member names, which are themselves checked
against the secret patterns.

### ValidationResult

- **Accepted**: `{outcome: accepted, envelope (without document), native {…, document, digest, byteLength}, projection}`.
- **Rejected**: `{outcome: rejected, problems[≤50], truncated}`.

The result is deterministic for a given body and target (FR-015).

### Projection (v1)

Contract: [contracts/projection-v1.md](./contracts/projection-v1.md).

`{projectionVersion: "1", adapter, family, kind, nativeName, title, description, declaredVersion, attributes, entries[], derived[]}`.

The projection is closed, carries no child identity, and nothing in it is written back.

### Sample

`web/src/samples/manifests/index.json` lists the samples (research R15). Each entry is:

`{id, family, title, file, expected: {outcome, category?, rule?, location?}}`

Each file is a complete envelope whose `capability` is the `{{capability}}` placeholder.

## State transitions

None. Validation has no lifecycle. The capability and publisher rows are unchanged, and lifecycle state
belongs to GOZ-0007 (revisions) and to later specs.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Gozleme:Manifests:ValidatePerMinute` | `30` | per account, in memory, per API instance |

The bounds in [contracts/manifest-validation.md](./contracts/manifest-validation.md) are contract constants,
not configuration.
