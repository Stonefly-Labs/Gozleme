# Bundled schema sources

Completed by T006 (source URL, tag or commit, SHA-256 and licence for every file). Rows recorded so far:

## upstream/a2a-1.0.1.json (T004)

- **Source**: `specification/a2a.proto` at A2A tag `v1.0.1` (https://github.com/a2aproject/A2A), licence Apache-2.0.
- **Generator**: A2A's own `scripts/proto_to_json_schema.sh` at `v1.0.1`, run on 2026-11-17 in Docker `golang:1.25-bookworm` with `libprotoc 3.21.12`, `protoc-gen-jsonschema v0.6.0` (github.com/bufbuild/protoschema-plugins), `go1.25.14`, `jq-1.6`, and googleapis at commit `c32bc6b4be2cb07ef3e06c57f2b44e2451a9b7c7`.
- **Command**: `go install github.com/bufbuild/protoschema-plugins/cmd/protoc-gen-jsonschema@v0.6.0 && GOOGLEAPIS_DIR=<googleapis> ./scripts/proto_to_json_schema.sh a2a-1.0.1.json`
- **SHA-256**: `ba4b702b5edbdcbfe972c272484eb38e1fcd72af3c0b7eaa114b50e97455d8dd`. It is byte-identical to the published https://a2a-protocol.org/v1.0.1/spec/a2a.json.
- **Notes**: the bundle is 2020-12, with 47 `definitions` keyed by **title** (for example `Agent Card`, with a space), and has no `$id`. `Agent Card`, `Agent Capabilities` and `Agent Card Signature` are closed (`additionalProperties: false`); only `Struct`, `Timestamp` and `Value` are open.
