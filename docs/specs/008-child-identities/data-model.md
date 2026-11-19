# Data Model: Child Capability Identities and Projections (GOZ-0008)

**Migration**: `20261122000000_ChildIdentities` (additive DDL in schema `gozleme`, plus one changed policy). It adds
no data: existing revisions get their children from the `migrate` install step (research R6).
**Research**: [research.md](./research.md).

All three new tables are **tenant-owned**:
- `organization_id` references `organizations`, and composite foreign keys `(…, organization_id)` pin each row to its
  capability's organization;
- row-level security is enabled **and forced**;
- `gozleme_app` holds `SELECT, INSERT` only;
- `immutable_row_guard` refuses `UPDATE`, `DELETE` and `TRUNCATE` for every login, the owner included (FR-003, FR-017).

**Read policies** (all three tables): `CASE WHEN gozleme.is_operator() THEN true ELSE organization_id IN (SELECT
gozleme.account_organizations()) END`.

**Member insert branch** (all three tables, FR-018), the same as `manifest_revisions_insert`:
`gozleme.account_has_role(organization_id, 'publisher')` and a `held` claim for the capability's publisher.

## Entities

### `capability_children` — child identity

| Column | Type | Rules |
|---|---|---|
| `id` | uuid PK | UUIDv7; the stored part of the child ID (R8); opaque route ID |
| `organization_id` | uuid | FK (`capability_id`, `organization_id`) → `capabilities` `ON DELETE RESTRICT` |
| `capability_id` | uuid | the owning capability; never changes |
| `kind` | text | `CHECK (kind IN ('tool','resource','resource-template','prompt','skill','operation','webhook','channel'))`; never changes |
| `native_name` | text | the match key with `kind` (R2); no length `CHECK` (maintainer decision 2026-11-19 "native-name-bound": validation guarantees no bound, so the table adds none); never in an ID, URL, span or log |
| `native_name_digest` | bytea | `sha256` of the UTF-8 `native_name`, set by the insert guard (not by the caller); the indexed match key, so a long name never exceeds a btree entry |
| `is_duplicate` | boolean | `false` except for install-step duplicates (FR-016a); never matched |
| `ordinal` | integer | ≥ 1, gap-free per capability (`UNIQUE (capability_id, ordinal)`, guard `max + 1`); order and paging only |
| `first_revision_id` | uuid | FK (`first_revision_id`, `organization_id`) → `manifest_revisions`; the revision that created it |
| `created_at` | timestamptz | `now()` (guard) |

**Indexes**:
- `ak_capability_children_id_organization_id UNIQUE (id, organization_id)`;
- the partial unique index `ix_capability_children_match UNIQUE (capability_id, kind, native_name_digest) WHERE NOT
  is_duplicate`, so there is one matchable child per key (R4). A guard-set column rather than an expression index, because
  `convert_to` is not immutable (maintainer decision 2026-11-19, "No bound; hashed index").

**Insert policy**: the member branch with `is_duplicate = false`, or `is_operator()` (install step, duplicates
allowed).

**Insert guard** (`BEFORE INSERT`, row): sets `created_at`; checks `ordinal = max + 1` for the capability and that
`first_revision_id` is a revision of the same capability.

**Deferred constraint trigger**: at commit, the child has a `revision_children` row for `first_revision_id`.

### `revision_children` — child revision record

