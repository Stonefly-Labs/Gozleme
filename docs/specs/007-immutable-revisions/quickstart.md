# Quickstart: Immutable Revisions and Signed Attestations

## Prerequisites

- Docker with Compose, .NET 10 SDK and Node 24, the same as GOZ-0006.
- **New NuGet package**: `NSec.Cryptography` 26.4.0, which brings native `libsodium` 1.0.22 (maintainer P1, research
  R1), pinned in `Directory.Packages.props`.
- **New configuration**:
  - **Compose**: `GOZLEME_SIGNING_KEK`, with a throwaway default in `.env.example`.
  - **Helm**: the `signing-key-encryption-key` key in the Secret named by `signing.existingSecret`. Create it with
    `openssl rand -base64 32`. See [contracts/signing-keys.md](./contracts/signing-keys.md).
- **Existing local volume**: no `make reset` is needed. `make up` runs the new migration
  `20261121000000_ManifestRevisions`. The seed is unchanged and creates no revision (research R18).
- `openssl` (3.x) on the host for the hand verification below. It is optional, because the demo verifies with Node.

## Run the automated checks

```bash
make test       # .NET unit + Testcontainers integration (PostgreSQL 17) and Vitest
make lint
make build
make validate
make demo       # cumulative Playwright run; goz-0007 is now the highest project
```

Key suites:

| Suite | Checks |
|---|---|
| `CanonicalJsonTests` | RFC 8785 vectors (number-free), UTF-16 key order with non-BMP names, escaping, refusal of floats, out-of-range integers, duplicates and lone surrogates |
| `AttestationStatementTests` | exact member set, field values from a revision, byte-identical canonical output, signature determinism |
| `RevisionVerifierTests` | each reason in [contracts/attestation-statement-v1.md](./contracts/attestation-statement-v1.md): added member (including `delegation`), unknown version, unknown algorithm, foreign key ID, altered statement, signature, native or projection byte |
| `SigningKeyVaultTests` | AES-GCM round trip, associated-data binding (a blob moved to another publisher or key fails), wrong or absent KEK → unavailable, no key material in `ToString` or exceptions |
| `RevisionPublishTests` | two revisions in order, `201` body and `Location`, a GOZ-0006 rejection answers byte-identically to validate and stores nothing, same version (also identical bytes and `+build` variants) `409`, a lower version accepted, concurrent same-version → one row, concurrent first publications → one key |
| `RevisionImmutabilityTests` | `UPDATE`, `DELETE` and `TRUNCATE` on all four tables refused as `gozleme_app` and as `gozleme_migrator`; `PUT`, `PATCH` and `DELETE` on revision routes `405`; superuser tamper → expected failure reason (SC-002, SC-003) |
| `RevisionAtomicityTests` | a revision without attestation cannot commit (deferred trigger); a failure after the key insert leaves no key |
| `RevisionAuthorizationTests` | role × {publish, list, read, downloads, keys} against the contract table; non-member, foreign and malformed IDs → identical empty `404`; suspended `409` in the API and in the write policy (direct SQL); `429` after authorization |
| `RevisionIsolationTests` | cross-tenant matrix for the four tables, both logins (select none, insert none) |
| `RevisionProvenanceTests` | rename and handle reclaim leave canonical IDs and verification unchanged; the suspended badge appears |
| `SigningKeySuccessionTests` | the command: `lost` and `compromised`, `--undecryptable` after a KEK change, a retired key still verifies, new revisions use the successor, the race with a publication, exit codes, output and audit hold opaque IDs only |
| `RevisionBoundaryTests` | `…/signing-keys/{rotate,replace,transfer,delegate}` and `POST /api/v1/publish` → `404` for every method; responses free of delegation, transfer and publisher-held wording |
| `RevisionTelemetryTests` | spans and counters with bounded labels; no key material, statement, signature, digest, version or name in spans or logs |
| `RevisionPerformanceTests` | publishing a 512 KiB native document in under 2 s, verifying in under 1 s (SC-007) |

Pins that change deliberately:

| Pin | Change |
|---|---|
| `OrganizationPermissionContractTests` | 22 actions, 27 scoped endpoints |
| `EndpointAuthorizationTests` | the new endpoints |
| migration pins | 8 migrations |
| `BootstrapBoundaryTests` | `(manifest\|revision\|projection)` now expects exactly the four named tables (research R19) |
| `ManifestNoStorageTests` | still green, because validate writes no revision |
| `TableClassification` and `RlsCatalog` | the four tenant-owned tables plus one `GlobalIdentity` table; `DefinerHelpers` unchanged |

## Demo walkthrough (what `make demo` drives)

After the seed, carol holds `{member, publisher}` and alice holds `{admin, member}` in **Seeded Demo Org**. Each run
publishes to the seeded capability `manifest-demo` under *Seeded Demo Publisher* with run-unique SemVer pre-release
versions (`1.0.0-r<runid>`, `1.1.0-r<runid>`), because revisions are never deleted (maintainer, A7 ruling 2026-11-18).

