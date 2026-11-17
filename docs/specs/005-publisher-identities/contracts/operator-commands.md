# Contract: Operator handle commands (v1)

One-shot modes of `Gozleme.Api`, like `migrate`, `seed`, `reset-password` and `unlock`. None of them is
reachable over HTTP, and no web or account-level surface exists for them (FR-032).

- **Login**: they connect as the owner, `ConnectionStrings:GozlemeMigrator` (maintainer Q11 = A), with no account context.
- **What they change**: they write only `gozleme.handle_claims`, and never a publisher, capability or alias row (FR-036, FR-037).
- **Compose**: `docker compose run --rm migrate <command> …`, using the migrator connection that the service already has.
- **Helm**: run as a one-off `Job` with the `gozleme-database` migrator secret. Operators run it by hand, and no chart hook runs it.

| Command | Effect | Audit |
|---|---|---|
| `handle-reserve --handle <h>` | inserts a `blocked` entry | 1406 |
| `handle-reserve --handle <h> --organization <uuid>` | inserts an `earmarked` entry for that organization | 1406 |
| `handle-release --handle <h>` | `blocked`/`earmarked` → `released` (an unused reservation only) | 1407 |
| `handle-reclaim --handle <h> [--organization <uuid>]` | `held` → `reclaimed`, which suspends the holding publisher permanently in this slice; then optionally earmarks the handle, in the same transaction | 1408 |

## Exit codes and output

| Code | Meaning | Example stderr |
|---|---|---|
| 0 | done | — (stdout: `Reclaimed handle claim <claim-id>; publisher <publisher-id> of organization <org-id> suspended`) |
| 1 | failed (database or unexpected error, class name only) | `Operator command failed (NpgsqlException).` |
| 2 | unknown target | `No such organization.` / `No held claim for that handle.` / `No unused reservation for that handle.` |
| 3 | invalid handle (namespace rule or reserved list) | `Invalid handle: …rule…` |
| 4 | handle not free | `That handle is already held or reserved.` |
| 64 | usage | `Usage: handle-reserve --handle <handle> [--organization <id>]` |

Output and audit carry only opaque IDs: claim, publisher and organization. The handle is the
operator's own input, and it is never logged, traced or written to an audit field. Spans are
`gozleme.operator.handle_reserve`, `gozleme.operator.handle_release` and
`gozleme.operator.handle_reclaim`, with `gozleme.operator.exit_code` only.

## Guarantees and limits

- A reclaim never changes a canonical ID, alias, capability name or display name, and cannot be undone. There is no unsuspend command.
- These commands need neither the configured origin nor the context key, only the migrator connection.
- Operators should still close open sign-up on shared instances (GOZ-0001). Reclaim is the remedy, not the prevention.
