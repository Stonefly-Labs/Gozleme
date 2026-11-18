# Research: Immutable Revisions and Signed Attestations (GOZ-0007)

**Date**: 2026-11-18 | **Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md)

Each item gives the decision, the rationale and the alternatives considered. **P** items are maintainer plan
decisions (recorded in `progress.yaml`). **A** items are agent choices, disclosed for maintainer veto; none
conflicts with the spec. Facts about the existing code were read from the branch at `dd04470`.

## R1 — Signature algorithm and library (maintainer P1)

- **Decision**: Ed25519 (RFC 8032, pure Ed25519, no pre-hash), through **NSec.Cryptography 26.4.0** (MIT), pinned
  centrally in `Directory.Packages.props`. Every other algorithm is refused (`unknown_algorithm`).
- **Facts checked 2026-11-18**:
  - The .NET 10 reference assembly (`Microsoft.NETCore.App.Ref` 10.0.7) has no standalone Ed25519 type. It has only
    `MLDsa` and `CompositeMLDsa` (including `MLDsa44WithEd25519`), so a package is needed.
  - NSec 26.4.0 was published on nuget.org on 2026-04-30 under MIT, with no listed vulnerabilities. It targets `net9.0`,
    which runs on `net10.0`, and depends on `libsodium [1.0.22, 1.0.23)` (ISC). That package ships native
    `linux-x64`/`linux-arm64`/`osx`/`win` binaries, which the Debian-based `mcr.microsoft.com/dotnet/aspnet:10.0`
    runtime image loads.
  - Ed25519 signatures are deterministic, so identical statement bytes always give an identical signature, which the
    tests use.
- **Verify at vendoring**: libsodium loads in the runtime container and in CI (a smoke test signs and verifies at
  start-up warm-up), and `THIRD-PARTY-NOTICES.md` gains NSec (MIT) and libsodium (ISC).
- **Verified at vendoring (2026-11-18, T001–T002)**:
  - NSec.Cryptography 26.4.0 restored with libsodium 1.0.22. `dotnet list package --vulnerable --include-transitive`
    reports no vulnerable packages ([evidence/dependency-check.txt](./evidence/dependency-check.txt)).
  - `Ed25519VendoringTests` (osx-arm64, local) passes 4/4:
    - the RFC 8032 section 7.1 TEST 1 signature matches byte-for-byte and verifies;
    - a raw 32-byte seed imports with `AllowPlaintextExport` and with `None`, and with `None` it refuses export;
    - a fresh key round-trips through its seed;
    - the SPKI PEM has the RFC 8410 prefix `302a300506032b6570032100`, and `openssl pkey -pubin` accepts it.
  - `docker run --rm gozleme-api:local signing-selftest` in the built `aspnet:10.0` runtime image prints
    `signing-selftest: ok (Ed25519, RFC 8032 test 1)` and exits 0 on `linux/arm64` and on `linux/amd64` (buildx).
  - CI (ubuntu-latest) runs the same suite in `dotnet test`. The result is recorded in `progress.yaml`.
  - The stop rule was not triggered.
- **Alternatives**: BouncyCastle.Cryptography 2.7.0 (managed, MIT; a large library of which only Ed25519 would be
  used); built-in ECDSA P-256 (no package, but its signatures are not deterministic, which conflicts with the spec
  assumption); ML-DSA (in the BCL, but needs OpenSSL 3.5+ and lacks broad independent verifiers today).

## R2 — Signed bytes: raw Ed25519 over the JCS statement (maintainer P2)

- **Decision**: the signature is Ed25519 over the exact canonical bytes of statement v1, serialized by RFC 8785
  (JSON Canonicalization Scheme). There is no envelope wrapper. The statement's own `type` and `statementVersion`
  name the payload. The stored statement bytes *are* the canonical bytes, so verification never re-serializes before
  checking the signature.
- **Independent check**: `openssl pkeyutl -verify -pubin -inkey key.pem -rawin -in statement.json -sigfile
  statement.sig`, or Node `crypto.verify(null, statement, publicKeyPem, signature)`. Both are OpenSSL-based and
  independent of libsodium.
- **Alternative**: a DSSE envelope (PAE framing, as in in-toto and Sigstore). It binds the payload type outside the
  JSON, but every independent check then needs a PAE step. It could later become statement version 2.

