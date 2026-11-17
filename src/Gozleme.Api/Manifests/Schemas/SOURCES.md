# Bundled schema sources

Completed by T006 (source URL, tag or commit, SHA-256 and licence for every file). Rows recorded so far:

## upstream/a2a-1.0.1/ (T004)

- **Source**: `specification/a2a.proto` at A2A tag `v1.0.1` (https://github.com/a2aproject/A2A), licence Apache-2.0.
- **Generator**: `protoc` with bufbuild `protoc-gen-jsonschema` v0.6.0 (github.com/bufbuild/protoschema-plugins), exactly as in A2A's `scripts/proto_to_json_schema.sh` at `v1.0.1` (the same `// --8<--` and `// protolint:` line filter, `--jsonschema_opt=target=json`), but **without the final `jq` stitching step** (maintainer decision, 2026-11-17). Run on 2026-11-17 in Docker `golang:1.25-bookworm` with `libprotoc 3.21.12`, `go1.25.14`, and googleapis at commit `c32bc6b4be2cb07ef3e06c57f2b44e2451a9b7c7`.
- **Command**: `grep -v -e "// --8<--" -e "// protolint:" specification/a2a.proto > $T/a2a.proto && protoc -I$T -Ispecification -I<googleapis> --jsonschema_out=<out> --jsonschema_opt=target=json $T/a2a.proto`
- **Output**: 47 files, unmodified; each file's `$id` equals its file name, and every relative `$ref` resolves within the set. Per-file SHA-256 values are listed by T006.
- **Why unstitched**: A2A's stitched `a2a.json` (published at https://a2a-protocol.org/v1.0.1/spec/a2a.json, SHA-256 `ba4b702b5edbdcbfe972c272484eb38e1fcd72af3c0b7eaa114b50e97455d8dd`, which our stitched run reproduced byte for byte) deletes each `$id` but keeps 33 relative `$ref`s to these files, so it cannot be compiled on its own.
- **Known gap**: the generator does not emit `required` for `(google.api.field_behavior) = REQUIRED` fields. `gozleme/a2a-strict-1.0.1.json` (T005) restates them.
