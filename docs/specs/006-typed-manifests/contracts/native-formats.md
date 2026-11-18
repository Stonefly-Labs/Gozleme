# Contract: Native formats and adapters v1 (GOZ-0006)

The upstream versions are pinned in research R1. The bundled schema files and their digests are listed in
`src/Gozleme.Api/Manifests/Schemas/SOURCES.md`.

Each adapter has its own version. Supporting a new native version adds a new adapter version or
`formatVersion` and never changes an existing one (FR-005). Adapters perform no network, file or process I/O
and execute no content (FR-006).

## Family → format matrix

| `family` | `native.format` | `formatVersion` | `encoding` | Adapter |
|---|---|---|---|---|
| `mcp` | `mcp-server-manifest` | `1` | `json` | `mcp/1` |
| `a2a` | `a2a-agent-card` | `1.0` | `json` | `a2a/1` |
| `api` | `openapi` | `3.1`, `3.0` | `json`, `yaml` | `api/1` |
| `event` | `asyncapi` | `3.0.0` | `json`, `yaml` | `event/1` |
| `skill` | `agent-skills` | `2026-11-17` | `markdown` | `skill/1` |
| `model` | `gozleme-model-card` | `1` | `json` | `model/1` |

- A format that belongs to another family, or an unlisted `formatVersion`, is `unsupported_format`.
- An encoding outside the row is `malformed`.

## Rules common to every adapter

- **Unknown members**: a member outside the pinned or overlay schema is `unknown_field`, unless it is under the format's extension mechanism.
- **Extension members**: preserved verbatim in the native bytes and never projected.
- **Reserved vocabulary**: applied to envelope names, to every name inside extension mechanisms (nested values included), to the member names of closed native objects, and to the fields listed per adapter, after look-alike folding, camelCase splitting and separator splitting (research R6).
- **Secrets**: secret pattern list v1 applies to every string value and member name ([secret-patterns.md](./secret-patterns.md)).
- **References**: `$ref`/`$dynamicRef` not starting with `#` is `external_reference`, and so is an unbundled `$schema`/`jsonSchemaDialect`. Nothing is fetched.

## `mcp/1` — MCP server manifest v1 (maintainer decision P4)

Schema: [mcp-server-manifest-v1.schema.json](./mcp-server-manifest-v1.schema.json).

**Shape**: a closed root object.

| Member | Type |
|---|---|
| `protocolRevision` | `2026-07-28` or `2025-11-25` (required) |
| `serverInfo` | MCP `Implementation` (required) |
| `capabilities` | MCP `ServerCapabilities` (required) |
| `instructions` | optional |
| `tools[]` | MCP `Tool` |
| `resources[]` | MCP `Resource` |
| `resourceTemplates[]` | MCP `ResourceTemplate` |
| `prompts[]` | MCP `Prompt` |
| `remotes[]` | `server.json` 2025-12-11 remote transport |
| `packages[]` | `server.json` 2025-12-11 `Package` |
| `_meta` | extension object |

Each primitive is validated against the `$defs` of the core schema of **the declared revision**:
- `2026-07-28` (stateless: no `initialize`, no `Mcp-Session-Id`; SEP-2575, SEP-2567);
- `2025-11-25` (stateful: sessions).

A field valid only in the other revision is `unknown_field`, for example `execution.taskSupport` on a tool under `2026-07-28`.

**Consistency rules** (`invalid_value`, rule `native.consistency`):
- non-empty `tools` needs `capabilities.tools`;
- `resources` or `resourceTemplates` need `capabilities.resources`;
- `prompts` need `capabilities.prompts`;
- names are unique within each primitive list, and resource `uri` values are unique.

**Advisory**: primitive lists are declared by the publisher. Upstream MCP treats live `server/discover` and
list results as authoritative, so Gözleme never presents these lists as verified behaviour.

**Extension mechanism**: `_meta` at the root, on `serverInfo` and on every primitive.
- Keys must be reverse-DNS prefixed, `^[a-z0-9-]+(\.[a-z0-9-]+)+/.+$`; otherwise `invalid_value`, rule `native.meta-key`.
- The prefix `io.modelcontextprotocol/` is reserved by MCP and is `reserved_claim` (rule `reserved.mcp-namespace`).
- `io.modelcontextprotocol.registry/*` other than `publisher-provided` is `reserved_claim` (rule `reserved.mcp-registry-meta`).

**Reserved fields**: the vocabulary is applied to `_meta` keys at every level.