## R3 — Canonical JSON (statement and projection digest; FR-009, GOZ-0006-IF-08)

- **Decision**: a Gözleme `CanonicalJson` writer implements RFC 8785 for the value subset that the statement and
  projection v1 use: objects, arrays, strings, booleans, `null`, and integers with magnitude ≤ 2^53 − 1.
  - **Member order**: members are sorted by UTF-16 code units.
  - **Strings**: escaped as in ECMAScript `JSON.stringify` (`\b \t \n \f \r \" \\`, other C0 characters as
    `\u00xx` in lowercase hex, everything else literal UTF-8). There is no BOM.
  - **Rejected input**: any non-integer number, an integer out of range, a duplicate member or a lone surrogate throws.
    That is a programming error, because projections are built by Gözleme and their integers are bounded at 10^9
    by the model card schema.
- **Projection digest**: `sha256:` + lowercase hex of SHA-256 over `CanonicalJson(projection)`, where the projection
  is the GOZ-0006 `ManifestProjection` serialized with `ManifestJson.Options`. The stored projection is these canonical
  bytes, so the stored bytes, the download and the digest input are the same bytes.
- **Tests**: the RFC 8785 Appendix B number-free vectors; key sorting with non-BMP characters (UTF-16 order, which
  differs from code-point order); and a cross-check in e2e where Node re-canonicalizes the download with
  `JSON.stringify` over sorted keys (equivalent for this subset) and compares bytes.
- **Alternatives**: hashing the plain `ManifestJson` output, which depends on declaration order and on serializer
  options, so it is not independently reproducible; jsonb text, which is not deterministic across PostgreSQL versions;
  a JCS NuGet package. None was evaluated: it would add a second new dependency for a subset of about 150 lines,
  and the maintainer approved only NSec (P1).

## R4 — Statement v1 shape (FR-008, FR-012)

- **Decision**: a closed JSON object, documented in [contracts/attestation-statement-v1.md](./contracts/attestation-statement-v1.md):
  `statementVersion` (1), `type` (`gozleme.revision-attestation`), `registryOrigin`, `publisher` and `capability`
  (canonical IDs `gz://<origin>/pub/<id>` and `gz://<origin>/cap/<id>`), `version`, `sequence`, `native`
  {`format`, `formatVersion`, `encoding`, `digest`, `byteLength`}, `adapter` {`id`, `version`}, `projection`
  {`version`, `digest`}, `publishedAt`, and `key` {`id`, `algorithm`}. It has no delegation, transfer, chain,
  witness, transparency, alias, handle, display name, organization or email member.
- **Adapter ID and version (A1)**: the GOZ-0006 adapter identifier `mcp/1` is split at the slash into `adapter.id`
  `mcp` and `adapter.version` `1`. These are the same facts in the shape FR-002 and FR-008 name. A future `mcp/2`
  gives `{id: mcp, version: 2}`.
- **Time**: `publishedAt` is the transaction time (`now()`), read at the start of the publication transaction and
  written as RFC 3339 UTC with exactly six fractional digits (`2026-11-18T10:15:30.123456Z`), which is PostgreSQL's
  precision. A trigger pins `published_at = now()`, so the signed value and the stored value are equal.

## R5 — Key ID (A2)

- **Decision**: `key.id` = `sha256:` + lowercase hex of SHA-256 over the 32-byte raw Ed25519 public key. It is derived
  from the key itself, so anyone can recompute it from the downloaded public key, and it reveals nothing more. The row
  also has an opaque UUID, which routes use.
- **Alternatives**: an RFC 7638 JWK thumbprint (needs JWK canonicalization for a single field, and the result is
  similar); a random ID (cannot be checked against the key).

## R6 — Private-key custody and the key-encryption key (Q1, FR-007)

