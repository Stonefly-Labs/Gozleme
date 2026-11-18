# Contract: Publisher Signing Keys, Key-Encryption Key and Operator Succession

GOZ-0007 Q1, Q5 and clarify Q3. Research R6, R7 and R9.

## Custody

- **One key per publisher**: each publisher has at most one **active** Ed25519 signing key, registry-held. It is
  created lazily inside the transaction of the publisher's first publication. Publisher registration (GOZ-0005) is
  unchanged, and a publisher that has never published has no key ("no key yet").
- **Public part**: the public key, its key ID (`sha256:` of the raw public key) and its generation are immutable and
  member-readable.
- **Private part**: the private key seed is stored only as AES-256-GCM ciphertext under the **key-encryption key
  (KEK)**. The associated data binds it to the publisher and key ID. It is never returned by any API, never logged,
  traced or shown, and never protected by Data Protection or by the database alone.

## Key-encryption key

| Deployment | Source |
|---|---|
| Configuration | `Gozleme:Signing:KeyEncryptionKey`, base64 of exactly 32 random bytes |
| Compose | `GOZLEME_SIGNING_KEK`; `.env.example` holds a throwaway, clearly labelled local value |
| Helm | key `signing-key-encryption-key` in a Secret named by a new value `signing.existingSecret` (default `gozleme-signing`), mounted as an environment variable into the API and the migrate image used for operator Jobs; the value itself never appears in values |

Generate one with `openssl rand -base64 32`.

- **Missing, malformed or wrong KEK**: the API still starts. Publication answers `503 unavailable` and stores nothing.
  Reads, downloads and verification keep working.
- **Changing the KEK** does not re-encrypt existing keys. Keys under the old KEK become undecryptable, and the operator
  recovers by key succession.

## Operator command `signing-key-succession`

The command is run like `migrate` (Compose: `docker compose run --rm migrate signing-key-succession …`; Helm: a
one-off Job from the migrate image). It has **no HTTP route**. It connects as the owner with no account context (the
operator), and it needs the current KEK.

```text
signing-key-succession --publisher <uuid> --reason lost|compromised
signing-key-succession --undecryptable --reason lost
```

For each publisher, in one transaction under an exclusive per-publisher advisory lock, the command:
1. reads the active key (generation n);
2. creates generation n + 1 encrypted under the current KEK;
3. inserts the retirement of generation n with the reason and successor.

It commits, then writes audit event 1503.

- **Old keys**: they are never changed or deleted. They still verify every revision they signed, and their private
  part is never used again.
- **Publications in flight**: they hold the shared form of the same lock. They commit with the old key before the
  retirement, or sign with the new key after it.
- **Suspended publishers**: the command works for them too, and publication stays refused.
- **Output**: opaque IDs only, one line per publisher: `<publisherId> retired <keyUuid> successor <keyUuid>`.

| Exit | Meaning |
|---|---|
| 0 | done (for `--undecryptable`, also when there was nothing to do) |
| 1 | failed (database, KEK missing or invalid); nothing committed |
| 2 | unknown publisher, or publisher with no key yet |
| 64 | usage |

## What succession is not

It is a **local operator action** on this registry. It is not rotation by the publisher, not delegation, not a
transfer of authority, and not revocation of earlier signatures, which stay valid and are shown with the retirement
time and reason. A `compromised` retirement is shown prominently. Gözleme does not decide which earlier signatures
are genuine (GOZ-0033). Federation peers MUST NOT trust a succession without their own check (GOZ-0029, CR-05).
API and UI rotation are GOZ-0039.

## Key view (member-level)

`GET …/publishers/{p}/signing-keys` returns
`{ publisherId, keys: [{ id, keyId, algorithm, generation, createdAt, status: active|retired, retiredAt?, retiredReason?, successorId? }] }`,
ordered by generation. The list is empty when there is no key yet. No private or encrypted material is included.