**Secret-bearing fields**: the `value` and `default` of any `remotes[].headers[]` entry, `remotes[].variables{}` entry or `packages[].environmentVariables[]` entry where either of these holds:
- `isSecret` is `true`;
- the header `name` is case-insensitively `Authorization`, `Proxy-Authorization`, `Cookie`, `X-Api-Key`, `Api-Key` or `X-Auth-Token`.

**Embedded schemas**: tool `inputSchema`/`outputSchema` are JSON Schema 2020-12 by default, and a declared `$schema` must be a bundled dialect. External `$ref` is rejected.

## `a2a/1` — A2A Agent Card 1.0 (v1.0.1)

- **Schema**: the bundled JSON Schema generated from `specification/a2a.proto` at tag `v1.0.1`. It is closed upstream.
- **Extension mechanism**: `capabilities.extensions[]` (`uri`, `description`, `required`, `params`). The vocabulary is applied to member names inside `params`.
- **Reserved fields**: `/signatures` (JWS card signatures; signing is GOZ-0007's) is `reserved_claim`, rule `reserved.a2a-signatures`.
- **`securitySchemes` and `securityRequirements`**: these are declarations without values and are accepted.
- **Secret-bearing fields**: none. Pattern list only.
- **Native names**: `name` and skill `id`s are native names, never identity claims.
- **`provider`**: `provider.organization` and `provider.url` are descriptive text, kept verbatim in the native document. They are never projected and never trusted as the capability's owner; the owner is the Gözleme publisher (maintainer decision 2026-11-17).

## `api/1` — OpenAPI 3.1 and 3.0

- **Schemas**: OAS 3.1 schema `2026-11-05` (2020-12) and OAS 3.0 schema `2024-10-18` (draft-04). Both are closed upstream.
- **Document field**: `openapi` must match the declared line (`unsupported_format`, rule `native.version-mismatch`).
- **Extension mechanism**: `^x-` members. The vocabulary is applied to every `x-` name.
- **Native `deprecated` flags**: operation, parameter, header and schema `deprecated` flags are protocol-native element metadata. They are not reserved (maintainer approved A1, 2026-11-17). A `deprecated` name that is not the protocol's own field, such as `x-deprecated`, is still reserved.
- **Secret-bearing fields**: header and cookie parameters, and header objects, whose name is in the MCP list above. Their `example`, `examples.*.value` and `default` are checked.
- **References**: `#`-internal `$ref` is allowed and must resolve. Other `$ref` values, and a `jsonSchemaDialect` outside the bundled list, are `external_reference`.
- **Informational URLs**: Example `externalValue` and a Link `operationRef` that points outside the document are informational URLs under research R10 (maintainer decision 2026-11-17). They are kept as data, never fetched and never rejected for being URLs; the secret patterns still apply.

## `event/1` — AsyncAPI 3.0.0

- **Schema**: `spec-json-schemas` `3.0.0` (draft-07). It is closed upstream, root included.
- **Document field**: `asyncapi` must equal `3.0.0`.
- **Extension mechanism**: `^x-[\w\d.\-_]+$` members.
- **Native `deprecated` flags**: as for `api/1` (A1).
- **Reserved, secret and reference rules**: as for `api/1`. This covers message and messageTrait `headers` schemas' `default`/`examples` for the listed header names.

## `skill/1` — Agent Skills `SKILL.md` (snapshot 2026-11-17)

**Front matter** (closed):

| Field | Rule |
|---|---|
| `name` | 1–64 characters, `^[a-z0-9]+(-[a-z0-9]+)*$` (no leading, trailing or double hyphen) |
| `description` | 1–1024 characters |
| `license` | optional string |
| `compatibility` | 1–500 characters |
| `metadata` | string → string map |
| `allowed-tools` | space-separated string; experimental upstream |

- **Directory rule**: the upstream rule that `name` equals the parent directory name does not apply, because a single document has no directory. It is documented as not enforced.
- **Extension mechanism**: `metadata`. The vocabulary is applied to its keys.
- **Body**: kept and not parsed. It is scanned by the pattern list only, with line and column locations.

## `model/1` — Gözleme model card v1

- **Schema**: [model-card-v1.schema.json](./model-card-v1.schema.json). It is closed.
- **Extension mechanism**: `^x-` members at the root and in `modalities` and `limits`.
- **Reserved names**: a lifecycle or ownership name at the root is reported as both `reserved_claim` and `unknown_field` at the same pointer (every distinct rule per pointer is kept, maintainer decision 2026-11-17; research R6).
- **Secret-bearing fields**: none. Pattern list only.