- **Decision**:
  - **Encryption**: the 32-byte Ed25519 private key seed is encrypted with **AES-256-GCM** (BCL `AesGcm`) under the
    key-encryption key (KEK). The stored blob is `0x01 ‖ nonce(12) ‖ ciphertext(32) ‖ tag(16)`.
  - **Binding**: the associated data binds the blob to its row: `"gozleme.signing-key.v1" ‖ publisher UUID bytes ‖
    UTF-8 key ID`. A blob copied to another publisher or key does not decrypt.
  - **Configuration**: the KEK is `Gozleme:Signing:KeyEncryptionKey`, base64 of exactly 32 random bytes. Compose takes
    it from `GOZLEME_SIGNING_KEK`, with a throwaway default in `.env.example` that is labelled as such. Helm takes it
    from a Secret key `signing-key-encryption-key` referenced by name, and chart values never hold the value. The KEK
    is never stored in the database, is not part of Data Protection, and never appears in logs, telemetry, the status
    document or errors.
  - **Absent or invalid KEK**: the API still starts (reads and verification need only public keys). Publication answers
    `503 unavailable`, and so does a KEK that fails to decrypt a key (wrong KEK). The span reason is the bounded value
    `signing_unavailable`.
  - **In memory**: the private key exists in memory only for the single signing operation (NSec `Key` with
    `KeyExportPolicies.None` after import, disposed immediately).
- **Stated limit, repeated in the contract**: an operator holding the KEK and database access can sign any statement.
  The attestation proves that the registry origin accepted the revision for the publisher, not that the publisher
  held a key.
- **Alternatives**: Data Protection (refused by Q1 and GOZ-0001-IF-20); the database alone (refused by Q1); a per-row
  KDF from a master key (no benefit over AEAD with bound associated data).

## R7 — Lazy key creation and races (clarify Q3, edge case "first-key race")

- **Decision**:
  - **Generations**: a key row has `generation` (1, 2, …), with `UNIQUE (publisher_id, generation)`. The active key is
    the highest generation, and every lower generation has a retirement row.
  - **First publication**: the publication inserts generation 1 with `ON CONFLICT (publisher_id, generation) DO NOTHING`
    and then reads it back. A concurrent first publication waits on the unique index entry and, once the other
    transaction commits, reads the committed key. Both revisions are signed with it, so exactly one key exists.
  - **Insert policy**: a member may insert only generation 1, and only for a publisher with a held claim. Successor
    generations are operator-only (R9), so SQL cannot be used to rotate a key.
  - **Audit**: key creation writes audit event 1502 after commit, and only when this transaction inserted the key.
- **Alternatives**: creating the key at publisher registration (refused by clarify Q3); a
  `SELECT … FOR UPDATE` on the publisher row (needs `UPDATE` privilege and blocks renames).

## R8 — Sequence, version uniqueness and concurrency (FR-003, US2-AS5)

- **Decision**:
  - **Version key**: `version_key` = the declared version with any `+build` suffix removed. SemVer 2.0.0 precedence
    treats two versions as equal exactly when their major, minor, patch and pre-release identifiers are equal.
    Pre-release numeric identifiers cannot carry leading zeros, so equality of the remaining string is equality of
    precedence. `UNIQUE (capability_id, version_key)` makes a duplicate fail under any concurrency.
  - **Lock order**: the publication takes `pg_advisory_xact_lock(hashtextextended('gozleme.revisions:' || capability_id, 0))`.
    It then checks the version (`409 version_exists`; contents are never compared) and assigns
    `sequence = coalesce(max(sequence), 0) + 1` in a statement that runs after the lock, so under READ COMMITTED it
    sees every committed revision.
  - **Database pins**: `UNIQUE (capability_id, sequence)`, and the insert trigger refuses any sequence other than
    `max + 1`, so committed sequences have no gaps. A rolled-back publication consumes nothing.
  - **Unique violation**: if the database reports a unique violation anyway (the defence-in-depth path), it maps to
    `409 version_exists`.
- **Ordering**: lists are ordered by `sequence` ascending, and nothing is labelled "latest" (clarify Q2).
- **Alternatives**: a PostgreSQL sequence (leaves gaps); a SemVer collation or a comparison function in SQL (more
  surface for the same uniqueness).

## R9 — Operator key succession (Q5, FR-007a)

