# Research: Child Capability Identities and Projections (GOZ-0008)

Decisions taken by the maintainer are Q1–Q3 (specify) and the two clarify answers, recorded in
[spec.md](./spec.md) and `progress.yaml`. Everything below either applies those decisions or is a design choice
inside them; agent choices are listed under **Agent choices** at the end so the maintainer can veto them. None of them
conflicts with the spec.

## R1 — Where child assignment runs

**Decision**: child matching runs in C# (`ChildAssigner`, a pure class) inside the existing publication transaction,
after the revision row and before `SaveChangesAsync`, under the per-capability advisory lock that
`RevisionPublisher` already holds (`gozleme.revisions:<capability>`). The database then **checks** the result (R4), so
an assignment that does not follow the rule cannot commit.

**Rationale**: the lock already serializes sequences, so matching in sequence order (FR-005, US2-AS4) comes free, and
concurrent publications cannot create two children for one key. One C# implementation serves both publication and the
install step (R6), so there is no second matcher to drift.

**Alternatives considered**:
- *A `plpgsql` trigger that assigns children from the projection*: one implementation in the database. It was rejected
  because the install step would then need the owner binding lifted inside the migration (GOZ-0003-IF-03) and a
  superuser per-row test, and the matching logic would sit in the least testable layer. The checks in R4 give the same
  guarantee without moving the logic.
- *Assignment in a separate transaction after publication*: rejected by FR-007 (stored together or not at all).

## R2 — Matching rule (maintainer Q2, clarify Q1)

**Decision**: for revision *r* of capability *c*, walk the stored projection's `entries` in position order:

1. The key is (`kind`, `nativeName`), compared **ordinally** (exact UTF-8 bytes; no case folding or normalization).
2. If an existing **non-duplicate** child of *c* has that key, the entry is that child (`matched`).
3. Otherwise a new child is created with that key (`created`).
4. A second entry with the same key inside one revision:
   - at publication: refused before anything is stored (R3);
   - in the install step only: it gets a new child with `is_duplicate = true` (FR-016a). Matching never selects a
     duplicate child, so it is absent from every later revision.
5. Title, description and position never affect matching.
6. `absent` = non-duplicate children of *c* that existed before *r* and that *r* does not declare, plus every
   duplicate child (bounded count only; used in telemetry and the view).

Determinism (FR-005, SC-003): the result depends only on the ordered sequence of stored projections, so republishing
the same sequence into a fresh stack gives the same assignment. Child UUIDs are random UUIDv7s, so the test compares
the **mapping** (revision sequence, entry index) → child ordinal, not the UUID bytes.

## R3 — Publish-time duplicate rule (FR-005a)

**Decision**: a new rule `publish.duplicate_entry` in its own **publish rule catalogue** (`PublishRules`, version `1`),
separate from the GOZ-0006 rule catalogue, which stays unchanged (FR-021; `ManifestRuleCatalogueTests` keeps 40 rules,
and `rule_catalogue_version` on revisions is unchanged).

- **Category**: `invalid_value`, one of the existing 11, so the category set does not change.
- **Location**: `{part: "projection", pointer: "/entries/<i>"}` for **every** entry in a duplicate group, the first
  included, ordered by index. `projection` is a new location part. It points into the projection that validate returns
  for the same body, so the publisher can find the entry. The pointer never contains the name.
- **Message**: fixed, "Two or more projection entries have the same kind and native name." No value is echoed.
- **Shape and status**: the GOZ-0006 rejected body `{problems, truncated}` with `422`, capped at 50 with `truncated`.
- **Order in publication**: after GOZ-0006 validation accepts and before the version check, so the order becomes
  … → validation → **duplicate entries** → version → key → `201`. Nothing is stored and the budget attempt still
  counts, as for any rejection.
- **Telemetry**: the existing outcome `rejected`; no new outcome value.

This is the documented validate/publish contract delta ([contracts/publish-delta.md](./contracts/publish-delta.md)):
validate can answer `200` for a body that publish answers `422`.

**Alternatives considered**: a new adapter version (rejected by maintainer Q2); adding the rule to the GOZ-0006
catalogue (would change validate, against FR-005a); a new category (a larger contract change for no gain).

## R4 — Database guarantees for children

**Decision**: the database enforces that a child assignment is consistent, whatever path inserted it:

- **One matchable child per key**: a partial unique index `(capability_id, kind, native_name_digest) WHERE NOT is_duplicate` (the guard-set SHA-256 of the name; maintainer decision 2026-11-19)
  on `capability_children`. Together with the entry check below, a non-duplicate entry has exactly one possible child, so
  a wrong reuse or a second child for one key cannot commit.
- **Duplicates only from the install step**: the insert policy admits `is_duplicate = true` only for the operator
  (`is_operator()`, R6).
- **Each child revision record equals its projection entry**: a statement-level `AFTER INSERT` trigger with a transition
  table on `revision_children` parses each affected revision's stored projection **once**
  (`convert_from(projection, 'UTF8')::jsonb -> 'entries'`) and refuses any row whose `entry_index`, `kind`,
  `native_name` or `title` differs from the entry, whose child belongs to another capability or kind, or whose
  non-duplicate child has a different native name.
- **Complete or nothing**: a deferred constraint trigger on `manifest_revisions` (new, `AFTER INSERT`, like
  `manifest_revisions_attested`) refuses at commit a revision whose generation 1 is missing or whose
  `revision_children` count differs from its entry count (SC-002). A deferred constraint trigger on
  `capability_children` refuses a child with no revision record.
- **Ordinals**: each child gets a gap-free per-capability `ordinal` (insert guard: `max + 1`, under the capability lock)
  used only for stable, name-free list order and paging (FR-013).

**Rationale**: GOZ-0007 already enforces its derivations (`version_key`, `sequence`) in insert guards; this extends the
pattern. The per-statement trigger parses each projection once, not once per row, which keeps 1,000 entries within
SC-006 (R12).

**Alternatives considered**: row-level guards that each parse the projection (up to 1,000 parses per publication);
application-only checks (bypassable by SQL, against FR-018's mirroring).

## R5 — Projection generations (maintainer Q3, FR-009–FR-012)

**Decision**: a table `projection_generations` with its own UUIDv7 `id`, `revision_id`, `generation` (gap-free from 1)
and `adapter_id`, `adapter_version`, `projection_version`.

- **Generation 1** stores **no bytes and no digest** (`CHECK`); readers take them from `manifest_revisions.projection`
  and `projection_digest`, so no copy can diverge (FR-009). Its adapter columns must equal the revision's (guard). It
  is the only attested generation.
- **Generation N > 1** stores `projection` bytes and `projection_digest`; the guard checks
  `projection_digest = 'sha256:' || encode(sha256(projection), 'hex')`, that the entry (`kind`, `nativeName`) multiset
  equals generation 1's, that only the operator inserts it, and that `generation = max + 1`.
- **Identifiers**: generation IDs and child IDs are different columns of different tables. No route, field or span
  attribute takes one for the other. A generation ID in a child route (or the reverse) finds nothing and answers the
  empty `404` (US3-AS3).
- **Child identity per generation** (FR-012): child revision records are per revision and are taken from generation 1.
  A child's identity in generation N is found by key and occurrence: the *k*-th entry with key K in generation N is the
  same child as the *k*-th entry with key K in generation 1. Because the multiset of keys is equal, every entry
  resolves and no child is created or dropped. Titles in later generations are visible only in that generation's
  bytes; the child view shows generation 1 titles.
- **Not attested**: every view and the API mark generation > 1 `attested: false` (FR-011, GOZ-0007-IF-01).
- **Comparison**: the "digest equals previous generation" report compares only when adapter ID, adapter version and
  projection version are equal; otherwise it reports `not_comparable` (GOZ-0007-IF-09).

## R6 — Install step for existing revisions (FR-016, FR-016a)

**Decision**: `migrate` gains a final, idempotent step, **child assignment**, after the registry origin is verified. As
the operator (owner login, no account context, which `is_operator()` recognises), it:

1. selects revisions without a generation 1, grouped by capability and ordered by `sequence`;
2. per capability, takes the same `gozleme.revisions:<capability>` advisory lock, then assigns each revision with
   `ChildAssigner` in **install mode** (duplicates allowed, FR-016a) and inserts generation 1;
3. commits per capability, and writes one audit event per capability with counts and opaque IDs.

Running it again finds nothing to do and changes nothing (US5-AS2). Revisions and attestations are only read, so every
revision verifies exactly as before (US5-AS3).

**Supporting policy change**: `manifest_revisions_select` gains the branch `WHEN gozleme.is_operator() THEN true` (the
`publisher_signing_keys_select` pattern), so the operator can read projections and native documents. This is the only
change to an existing policy, and a **security-review item**: it widens what the owner login reads with no account
context. The owner already owns the table, so it adds no capability that the owner could not obtain with DDL, and
`gozleme_app` can never satisfy `is_operator()`.

**Rollout gap**: between `migrate` and the new API starting (only in a mixed rolling upgrade; Compose and Helm run
`migrate` first), a publication by the new API to a capability with a revision that has no generation 1 answers
`503 {"error":"unavailable"}` (checked under the capability lock), and a publication by a pre-GOZ-0008 API fails at
commit (the R4 constraint trigger). Both fail closed; neither stores anything.

**Alternatives considered**:
- *A data migration in SQL*: a second matcher in `plpgsql`, plus lifting the owner binding (GOZ-0003-IF-03) and a
  superuser per-row test.
- *A separate operator command the operator must remember*: FR-016 needs every revision to have children before later
  specs rely on it; making it part of `migrate` means it cannot be forgotten.

## R7 — Re-projection operator command (FR-011a, US3)

**Decision**: `projection-regenerate --revision <uuid>`, run like `migrate` and `signing-key-succession`
(`docker compose run --rm migrate projection-regenerate --revision <id>`), owner login, no account context, no API
route.

Steps, in one transaction:
1. Take the per-revision lock `gozleme.projections:<revision>`; read the revision as the operator.
2. Check that the stored native bytes still hash to `native_digest`; if not, refuse (exit 3, `native_digest_mismatch`).
3. Find the adapter whose `Id` equals the revision's stored adapter (`mcp/1`, …); if none is registered, refuse (exit 3,
   `adapter_unavailable`).