1. **Publish two revisions** (User Story 1, SC-001).
   - carol opens the capability → **Validate manifest**, loads the OpenAPI sample (version `1.0.0-r<runid>`), validates it and
     presses **Publish revision**. She is taken to the new revision.
   - She goes back, loads it with version `1.1.0-r<runid>` and publishes it.
   - **Revisions** shows sequence 1 and 2, the versions, times, the publishing account, the digests and two
     **Verified** badges.
   - Screenshots are taken of the list, the detail (Native, Projection and Attestation) and the key view, which shows
     generation 1 as active.
2. **Mutation is refused** (User Story 2, SC-002, SC-003).
   - Through the API: republishing `1.0.0-r<runid>` with different bytes, with identical bytes and as `1.0.0-r<runid>+build.7` gives
     `409 version_exists`. `PUT`, `PATCH` and `DELETE` on a revision give `405`.
   - Through `psql` as `gozleme_app` (with carol's context) and as `gozleme_migrator`: `UPDATE`, `DELETE` and
     `TRUNCATE` on each table are refused, and both revisions still verify.
   - **Tamper**: as the superuser, with triggers disabled, one byte of a third, run-unique "tamper" revision is altered
     (native, then the projection, statement and signature, each on its own revision). The detail shows **Failed** with
     the expected reason, and a screenshot is taken.
3. **Independent verification** (User Story 3, SC-004). For one revision of every family, the suite downloads native,
   projection, statement, signature and public key, and checks:
   - Node `crypto.verify(null, …)` succeeds;
   - SHA-256 of the native and projection downloads equals the statement digests;
   - Node re-canonicalization of the projection and statement equals the downloaded bytes.

   The detail page states "Signed by registry … for publisher … with a registry-held key".
4. **Provenance** (User Story 3, AS3–AS4).
   - alice registers the run-unique publisher `revdemo-<runid>` and makes carol a publisher there. carol publishes one
     revision and renames the capability, and the revision keeps the same canonical IDs and verifies.
   - The suite runs `handle-reclaim` for `revdemo-<runid>`. The revision is still readable and verified, with a
     **Suspended publisher** badge, and publishing answers `409 suspended`. Screenshots are taken.
5. **Key succession** (Q5, SC-008). On a second run-unique publisher (active), the suite runs
   `docker compose run --rm migrate signing-key-succession --publisher <id> --reason lost`. The key view shows
   generation 1 as retired (`lost`, with the time) and generation 2 as active. The earlier revision still verifies and
   shows that its key is retired. A new revision is signed with generation 2. A `compromised` succession shows the
   prominent banner. Screenshots are taken.
6. **No delegation** (User Story 4). The boundary routes give `404`. A DOM and response scan finds no "delegat",
   "transfer", "chain of trust", "transparency" or "publisher-held" text. A manifest that carries a `signature` claim
   gives the GOZ-0006 `reserved_claim` `422`.
7. **Roles** (User Story 5, SC-006).
   - alice is not offered **Publish revision**, and a direct `POST` gives `403`.
   - Every built-in role is checked against the contract table.
   - dave (Seeded Partner Org) gets the identical empty `404` for publish, list, read, downloads and keys.
   - 11 publications in a minute give `429`.
8. **Telemetry**.
   - Jaeger shows `gozleme.manifest.publish` and `gozleme.manifest.verify` spans, and the operator span
     `gozleme.operator.signing_key_succession`. The trace screenshots go into the PR.
   - The metrics show `gozleme.manifest.publications` and `gozleme.manifest.verifications`.
   - A no-secret scan of Jaeger, Loki and API logs finds none of the following: the KEK, the run's statement or
     signature (base64 and hex), the native digest, the version string, or the capability name.
9. **Regression**: the `goz-0000` … `goz-0006` suites, with screenshots under
   `evidence/screenshots/regression-goz-NNNN/`.

Evidence is written to `docs/specs/007-immutable-revisions/evidence/`: `demo-run.json`, `revision-evidence.json`
(including the tamper and succession run IDs) and `screenshots/`.

## Verify by hand

```bash
BASE=http://localhost:3000/api/organizations/$ORG/publishers/$PUB
curl -sb "$COOKIE" -o statement.json  "$BASE/capabilities/$CAP/revisions/$REV/statement"
curl -sb "$COOKIE" -o statement.sig   "$BASE/capabilities/$CAP/revisions/$REV/signature"
curl -sb "$COOKIE" -o public-key.pem  "$BASE/signing-keys/$KEY/public-key"
openssl pkeyutl -verify -pubin -inkey public-key.pem -rawin -in statement.json -sigfile statement.sig
# → Signature Verified Successfully
```