- **Decision**: a new operator command, `signing-key-succession`, run like `migrate`, with no HTTP route:
  - `signing-key-succession --publisher <id> --reason lost|compromised`
  - `signing-key-succession --undecryptable --reason lost`: every publisher whose active key fails to decrypt under
    the configured KEK.
  - **Connection**: it connects as the owner (`ConnectionStrings:GozlemeMigrator`) with no account context, which is
    the operator identity that `handle_claims_guard` already recognises.
  - **Transaction**: per publisher, it takes `pg_advisory_xact_lock(hashtextextended('gozleme.signing:' || publisher_id, 0))`,
    creates generation n+1 encrypted under the current KEK, and inserts the retirement row for generation n
    (reason and successor). It commits, then writes audit 1503.
  - **Publication side**: a publication takes the *shared* form of the same lock before reading the active key. It
    therefore commits with the old key before the retirement, or reads the new key after it. A publisher never has two
    active keys, and no publication fails because of a succession.
  - **Output**: opaque IDs only (publisher, retired key, successor key) and exit codes like the handle commands
    (0 ok, 1 failed, 2 unknown publisher or no key yet, 64 usage). It never prints key material.
  - **Suspended publishers**: it works for them too, and publication stays refused.
- **Policy surface (security-review item)**: the operator writes to tenant-owned, forced-RLS tables. A new non-definer
  predicate `gozleme.is_operator()` (`current_user` and `session_user` are `gozleme_migrator`, and there is no account
  context, the same test as `handle_claims_guard`) is the only policy branch that admits successor keys, retirements
  and the operator's cross-tenant key reads. `gozleme_app` can never satisfy it, because `current_user` differs.
- **Alternatives**: an API route (refused by Q5 and FR-013); tables that are not forced, like `handle_claims` (FR-018
  requires forced RLS for keys).

## R10 — Immutability in the database (FR-004, FR-005, SC-002)

- **Decision**: for `manifest_revisions`, `revision_attestations`, `publisher_signing_keys` and `signing_key_retirements`:
  - **Triggers**: `BEFORE UPDATE OR DELETE` (row) and `BEFORE TRUNCATE` (statement) triggers run the existing
    `gozleme.immutable_row_guard()`. Triggers bind the owner as well (GOZ-0000-IF-04).
  - **Privileges**: `gozleme_app` has only `SELECT, INSERT`.
  - **No dangling foreign keys**: no foreign key uses `ON DELETE CASCADE` or `SET NULL` into these tables. They have no
    foreign key to `users` at all (R11).
- **Atomicity**: a `DEFERRABLE INITIALLY DEFERRED` constraint trigger on `manifest_revisions` insert checks at commit
  that a `revision_attestations` row exists for the revision. A revision without an attestation cannot commit, even
  through SQL. The API inserts both in the request's single `TenantTransaction`.
- **Tamper test**: only a superuser with `SET session_replication_role = replica` (which disables triggers) and RLS
  bypass can change a byte. The integration test and the demo do exactly that on a throwaway revision, then assert
  `failed` with the expected reason (SC-003).

## R11 — Publishing account without a foreign key (A3)

- **Decision**: `published_by` (revisions) and `created_by` (generation-1 keys) are opaque account UUIDs with no
  foreign key to `users`.
  - **Why**: a `SET NULL` cascade would be refused by the immutability guard and would block a future account erasure,
    and `RESTRICT` would also block erasure. CR-07 separates erasable data from historical proofs, and the UUID is
    provenance, not personal data.
  - **Checks instead**: the insert policy pins the value to `gozleme.current_account_id()`.
  - **Display**: the UI shows the opaque ID only (FR-002).
- **Security-review item**: whether a later account-erasure spec (GOZ-0036 retention) needs anything more for these
  IDs.

## R12 — Endpoint order and answers (FR-017, FR-006)

- **Decision** for `POST …/capabilities/{capabilityId}/revisions`:
  1. origin verified (`503 unavailable`)
  2. membership (identical empty `404`)
  3. `manifest.publish` (`403 forbidden`)
  4. publisher and capability lookup (the same empty `404`)
  5. suspension (`409 suspended`)
  6. publication budget (`429 rate_limited`)
  7. media type (`415`)
  8. body ≤ 1 MiB (`413`)
  9. **GOZ-0006 validation, unchanged**: a rejection answers exactly as validate (`400`/`413`/`422` with the result body)
  10. version lock and uniqueness (`409 version_exists`)
  11. signing key available (`503 unavailable`)
  12. insert revision, key if first, attestation
  13. commit, then `201` with the revision detail and a `Location` header. Audit 1501 (and 1502) is written after
      commit.

  The write policy refuses a suspended publisher independently (step 12 fails closed if step 5 is raced by a reclaim:
  `42501` → `409 suspended`).