| Column | Type | Rules |
|---|---|---|
| `revision_id` | uuid | FK (`revision_id`, `organization_id`) → `manifest_revisions` |
| `entry_index` | integer | ≥ 0; the entry's position in the stored projection `entries` |
| `organization_id` | uuid | |
| `child_id` | uuid | FK (`child_id`, `organization_id`) → `capability_children` |
| `kind` | text | equals the entry `kind` and the child's `kind` |
| `native_name` | text | equals the entry `nativeName` (and the child's `native_name` unless the child is a duplicate) |
| `title` | text NULL | equals the entry `title` |

**Keys**: PK (`revision_id`, `entry_index`); `UNIQUE (revision_id, child_id)` (a child appears at most once per
revision); index on `child_id` (child history).

**Insert policy**: the member branch, or `is_operator()`.

**Statement-level entry check** (`AFTER INSERT … REFERENCING NEW TABLE`, R4): for each affected revision, its stored
projection is parsed once, and every new row must equal entry `entry_index` (`kind`, `nativeName`, `title`), and its
child must be of the same capability and kind and, if not a duplicate, of the same native name. Otherwise it raises
`insufficient_privilege`, which publication classifies as a refusal (rollback, nothing stored).

### `projection_generations` — projection generation

| Column | Type | Rules |
|---|---|---|
| `id` | uuid PK | UUIDv7; opaque route ID; unrelated to any child ID (FR-010) |
| `organization_id` | uuid | |
| `revision_id` | uuid | FK (`revision_id`, `organization_id`) → `manifest_revisions` |
| `generation` | integer | ≥ 1; `UNIQUE (revision_id, generation)`; guard `max + 1` |
| `adapter_id`, `adapter_version` | text | for generation 1, equal to the revision's (guard) |
| `projection_version` | text | `'1'` in this slice |
| `projection` | bytea NULL | `NULL` for generation 1 (it is `manifest_revisions.projection`, FR-009); RFC 8785 bytes for N > 1 |
| `projection_digest` | text NULL | `NULL` for generation 1; for N > 1 `= 'sha256:' || encode(sha256(projection), 'hex')` (CHECK) |
| `created_at` | timestamptz | `now()` (guard) |

**CHECK**: `(generation = 1) = (projection IS NULL AND projection_digest IS NULL)`.

**Insert policy**: the member branch with `generation = 1`, or `is_operator()`.

**Insert guard** (`BEFORE INSERT`, row): `generation = max + 1`; generation 1 has the revision's adapter and projection
version; generation > 1 only for the operator, with the same `kind`/`nativeName` multiset as the revision's stored
projection (FR-011a, US3-AS5); sets `created_at`.

**Attested** is derived and never stored: `generation = 1`.

### Changed: `manifest_revisions` (GOZ-0007)

- **Policy** `manifest_revisions_select` gains `WHEN gozleme.is_operator() THEN true` (research R6; security-review item).
- **New deferred constraint trigger** `manifest_revisions_children` (`AFTER INSERT`, `DEFERRABLE INITIALLY DEFERRED`):
  at commit, the revision has generation 1 and exactly as many `revision_children` rows as projection entries
  (FR-007, SC-002). It fires only for revisions inserted after the migration; the install step covers older ones.
- Columns, statement, signature and attestation are unchanged (FR-021).

## Relationships

```text
capabilities 1─* capability_children 1─* revision_children *─1 manifest_revisions 1─* projection_generations
capability_children *─1 manifest_revisions (first_revision_id)
```

Generation IDs and child IDs never reference each other.

## Functions and triggers (all `plpgsql`, `SET search_path = pg_catalog, pg_temp`, `EXECUTE` revoked from `PUBLIC`)

No new callable function and no `SECURITY DEFINER`; `DefinerHelpers` is unchanged.

- `capability_children_insert_guard`, `projection_generations_insert_guard` (row, `BEFORE INSERT`);
- `revision_children_entry_check` (statement, `AFTER INSERT`, transition table);
- `capability_children_recorded`, `manifest_revisions_children` (deferred constraint triggers);
- immutability: `immutable_row_guard` row triggers (`UPDATE`/`DELETE`) and statement triggers (`TRUNCATE`) on all three
  tables.

## State

- **Child**: created by the first revision that declares its key (or as an install-step duplicate) → never changes.
  It is *present* in a revision with a `revision_children` row, and *absent* otherwise (derived).
- **Generation**: 1 at publication (or install) → N + 1 per operator re-projection; never changes.

## Application types (C#)

- `ChildAssigner` (pure): input is the capability's non-duplicate children by key, the next ordinal, the projection
  entries and the mode (`publish` | `install`); output is the new children, the revision records and the counts.
- `ChildEntries.Duplicates(entries)` (pure): the duplicate groups for `publish.duplicate_entry`.
- `ProjectionRegenerator` (pure core of the command): native bytes + envelope summary + adapter → canonical projection.
- Read models: `ChildSummary`, `ChildDetail` (with history page), `RevisionChild`, `ProjectionGenerationView`.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Gozleme:Manifests:ChildPageSize` | 50 | children, history and revision-children pages; maximum 100 |

No new secret, environment variable or Helm value.
