# Child Identity Contract v1 (GOZ-0008)

This is a living, versioned contract. A change to the ID form, the match key or the duplicate rule is a contract
change and needs a maintainer decision (GOZ-0005-IF-12).

## Child ID form (maintainer Q1, 2026-11-19)

```text
gz://<origin>/cap/<capability-uuid>/<kind>/<child-uuid>
```

- `<origin>` is the verified registry origin, as for `gz://<origin>/pub/<uuid>` and `gz://<origin>/cap/<uuid>`
  (`docs/specs/005-publisher-identities/contracts/namespace.md`, which gains an additive section that points here).
- `<capability-uuid>` is the owning capability's permanent UUID. A child never moves to another capability.
- `<kind>` ∈ `tool`, `resource`, `resource-template`, `prompt`, `skill`, `operation`, `webhook`, `channel`: the
  projection v1 entry kind. A child's kind never changes.
- `<child-uuid>` is a UUIDv7 and is the only stored part. In API responses the rendered form is `canonicalId` and the
  UUID is `id`.
- The ID never embeds or derives from a native name, title, alias, handle, organization ID, entry position, revision
  ID, sequence or projection generation.
- Children have **no friendly alias and no alias reservation**.
- A child ID is never reassigned, changed or deleted (FR-003).

## Match key (maintainer Q2)

- The key is (`kind`, `nativeName`) from the stored projection v1 entry, compared by exact bytes.
- When revision *r* of capability *c* is published, each entry with a key matching a **non-duplicate** child of *c* is
  that child, and every other entry creates a new child. Matching follows publication `sequence`.
- Title, description and position never affect matching. A rename is a new child, and the old child is absent from
  that revision.
- Children of different capabilities never share an identity.
- The children recorded for a revision never change.

## Duplicate keys

- **At publication** (rule `publish.duplicate_entry`, [publish-delta.md](./publish-delta.md)): a projection with two
  or more entries of one key is refused with `422`, and nothing is stored.
- **Revisions published before GOZ-0008** (clarify Q1, FR-016a): the first entry of a key, by position, takes or
  matches the key's child. Each further entry gets a new child with `duplicate: true`. A duplicate child is never
  matched, so it is absent from every later revision.

## Not verified behaviour

Children are declared by the publisher in the manifest and are not verified behaviour (GOZ-0006-IF-12). A child has
no approval, trust, safety, lifecycle or verification state in this version. Its only per-revision state is
`present` or `absent`.

The child summary member `latest` (and the `child-latest` link) means **last declared in**: the highest-sequence
revision of the capability in which this child is present. It is a per-child display fact, not a latest-revision
designation for the capability; GOZ-0007 defines none, and GOZ-0026 and GOZ-0027 own that question (GOZ-0008-IF-13,
maintainer ruling 2026-11-19).

## Portability

A child ID is anchored on the canonical capability ID (GOZ-0007-IF-08). Portable references to children in
lockfiles, federation and public discovery are not defined here (GOZ-0012, GOZ-0028, GOZ-0030).
