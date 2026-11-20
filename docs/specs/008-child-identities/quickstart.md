# Quickstart: Child Capability Identities and Projections

## Prerequisites

- Docker with Compose, .NET 10 SDK and Node 24, the same as GOZ-0007. No new package, environment variable, secret or
  Helm value.
- **Existing local volume**: no `make reset` is needed.
  - `make up` runs the new migration `20261122000000_ChildIdentities`.
  - The `migrate` install step then gives every existing revision (for example those of the GOZ-0007 demo) its
    generation 1 and children (research R6).
- The seed is unchanged.

## Run the automated checks

```bash
make test       # .NET unit + Testcontainers integration (PostgreSQL 17) and Vitest
make lint
make build      # includes next build; build the images before the demo (GOZ-0006-IF-14)
make validate
make demo       # cumulative Playwright run; goz-0008 is now the highest project
```

Key suites:

| Suite | Checks |
|---|---|
| `ChildAssignerTests` (unit) | match by exact kind + native name; title, description and position ignored; rename → new child; case and normalization variants are different keys; kind change → different child; install mode duplicates (first by position keeps the key); counts; deterministic mapping over a fixed sequence |
| `ChildEntriesTests` (unit) | duplicate groups and `publish.duplicate_entry` problems (every entry, ordered, capped at 50, no name echoed), including the `api` derived name equal to an `operationId` |
| `ProjectionRegeneratorTests` (unit) | the stored native bytes of each family re-project to byte-identical canonical bytes; an entries change is detected |
| `ChildPublishTests` | US1 and US2: four children from one MCP revision; second revision keeps, retitles, drops and adds; a lower version published later matches by sequence; same key in two capabilities → two children; duplicate keys → `422` and nothing stored, while validate still answers `200`; the `201` body is unchanged |
| `ChildIntegrityTests` | direct SQL as `gozleme_app` with a publisher context: a wrong child reuse, a second child for a key, a mismatched entry, a duplicate child, a later generation, an orphan child and a revision without generation 1 are all refused |
| `ChildImmutabilityTests` | `UPDATE`, `DELETE` and `TRUNCATE` on the three tables refused as `gozleme_app` and as `gozleme_migrator`; `POST`/`PUT`/`PATCH`/`DELETE` on child and projection routes `405` |
| `ChildAuthorizationTests` | role × the five endpoints against the contract; non-member, foreign, malformed and swapped IDs (a child ID in the generation route and the reverse, a revision ID as a child ID) → identical empty `404`; signed-out `401`; suspended publisher's children readable |
| `ChildIsolationTests` | cross-tenant matrix for the three tables, both logins |
| `ChildInstallTests` | migrate to `ManifestRevisions`, publish revisions (including an `a2a` card with duplicate skill IDs), migrate to latest + install: children and generation 1 as if published with the feature, the duplicate rule of FR-016a, idempotent repeat, every revision still verifies; publication refused with `503` while a capability has an uninstalled revision |
| `ProjectionRegenerateTests` | the command: generation 2 recorded with the same child IDs, identical digest reported, exit 0; generation 1 bytes, digest and attestation unchanged; unknown revision exit 2; tampered native or changed entries exit 3 with nothing recorded; without the owner login it is refused; audit and output hold opaque IDs only |
| `ChildTelemetryTests` | `gozleme.children.assign` counts, `gozleme.projection.generation.record`, the counters with bounded labels; no name, title, native name, digest or document in spans or logs |
| `RevisionChildrenPerformanceTests` | a 1,000-entry revision publishes within the GOZ-0007 budget + 1 s (SC-006) |

Pins that change deliberately:

| Pin | Change |
|---|---|
| `OrganizationPermissionContractTests` | 22 actions, **32** scoped endpoints |
| `EndpointAuthorizationTests` | the five new endpoints |
| migration pins | 9 migrations, latest `ChildIdentities` |
| `BootstrapBoundaryTests` | table list + `capability_children`; `(manifest\|revision\|projection)` also allows `projection_generations` and `revision_children` |
| `TableClassification` and `RlsCatalog` | three more tenant-owned tables; `DefinerHelpers` unchanged |
| `RevisionAtomicityTests` and other GOZ-0007 tests inserting revisions by SQL | add generation 1 and children, or expect the new deferred refusal |

