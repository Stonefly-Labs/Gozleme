# Contract: Revision Attestation Statement v1

**Status**: v1, GOZ-0007. **Algorithm**: Ed25519 (RFC 8032), the only one. **Canonical form**: RFC 8785 (JCS), the
subset below. Research R2–R5 and R14.

## What it proves, and what it does not

> An attestation v1 states that registry **origin** accepted this revision for publisher **P**, through an authorized
> publisher member, and signed it with a **registry-held** key kept for P.
>
> It does **not** prove that P (or any person) held the key. An operator holding the key-encryption key and database
> access could sign any statement. It makes no claim about other registries.

**Unsupported in v1**: delegated signing, key transfer, chains of signers, witnesses, transparency-log inclusion and
cross-registry authority. Statement v1 has **no field** for any of them. A verifier MUST refuse any statement that
carries an unknown member, statement version or algorithm, or a key that is not the revision publisher's key (CR-05).

## Statement members (exactly these; all required)

| Member | Type | Value |
|---|---|---|
| `statementVersion` | integer | `1` |
| `type` | string | `gozleme.revision-attestation` |
| `registryOrigin` | string | the registry origin host (GOZ-0005) |
| `publisher` | string | canonical publisher ID `gz://<origin>/pub/<uuid>` |
| `capability` | string | canonical capability ID `gz://<origin>/cap/<uuid>` |
| `version` | string | the declared SemVer 2.0.0 version, as submitted |
| `sequence` | integer | per-capability publication sequence, from 1 |
| `native` | object | `format`, `formatVersion`, `encoding` (strings), `digest` (`sha256:<hex64>` of the native UTF-8 bytes), `byteLength` (integer) |
| `adapter` | object | `id` (for example `mcp`), `version` (for example `1`) |
| `projection` | object | `version` (`1`), `digest` (`sha256:<hex64>` of the canonical projection bytes) |
| `publishedAt` | string | RFC 3339 UTC with six fractional digits, for example `2026-11-18T10:15:30.123456Z` |
| `key` | object | `id` (`sha256:<hex64>` of the 32-byte raw public key), `algorithm` (`Ed25519`) |

No alias, handle, display name, title, organization name or account information appears.

## Canonical bytes (RFC 8785 subset)

- UTF-8, no BOM, and no whitespace outside strings.
- Object members are sorted by the UTF-16 code units of their names, recursively.
- Strings are escaped as ECMAScript `JSON.stringify` escapes them: `\"`, `\\`, `\b`, `\f`, `\n`, `\r`, `\t`; other
  U+0000–U+001F as `\u00xx` (lowercase hex); everything else literal.
- Numbers are integers only, in the range −(2^53 − 1)…2^53 − 1, written in shortest decimal form. Any other number is
  outside v1.
- Duplicate member names and lone surrogates are invalid.

The same rules give the **projection canonical form**: `projection.digest` is computed over the canonical bytes of
projection v1 (GOZ-0006 `contracts/projection-v1.md`). Gözleme stores and serves exactly those bytes.

## Signature

`signature = Ed25519.Sign(privateKey, statementBytes)`, where `statementBytes` are the canonical bytes above. The
signature is 64 raw bytes. There is no envelope and no pre-hash.

## Downloads (all `manifest.read`)

| Download | Media type | Content |
|---|---|---|
| `…/revisions/{r}/native` | `application/json`, `application/yaml` or `text/markdown` (by encoding), `charset=utf-8` | the native bytes as published |
| `…/revisions/{r}/projection` | `application/json` | canonical projection bytes |
| `…/revisions/{r}/statement` | `application/json` | canonical statement bytes (the signed bytes) |
| `…/revisions/{r}/signature` | `application/octet-stream` | 64 raw signature bytes |
| `…/publishers/{p}/signing-keys/{k}/public-key` | `application/x-pem-file` | SubjectPublicKeyInfo PEM (`-----BEGIN PUBLIC KEY-----`, RFC 8410) |

Every download carries `Content-Disposition: attachment`, `Cache-Control: no-store` and `X-Content-Type-Options:
nosniff`.

## Independent verification

```sh
openssl pkeyutl -verify -pubin -inkey public-key.pem -rawin -in statement.json -sigfile statement.sig
sha256sum native.* projection.json   # compare with native.digest and projection.digest in statement.json
```

```js
crypto.verify(null, statementBytes, publicKeyPem, signatureBytes) // Node ≥ 18
```

## Gözleme verification result

`{ "status": "verified" | "failed", "reason": null | <reason>, "key": { "id", "status": "active" | "retired", "retiredAt"?, "retiredReason"? } }`

The first failure wins, in this order:

| Reason | Meaning |
|---|---|
| `malformed_statement` | not UTF-8 JSON, or the bytes are not their own canonical form |
| `unknown_statement_version` | `statementVersion` ≠ 1 |
| `unknown_field` | a member not listed above, at any level, or a listed member missing |
| `unknown_algorithm` | `key.algorithm` ≠ `Ed25519` |
| `unknown_key` | `key.id` is not the attestation's key, or that key is not the revision publisher's |
| `signature_invalid` | Ed25519 verification fails |
| `statement_mismatch` | a statement member differs from the stored revision |
| `native_digest_mismatch` | SHA-256 of the stored native bytes differs |
| `projection_digest_mismatch` | SHA-256 of the stored projection bytes differs |

Only `verified` is verified. The result is computed on every request and never stored.

## Versioning

Any change to members, canonical form, algorithm or meaning is a new `statementVersion`. A v1 verifier refuses it
(`unknown_statement_version`).
