# Contract: Normalized projection v1 (GOZ-0006, FR-014)

A projection is a Gözleme-defined summary derived from the native document. Its version is separate from the
envelope version, the native format version and the adapter version. It never assigns child identities
(GOZ-0008), and nothing in it flows back into the native document.

## Shape

```json
{
  "projectionVersion": "1",
  "adapter": "mcp/1",
  "family": "mcp",
  "kind": "mcp-server",
  "nativeName": "io.example.demo/forecasts",
  "title": "Forecasts",
  "description": "Synthetic demo server.",
  "declaredVersion": "1.4.0",
  "attributes": { "protocolRevision": "2026-07-28", "mode": "stateless", "transports": ["streamable-http"] },
  "entries": [
    { "kind": "tool", "nativeName": "get_forecast", "title": "Get forecast" },
    { "kind": "resource", "nativeName": "regions" },
    { "kind": "prompt", "nativeName": "summarize_week" }
  ],
  "derived": ["/attributes/mode"]
}
```

- `title` and `description` are `null` when the native document has none. When `title` falls back to `nativeName`, `/title` is listed in `derived`.
- `declaredVersion` is the envelope `version`.
- Entries from arrays keep native document order. Entries from maps are in a fixed order, so the key order of the document never changes the projection (maintainer decision 2026-11-17, SC-003): OpenAPI paths and webhooks by key (ordinal), then methods in the order `get`, `put`, `post`, `delete`, `options`, `head`, `patch`, `trace`; AsyncAPI channels, then operations, each by key (ordinal).
- `derived` lists RFC 6901 pointers into the projection, in a fixed order.
- No other member exists. `ProjectionSchemaTests` validates every projection against `projection-v1.schema.json`, which is closed and lives in `src/Gozleme.Api/Manifests/Schemas/`.

## Per family

| Family | `kind` | `nativeName` | `title` | `description` | `attributes` | `entries` |
|---|---|---|---|---|---|---|
| `mcp` | `mcp-server` | `serverInfo.name` | `serverInfo.title` | `serverInfo.description` | `protocolRevision`; `mode` = `stateless` (2026-07-28) or `stateful` (2025-11-25), always derived; `transports[]`: distinct `remotes[].type` then `packages[].transport.type`, in order | `tool`, `resource`, `resource-template`, `prompt`: `name` and `title` of each |
| `a2a` | `a2a-agent` | `name` | — (derived from `name`) | `description` | `protocolBindings[]`: distinct `supportedInterfaces[].protocolBinding` | `skill`: `id` (nativeName) and `name` (title) |
| `api` | `openapi-api` | `info.title` | `info.title` | `info.description` | `openapiVersion` (the document's `openapi`) | `operation` per path and method, in the fixed order above: `operationId`, else `"<METHOD> <path>"` (derived, `/entries/<i>/nativeName`), title = `summary`; `webhook` (3.1) per webhook operation, same rule |
| `event` | `asyncapi-application` | `info.title` | `info.title` | `info.description` | `asyncapiVersion` | `channel` per `channels` key, title = `title`; `operation` per `operations` key, title = `title` |
| `skill` | `agent-skill` | `name` | — (derived) | `description` | `{}` | `[]` |
| `model` | `model` | `name` | `title` | `description` | `inputModalities[]`, `outputModalities[]`, `contextWindowTokens` and `maxOutputTokens` when present | `[]` |

## Never projected

Extension members (`x-*`, `_meta`, A2A `capabilities.extensions`, skill `metadata`), schemas, examples,
URLs, server addresses, security schemes, parameters, bodies, `SKILL.md` bodies, and every field not named
above.

## Boundary guarantees (User Story 3)

- The native document is returned unchanged, and `digest` is the SHA-256 of its UTF-8 bytes.
- Changing only an extension member, insignificant whitespace or the key order of a JSON object (maps included) changes the digest but not the projection. Only reordering an array (tools, skills) reorders entries.
- A defaulted or derived value is marked in `derived`, and the native document still shows what was submitted.