- **Reads**: list, read and the downloads follow the same order through step 4, and verification runs for each detail
  and list row.
- **Answer bodies (A4)**: `version_exists` and `suspended` are `409` with `{error}` bodies, like the other GOZ-0006
  errors.

## R13 — Routes and actions (Q4, FR-016)

- **Decision**: these routes are added on the organizations group, which gives 9 new scoped endpoints (18 → 27):

  | Route | Action |
  |---|---|
  | `POST …/publishers/{p}/capabilities/{c}/revisions` | `manifest.publish` |
  | `GET …/capabilities/{c}/revisions` | `manifest.list` |
  | `GET …/revisions/{r}` | `manifest.read` |
  | `GET …/revisions/{r}/native` | `manifest.read` |
  | `GET …/revisions/{r}/projection` | `manifest.read` |
  | `GET …/revisions/{r}/statement` | `manifest.read` |
  | `GET …/revisions/{r}/signature` | `manifest.read` |
  | `GET …/publishers/{p}/signing-keys` | `manifest.read` |
  | `GET …/publishers/{p}/signing-keys/{k}/public-key` | `manifest.read` |

  - **Actions (19 → 22)**: `manifest.publish`, `manifest.list` and `manifest.read` are added, and `manifest.validate`
    moves to the `manifest` resource.
  - **Keys use `manifest.read` (A5)**: public keys exist to verify revisions, so the read that shows a revision also
    shows its key. No `signing-key` resource is added.
  - **No item route for keys**: there is deliberately no `signing-keys/{k}` route. `…/signing-keys/rotate`,
    `/replace`, `/transfer` and `/delegate` therefore match nothing and answer `404` for every method. A boundary test
    pins this, together with `POST /api/v1/publish`. `PUT`/`PATCH`/`DELETE` on a revision answer `405`, which the spec
    allows (US2-AS2).
- **No arbitrary-statement verifier endpoint (A6)**: US4's "statements submitted to the verifier" are exercised against
  the pure `RevisionVerifier` in unit tests (added member, unknown version, unknown algorithm, foreign key ID) and
  against superuser-tampered stored rows in integration and demo. A public "verify these bytes" route would be a
  signing-key oracle surface with no user need in this slice.

## R14 — Verification (FR-010)

- **Decision**: `RevisionVerifier` is pure, with no I/O. The first failure wins, in this order:
  1. the statement is valid UTF-8 JSON and its bytes equal its own canonical form (`malformed_statement`)
  2. `statementVersion` is 1 (`unknown_statement_version`)
  3. it has only the known members at every level (`unknown_field`)
  4. `key.algorithm` is `Ed25519` (`unknown_algorithm`)
  5. `key.id` equals the attestation's key row and that key belongs to the revision's publisher (`unknown_key`)
  6. the signature verifies (`signature_invalid`)
  7. the statement fields equal the revision row (`statement_mismatch`)
  8. SHA-256 of the stored native bytes equals both the row and the statement (`native_digest_mismatch`)
  9. the same for the projection (`projection_digest_mismatch`)

  Anything else is `verified`.
- **Key status**: the result always carries the key status: `active`, or `retired` with time and reason.
- **Not authority**: the result is never stored as authority (Key Entities). The reasons are a closed set, also used as
  a metric label.

## R15 — Rate limit (FR-019)

- **Decision**: `manifest_publication_attempts` (`GlobalIdentity`) has the same shape and SQL as
  `manifest_validation_attempts`. Its option is `Gozleme:Manifests:PublishPerMinute` (default 10, range 1–10,000), and
  it is acquired after suspension.
- **Rejections** are recorded on the publish span and counter as `rate_limited` only.

## R16 — Telemetry and audit (FR-021, FR-022)

- **Spans**:
  - `gozleme.manifest.publish`, with tags for family, outcome and organization/publisher/capability/revision IDs after
    membership;
  - `gozleme.manifest.verify`, with outcome and reason;
  - the operator span `gozleme.operator.signing-key-succession`, through `PublisherTelemetry.StartOperator`.