## Demo walkthrough (what `make demo` drives)

carol holds `{member, publisher}` in **Seeded Demo Org**. The suite publishes to the seeded capability `manifest-demo`
with run-unique versions (`2.0.0-r<runid>`, `2.1.0-r<runid>`) and run-unique native names (`search_r<runid>`, …),
because revisions and children are permanent (research R14).

1. **Browse children** (User Story 1).
   - carol validates and publishes an MCP manifest with tools `search_r<runid>` and `fetch_r<runid>`, one resource and
     one prompt.
   - The revision page lists 4 children, each linked by its `gz://…/cap/<uuid>/<kind>/<uuid>` ID.
   - The capability's **Children** view contains them, with the fixed notice "Children are declared by the publisher
     in the manifest. They are not verified behaviour."
   - Each child page shows kind, child ID, parent capability and history. The API returns the same.
   - Screenshots: Children view, child page, revision Children section.
2. **Continuity** (User Story 2).
   - A second revision keeps `search_r<runid>`, retitles `fetch_r<runid>`, drops the prompt and adds `index_r<runid>`.
   - The kept and retitled tools have the same child IDs, the new tool has a new ID, and the prompt's page shows
     **Absent** for the new revision. The first revision still lists exactly its 4 children.
   - **Duplicate keys**: MCP validation already refuses two tools with one name, so the suite uses an `a2a` card with
     two skills of one `id` (GOZ-0006-IF-07). Validate answers `200`. Publish answers `422 publish.duplicate_entry`
     with two `projection` locations, and nothing is stored.
   - Screenshots: child page with Present/Absent history, the `422` problem list.
3. **Generations are not child IDs** (User Story 3).
   - The suite runs `docker compose run --rm migrate projection-regenerate --revision <first revision>` (exit 0,
     "digest equals generation 1: yes").
   - The revision's **Projection generations** section shows generation 1 **Attested at publication** and generation 2
     **Not attested**, with equal digests. Every child keeps its ID, and the revision still verifies.
   - The generation ID in a child route and a child ID in the generation route both answer `404`.
   - An unknown revision gives exit 2.
   - Screenshot: Projection generations section.
4. **Immutable and private** (User Story 4).
   - `psql` as `gozleme_app` (carol's context) and as `gozleme_migrator`: `UPDATE`, `DELETE` and `TRUNCATE` on each
     table are refused.
   - dave (Seeded Partner Org) gets the identical empty `404` on all five endpoints.
   - A signed-out visitor is redirected to sign in (pages) and gets `401` (API).
   - `DELETE` on a child route gives `405`.
5. **Existing revisions** (User Story 5). The GOZ-0007 suite's revisions (already in the stack) have generation 1 and
   children, and still verify.
6. **Telemetry**.
   - Jaeger shows `gozleme.children.assign` (with the four counts) under `gozleme.manifest.publish`,
     `gozleme.projection.generation.record` and `gozleme.operator.projection_regenerate`. The trace screenshots go
     into the PR.
   - The metrics show `gozleme_children_assignments_total` and `gozleme_projection_generations_total`.
   - A no-secret scan of Jaeger, Loki and API logs finds no run-unique native name, title or digest.
7. **Regression**: the `goz-0000` … `goz-0007` suites, with screenshots under `evidence/screenshots/regression-goz-NNNN/`.

Evidence is written to `docs/specs/008-child-identities/evidence/`: `demo-run.json`, `children-evidence.json` (child
IDs, generation IDs and the run ID) and `screenshots/`.

## Check by hand

```bash
BASE=http://localhost:3000/api/organizations/$ORG/publishers/$PUB/capabilities/$CAP
curl -sb "$COOKIE" "$BASE/children" | jq '.items[] | {canonicalId, kind}'
curl -sb "$COOKIE" "$BASE/revisions/$REV/projections" | jq '.items[] | {generation, attested, digest}'
docker compose run --rm migrate projection-regenerate --revision "$REV"
# → <rev> generation 2 <gen-id> digest equals generation 1: yes
```
