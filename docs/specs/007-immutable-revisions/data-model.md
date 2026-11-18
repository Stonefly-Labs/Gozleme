# Data Model: Immutable Revisions and Signed Attestations (GOZ-0007)

**Migration**: `20261121000000_ManifestRevisions` (additive DDL only, schema `gozleme`). **Research**: [research.md](./research.md).

All four new content tables are **tenant-owned**:
- `organization_id` references `organizations`, and composite keys pin each row to its publisher's organization;
- row-level security is enabled **and forced**;
- `gozleme_app` holds `SELECT, INSERT` only;
- `immutable_row_guard` refuses `UPDATE`, `DELETE` and `TRUNCATE` for every login, including the owner (R10).

Reads are member-level (`organization_id IN (SELECT gozleme.account_organizations())`).

## Entities

### `publisher_signing_keys` — publisher signing key

| Column | Type | Rules |
|---|---|---|
| `id` | uuid PK | opaque route ID |
| `organization_id` | uuid | FK (`publisher_id`, `organization_id`) → `publishers` |
| `publisher_id` | uuid | |
| `generation` | integer | ≥ 1; `UNIQUE (publisher_id, generation)`; the active key is the highest generation |
| `key_id` | text | `UNIQUE`; `sha256:` + 64 lowercase hex of the raw public key (R5) |
| `algorithm` | text | `CHECK (algorithm = 'Ed25519')` |
| `public_key` | bytea | `CHECK (octet_length = 32)` |
| `encrypted_private_key` | bytea | `0x01 ‖ nonce ‖ ct ‖ tag`, `CHECK (octet_length = 61)`; never selected by any API query (R6) |
| `created_at` | timestamptz | set to `now()` by trigger |
| `created_by` | uuid NULL | the account for generation 1 (lazy creation); `NULL` for operator successors; no FK (R11) |

**Insert policy**:
- **Member**: generation 1 only, the account holds `publisher`, a `held` claim names the publisher, and
  `created_by = current_account_id()`.
- **Operator**: `gozleme.is_operator()` with generation > 1 (R9).

**Select**: member-level, or `is_operator()`.

### `signing_key_retirements` — key retirement (append-only)

| Column | Type | Rules |
|---|---|---|
| `key_id` | uuid PK | FK → `publisher_signing_keys(id)`; one retirement per key |
| `organization_id` | uuid | same organization as both keys |
| `successor_key_id` | uuid | `UNIQUE`; FK → `publisher_signing_keys(id)`; same publisher, generation = retired + 1 (checked by trigger) |
| `reason` | text | `CHECK (reason IN ('lost', 'compromised'))` |
| `retired_at` | timestamptz | set to `now()` by trigger |

**Insert**: `is_operator()` only. **Select**: member-level, or `is_operator()`.

### `manifest_revisions` — manifest revision

| Column | Type | Rules |
|---|---|---|
| `id` | uuid PK | opaque route ID |
| `organization_id`, `publisher_id`, `capability_id` | uuid | FK (`capability_id`, `organization_id`) → `capabilities`; the trigger checks that `publisher_id` is the capability's publisher |
| `sequence` | integer | ≥ 1; `UNIQUE (capability_id, sequence)`; the trigger refuses any value except `max + 1` (R8) |
| `version` | text | the declared SemVer 2.0.0 string as submitted |
| `version_key` | text | `version` without `+build`; `UNIQUE (capability_id, version_key)`; the trigger checks the derivation |
| `family` | text | one of the six families |
| `envelope_summary` | jsonb | the GOZ-0006 `EnvelopeSummary` (not signed) |
| `native_format`, `native_format_version`, `native_encoding` | text | from the envelope |
| `native_document` | bytea | UTF-8 bytes exactly as submitted; `octet_length ≤ 524288` |
| `native_digest` | text | `sha256:<hex64>`; not unique (same bytes may appear under a new version) |
| `native_byte_length` | integer | `= octet_length(native_document)` (CHECK) |
| `projection` | bytea | canonical JCS bytes of projection v1 (R3) |
| `projection_version` | text | `'1'` |
| `projection_digest` | text | `sha256:<hex64>` of `projection` |
| `adapter_id`, `adapter_version` | text | for example `mcp`, `1` (R4) |
| `rule_catalogue_version`, `schema_bundle_version` | text | the GOZ-0006 rule catalogue v1 and schema bundle v1 constants (pinned by tests) |
| `published_by` | uuid | `= current_account_id()` (policy); no FK (R11) |
| `published_at` | timestamptz | `= now()` (trigger), equal to the statement's `publishedAt` |