4. Parse the stored native bytes with the stored encoding and call the adapter's `Project` with an envelope rebuilt
   from `envelope_summary`. Validation rules are **not** re-run: the revision was accepted at publication, and
   re-running envelope identity checks would fail after a capability rename. A parse failure refuses (exit 3,
   `projection_failed`).
5. Canonicalize (RFC 8785, `CanonicalJson`) and compare the entry key multiset with generation 1. If it differs, refuse
   (exit 3, `entries_changed`) and record nothing (US3-AS5).
6. Insert generation N + 1 (`max + 1`), even when its bytes equal the latest generation (clarify Q2).

Output (stdout): `<revision-uuid> generation <n> <generation-uuid> digest equals generation <n-1>: yes|no|not_comparable`.
No digest, name or document is printed.

Exit codes: `0` recorded, `1` failed (connection, configuration; stderr `failed`), `2` unknown revision (stderr
`unknown_target`), `3` refused (reason on stderr as a fixed word: `native_digest_mismatch`, `adapter_unavailable`,
`projection_failed`, `entries_changed`, or `not_installed` for a known revision without generation 1, maintainer
ruling F14), `64` usage. Span `gozleme.operator.projection_regenerate`; audit 1601 on success and 1602 on refusal,
after commit, opaque IDs only. A suspended publisher's revision can be re-projected: a generation never changes which
children the revision declares, so the revision stays frozen.

In this slice the adapters never change (GOZ-0006 FR-005), so generation 2 is normally byte-identical to generation 1;
the command exists so a future adapter version can re-project without breaking child IDs.

**Convergence rulings** (maintainer, 2026-11-19; `progress.yaml` decisions nul-character, nul-pointer, envelope-nul,
child-refusal-status, regenerate-not-installed). They extend R3, R6 and R7:
- R3: publish rule catalogue v1 also holds `publish.nul_character` (projection and envelope strings holding U+0000, `422`,
  exact string pointer; duplicates first). PostgreSQL `text` and `jsonb` cannot hold U+0000, which validate accepts.
- R6: the install step skips a revision whose projection holds U+0000, audits 1604 (`gozleme.audit.children_install_skipped`, revision
  ID only) and continues.
- Publication: a child-table refusal (insert or completeness, `23505`, `42501`, `23000`) and `22P05`/`22021` answer
  `503 unavailable`, nothing stored (contracts/publish-delta.md).
- R7: `not_installed` (exit `3`, audit 1602) as above.

## R8 — Child identifier form (maintainer Q1)

