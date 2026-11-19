# Projection Generations and the Re-projection Command (GOZ-0008)

This is a living, versioned contract. Research: R5, R7.

## Generations

- Each revision has projection generation **1**, recorded with the revision (or by the install step for revisions
  published before GOZ-0008). Its bytes and digest **are** the revision's stored projection (`manifest_revisions.
  projection`, `projection_digest`). No copy is kept, and it is the only generation covered by the statement v1
  attestation.
- Later generations (N > 1) are additive, immutable and **not attested**. They are produced only by the operator
  command below.
- A generation has its own UUIDv7 `id`. A generation ID is never a child ID, and a child ID is never a generation ID.
  Each is "not found" where the other is expected.
- A generation never changes which children a revision declares: its multiset of (`kind`, `nativeName`) equals
  generation 1's. The *k*-th entry with key K in generation N is the same child as the *k*-th entry with key K in
  generation 1.
- Generations are compared only within one adapter ID, adapter version and projection version. Equal digests mean
  byte-identical projections (GOZ-0007-IF-09).

## API view

`GET …/revisions/{revisionId}/projections` returns:

```json
{
  "items": [
    { "id": "<uuid>", "generation": 1, "adapter": { "id": "mcp", "version": "1" }, "projectionVersion": "1",
      "digest": "sha256:…", "byteLength": 812, "attested": true, "createdAt": "…" },
    { "id": "<uuid>", "generation": 2, "adapter": { "id": "mcp", "version": "1" }, "projectionVersion": "1",
      "digest": "sha256:…", "byteLength": 812, "attested": false, "createdAt": "…",
      "comparedToPrevious": "identical" }
  ]
}
```

`comparedToPrevious` ∈ `identical`, `different`, `not_comparable`, and is absent for generation 1.
`GET …/projections/{generationId}` returns the exact bytes as an attachment.

## Operator command `projection-regenerate`

```bash
docker compose run --rm migrate projection-regenerate --revision <revision-uuid>
```

- It runs with the owner login (`ConnectionStrings:GozlemeMigrator`) and no account context, which `is_operator()`
  recognises. There is no API route. It needs no KEK.
- In one transaction, under the per-revision lock `gozleme.projections:<revision>`, it:
  1. reads the revision;
  2. checks the native digest;
  3. selects the adapter whose ID equals the stored adapter;
  4. parses the native bytes and projects them with that adapter, without re-running the validation rules;
  5. canonicalizes the projection (RFC 8785);
  6. checks the entry key multiset;
  7. inserts generation N + 1, even when it is byte-identical to the latest generation.

| Exit | Meaning | stderr word |
|---|---|---|
| 0 | generation recorded | — |
| 1 | failed (configuration, connection) | `failed` |
| 2 | unknown revision | `unknown_target` |
| 3 | refused, nothing recorded | `native_digest_mismatch` · `adapter_unavailable` · `projection_failed` · `entries_changed` |
| 64 | usage | usage text |

stdout on success (opaque IDs only):

```text
<revision-uuid> generation <n> <generation-uuid> digest equals generation <n-1>: yes|no|not_comparable
```

- **Telemetry**: span `gozleme.operator.projection_regenerate` (`gozleme.operator.exit_code`). Counter
  `gozleme.projection.generations` with outcome `recorded`, `identical` or `refused`.
- **Audit**, after commit: 1601 `projection_generation_recorded` (revision, generation ID, generation number) and
  1602 `projection_generation_refused` (the stderr word).
- Never printed, traced or logged: digests, names, titles or documents.

## Install step (in `migrate`)

The last step of every `migrate` run assigns generation 1 and children to revisions that have no generation 1. It works
per capability in `sequence` order under `gozleme.revisions:<capability>`, applies the duplicate rule for older
revisions, and commits per capability. It is idempotent. It writes the span `gozleme.children.install` and audit 1603
`children_installed` (capability, revision count). It changes no revision or attestation.