**Insert policy**: the account holds `publisher` in `organization_id`, a `held` claim names `publisher_id`, and
`published_by = current_account_id()`.

**Constraint trigger**: `DEFERRABLE INITIALLY DEFERRED`; at commit, an attestation row must exist for the revision.

### `revision_attestations` — revision attestation

| Column | Type | Rules |
|---|---|---|
| `revision_id` | uuid PK | FK (`revision_id`, `organization_id`) → `manifest_revisions`; one attestation per revision in this slice |
| `organization_id` | uuid | |
| `signing_key_id` | uuid | FK → `publisher_signing_keys(id)`; the trigger checks that it is the revision publisher's key |
| `statement_version` | smallint | `CHECK (= 1)` |
| `statement` | bytea | the canonical statement v1 bytes that were signed |
| `signature` | bytea | `CHECK (octet_length = 64)` |
| `created_at` | timestamptz | `now()` |

**Insert policy**: as for revisions (publisher role and held claim).

### `manifest_publication_attempts` — publication budget (`GlobalIdentity`)

It has the same columns as `manifest_validation_attempts` (`account_id` FK `users` `ON DELETE CASCADE`,
`window_start`, `attempts`). It holds no manifest data.

### Not stored

- **Verification result**: computed on every read (R14) and never persisted.
- **Key-encryption key**: configuration only (R6).

## Relationships

```text
organizations 1─* publishers 1─* capabilities 1─* manifest_revisions 1─1 revision_attestations
publishers 1─* publisher_signing_keys 1─* revision_attestations
publisher_signing_keys 1─0..1 signing_key_retirements ─1 publisher_signing_keys (successor)
```

## Functions and triggers (all `SET search_path = pg_catalog, pg_temp`, `EXECUTE` revoked from `PUBLIC`)

- **`gozleme.is_operator()`**: `STABLE`, *not* a definer. True when `current_user = 'gozleme_migrator' AND
  session_user = 'gozleme_migrator' AND gozleme.current_account_id() IS NULL`. `TableClassification.DefinerHelpers`
  is unchanged.
- **`manifest_revisions_insert_guard`**: sets `published_at := now()`. It checks the publisher–capability pairing, the
  `version_key` derivation and `sequence = max + 1`.
- **`revision_attestations_insert_guard`**: checks that the key belongs to the revision's publisher, and sets
  `created_at := now()`.
- **`publisher_signing_keys_insert_guard`**: sets `created_at := now()`. It checks that generation n > 1 is inserted
  only by the operator and only when generation n − 1 exists.
- **`signing_key_retirements_insert_guard`**: requires the same publisher and successor generation = retired + 1, and
  sets `retired_at := now()`.
- **`manifest_revisions_attested`**: the deferred constraint trigger (FR-005).
- **Immutability**: `immutable_row_guard` (`UPDATE`/`DELETE` row triggers and `TRUNCATE` statement triggers) on all
  four tables.

## State

- **Revision**: created signed → (never changes). It shows "suspended publisher" while its publisher has no held claim.
- **Key**: active → retired (`lost` | `compromised`), only by operator succession. A retired key still verifies its
  revisions and never signs again.
- **Publisher**: no key yet → key generation 1 (first publication) → generation n (succession).

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Gozleme:Signing:KeyEncryptionKey` | — (Compose: throwaway `GOZLEME_SIGNING_KEK`) | base64 of 32 bytes; Helm Secret key `signing-key-encryption-key`; API, `signing-key-succession` |
| `Gozleme:Manifests:PublishPerMinute` | 10 | 1–10,000 |
| `Gozleme:Manifests:RevisionPageSize` | 50 | list page size, maximum 100 |