**Decision**: `gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>`, rendered by a new
`CanonicalId.Child(origin, capabilityId, kind, childId)` beside the GOZ-0005 forms. Only the child UUID (UUIDv7) is
stored. `kind` is one of `tool`, `resource`, `resource-template`, `prompt`, `skill`, `operation`, `webhook`,
`channel`. No alias, no alias reservation. The namespace contract
`docs/specs/005-publisher-identities/contracts/namespace.md` gets an additive "Child IDs (GOZ-0008)" section with the
decision provenance (GOZ-0005-IF-12); [contracts/child-identity.md](./contracts/child-identity.md) holds the details.
The child ID never embeds a native name, title, alias, handle, organization ID, position, revision, sequence or
generation (FR-002).

## R9 — API surface and permissions

**Decision**: five read-only endpoints under
`/api/v1/organizations/{organizationId}/publishers/{publisherId}/capabilities/{capabilityId}`, all on existing actions:

| Method | Route | Action |
|---|---|---|
| GET | `…/children?after=&limit=` | `manifest.list` |
| GET | `…/children/{childId}?after=&limit=` | `manifest.read` |
| GET | `…/revisions/{revisionId}/children?after=&limit=` | `manifest.read` |
| GET | `…/revisions/{revisionId}/projections` | `manifest.read` |
| GET | `…/revisions/{revisionId}/projections/{generationId}` | `manifest.read` |

- **No new action or resource** (spec Assumption): reads stay member-level, like `manifest.list`/`manifest.read`, so no
  GOZ-0004-IF-01 maintainer question arises. `OrganizationPermissionContractTests` moves to **32** scoped endpoints and
  keeps 22 actions over 6 resources.
- **Order** (FR-015): `503 unavailable` (origin not verified) → empty `404` (malformed, unknown or non-member
  organization) → `403 forbidden` → empty `404` (unknown, foreign or misplaced publisher, capability, revision, child
  or generation; malformed IDs). Reused from `RevisionEndpoints.ResolveAsync`.
- **No writes**: `POST`, `PUT`, `PATCH` and `DELETE` on the child routes answer `405` (as GOZ-0007 revision routes do);
  there is no create, rename or delete route (US4-AS4).
- **Paging**: capability children by `ordinal` (default 50, at most 100); child history by revision `sequence` (same
  bounds); revision children by `entry_index` (same bounds); generations are few and unpaged.
- **Generation download**: `…/projections/{generationId}` returns the exact bytes (`application/json`,
  `Content-Disposition: attachment`, `nosniff`); for generation 1 these are the stored revision projection bytes.
- **Publication response**: unchanged (FR-021). Counts are on the span, and the revision page reads the children.

## R10 — Views

**Decision** (web, Next.js, same patterns as GOZ-0007 pages):
- **Children** page `…/capabilities/[capabilityId]/children`: table of kind, child ID (`CanonicalId`), names and titles
  by revision (latest few, with a link), and the revisions it appears in; empty state for `skill`/`model`
  capabilities ("This capability declares no children."). Linked from the capability list and the revision list.
- **Child** page `…/children/[childId]`: child ID, kind, parent capability (canonical ID and display name), and a
  history table of every revision (sequence, version) with **Present** (native name, title) or **Absent**; a
  "Duplicate entry" note for duplicate children.
- **Revision page** (GOZ-0007): a **Children** section listing the revision's children by entry, each linked by child
  ID, and a **Projection generations** section (generation, generation ID, adapter, digest, **Attested at
  publication** for 1, **Not attested** for later ones, and a download).
- **Fixed notice** on every child view: "Children are declared by the publisher in the manifest. They are not verified
  behaviour." No approval, trust, safety, lifecycle or verification badge on a child (FR-014).
- Two moves from the capability to a child's history: capability list → **Children** → child (SC-001).
- Browser flows `gozleme.ui.children_list` and `gozleme.ui.child_read`.

## R11 — Telemetry and audit (FR-019, FR-020)