- **Counters**:
  - `gozleme.manifest.publications` {family, outcome}, where outcome is one of `published`, `rejected`,
    `version_exists`, `forbidden`, `not_found`, `suspended`, `rate_limited`, `too_large`, `unsupported_media_type`,
    `unavailable`;
  - `gozleme.manifest.verifications` {outcome, reason}.
- **Audit** (range 1501–1510):

  | Event | Name | Content |
  |---|---|---|
  | 1501 | `revision_published` | revision, capability, publisher, organization, account |
  | 1502 | `signing_key_created` | key, publisher, organization, account |
  | 1503 | `signing_key_succeeded` | retired key, successor key, publisher, reason |
  | 1504 | `signing_key_succession_refused` | category; operator only |

  These are written after commit, with opaque IDs only. A rejected publication writes no audit event (as in
  GOZ-0006), and a permission denial keeps 1301.
- **Never recorded**: key material, signatures, statements, digests, versions, documents, names or aliases. The e2e
  no-secret scan greps Jaeger, Loki and API logs for the KEK, the run's statement and signature bytes (base64 and hex)
  and the native digest.

## R17 — Product UI (FR-023)

- **Publish**: the GOZ-0006 validation page gains **Publish revision** after an accepted result (when the member holds
  `manifest.publish`). It posts the same body through a same-origin BFF route.
- **Revisions list**: `…/capabilities/{c}/revisions`, with sequence, version, publication time, publishing account,
  native digest (shortened, copyable) and a verification badge.
- **Revision detail**: tabs for Native, Projection and Attestation. The Attestation tab shows the statement fields, the
  key (with its active or retired status, and a prominent banner for `compromised`), the verification result and the
  downloads.
- **Fixed proof wording**: "Signed by registry `<origin>` for publisher `<canonical id>` with a registry-held key.
  This shows that this registry accepted the revision from an authorized publisher member. It is not a signature made
  by the publisher." A wording test pins that no page contains "delegat", "transfer", "chain of trust",
  "transparency" or "publisher-held".
- **Suspended publisher**: a banner on the list and the detail.

## R18 — Seed and demo data (FR-024, FR-025)

- **Seed (A7)**: the seed creates no revision. It stays idempotent and needs no KEK.
- **Demo isolation** (maintainer ruling 2026-11-18, replacing a run-unique capability): stories 1, 2 and 4 publish to
  the seeded capability `manifest-demo` under the seeded demo publisher, matching US1's "a seeded capability", with
  run-unique SemVer pre-release versions (`1.0.0-r<runid>`, `1.1.0-r<runid>`), as FR-024 allows. Its revisions
  accumulate across runs, because revisions are never deleted. For the rename, reclaim and succession stories it registers a run-unique publisher
  (`revdemo-<runid>`), because a reclaim suspends that publisher permanently.
- **Tamper data**: superuser tampering happens only on a dedicated run-unique revision, which is labelled tampered in
  the evidence.

## R19 — Boundary and pin updates (FR-026)

- **Migration**: one new migration, `20261121000000_ManifestRevisions`, which brings the count to 8.
- **Table classification**: `TableClassification.TenantOwned` gains the four tables, `GlobalIdentity` gains
  `manifest_publication_attempts`, and `DefinerHelpers` is unchanged (`is_operator` is not a definer).
- **Boundary regex**: `BootstrapBoundaryTests`' `(manifest|revision|projection)` assertion now expects exactly
  `manifest_publication_attempts, manifest_revisions, manifest_validation_attempts, revision_attestations`.
- **Validate stays stateless**: `ManifestNoStorageTests` keeps proving that validate writes no revision row.
- **Other pins**: `OrganizationPermissionContractTests` pins 22 actions and 27 scoped endpoints. The cross-tenant
  isolation matrix adds the four tables for both logins.

## R20 — GOZ-0005-IF-08 (permanent suspension) as it affects this slice

- **Decision**: this plan fixes only the revision semantics during suspension (Q3). Revisions are readable,
  verifiable, frozen and badged, new publication is refused, and key succession still works. Whether and how a
  suspension can be lifted stays with GOZ-0039. Nothing here reads or changes `handle_claims` beyond the existing
  held-claim predicate.