- **Spans**:
  - `gozleme.children.assign` (child of `gozleme.manifest.publish`, or of the install span), with
    `gozleme.manifest.family` and the counts `gozleme.children.declared`, `.created`, `.matched`, `.absent`
    (`.duplicates` in install mode).
  - `gozleme.projection.generation.record` with `gozleme.projection.generation` (integer) and
    `gozleme.projection.attested` (bool).
  - `gozleme.children.install` in `migrate`, with counts of revisions and capabilities.
  - `gozleme.operator.projection_regenerate` with `gozleme.operator.exit_code`.
  - Read endpoints reuse the GOZ-0007 attributes, plus `gozleme.child.id` and `gozleme.projection.generation_id`,
    which are set only after membership is confirmed.
- **Counters**: `gozleme.children.assignments` (`gozleme.manifest.family`, `gozleme.child.kind`,
  `gozleme.children.outcome` ∈ {`created`, `matched`, `duplicate`}) and `gozleme.projection.generations`
  (`gozleme.manifest.family`, `gozleme.projection.outcome` ∈ {`recorded`, `identical`, `refused`}).
- **Never recorded**: names, titles, native names, digests, documents or versions.
- **Audit** (range 1601–1610, `SecurityAudit`): 1601 `projection_generation_recorded`, 1602
  `projection_generation_refused` (fixed reason word), 1603 `children_installed` (capability, revision count). Child
  assignment during publication is covered by the existing 1501 event; no per-child audit event. The next free range
  is **1701**.

## R12 — Performance (SC-006)

**Decision**: inserts go through one `SaveChangesAsync` (Npgsql batches them); the guards are statement-level (R4), so
each projection is parsed once per statement and once in the deferred check; matching uses one query for the
capability's non-duplicate children by key. `RevisionChildrenPerformanceTests` publishes a 1,000-entry MCP manifest
and asserts the time stays within the GOZ-0007 budget (2 s) plus 1 s.

## R13 — Tables, classes and pins

- Three new tables, all `TenantOwned`, with forced RLS, `SELECT, INSERT` only for `gozleme_app`, `immutable_row_guard`
  for `UPDATE`/`DELETE`/`TRUNCATE` for every login, composite foreign keys `(…, organization_id)`, and entries in the
  isolation matrix for both logins (FR-017).
- **No new function** except trigger functions (plain `plpgsql`, fixed `search_path`, `PUBLIC` execute revoked);
  `DefinerHelpers` unchanged.
- **Migration**: `20261122000000_ChildIdentities`.
- **Pins updated deliberately**:
  - `LATEST_MIGRATION` (`/_ChildIdentities$/`);
  - `MigrationAndRoleTests` (9 migrations);
  - `StatusEndpointTests`;
  - the `BootstrapBoundaryTests` table list;
  - the `(manifest|revision|projection)` set, which now also allows `projection_generations` and `revision_children`.
    `capability_children` does not match that pattern; it goes in the table list only.
- `GET /api/v1/capabilities`, `/catalog`, `/search` and `POST /api/v1/publish` stay `404`.

## R14 — Demo fixtures

**Decision**: the seed is unchanged. The `goz-0008` suite publishes to the seeded `manifest-demo` capability with
run-unique versions (GOZ-0007-IF-02, spec Assumption) and **run-unique native names** (for example
`search_r<runid>`), so each run's tools are new children and the counts per revision are exact, although the
capability's Children view grows across runs. Assertions on the capability list are "contains", and on the revision
they are exact. Integration tests use fresh capabilities for exact capability-level counts. The re-projection command
is driven through `e2e/tests/operator.ts`.

## Agent choices (disclosed for veto; none conflicts with the spec)

| ID | Choice | Where |
|---|---|---|
| A1 | Matching in C# under the existing lock, verified by database guards (not a `plpgsql` matcher) | R1, R4 |
| A2 | Install step as the last stage of `migrate`, as the operator; operator read branch on `manifest_revisions_select` (security-review item) | R6 |
| A3 | Duplicate rule in a separate publish rule catalogue, category `invalid_value`, location part `projection` | R3 |
| A4 | Command name `projection-regenerate`, exit codes 0/1/2/3/64, adapter `Project` without re-running validation rules | R7 |
| A5 | Five read endpoints on `manifest.list`/`manifest.read`, no new action; publication response unchanged | R9 |
| A6 | Per-capability `ordinal` for the stable, name-free list order | R4, R9 |
| A7 | A new-API publication to a capability with un-installed revisions answers `503 unavailable` (rollout gap only) | R6 |
| A8 | Later generations map to children by key and occurrence rank; child views show generation 1 titles | R5 |
