# Contract: Secret pattern list v1 (GOZ-0006, FR-010)

Rule 2 of inline-secret detection. The list is a versioned contract: adding, removing or changing a pattern
is a contract change and a **security review item**. There is no randomness (entropy) heuristic
(clarification Q3).

## Where the patterns apply

- Every string value and every member name, in the envelope and in the native tree.
- The unparsed `SKILL.md` body, one line at a time.

A match is `secret_value`. Its rule is `secret.pattern` for a value, or `secret.name` for a member name, in
which case the location is the parent object (see [manifest-validation.md](./manifest-validation.md)).

## Accepted (never flagged)

- `secretref:<provider>/<key>`: provider `^[a-z][a-z0-9-]{0,31}$`, key `^[A-Za-z0-9._/-]{1,128}$` (agent choice A2).
- An MCP placeholder `{NAME}` (`^\{[A-Za-z_][A-Za-z0-9_]*\}$`).
- Empty or absent values, and declarations such as `isSecret: true` with no value.
- UUIDs, hex digests (including `fileSha256`) and opaque IDs. They match none of the patterns below.

## Patterns (ordered)

Each pattern is a .NET regular expression compiled with `CultureInvariant | NonBacktracking`, with a 100 ms
match timeout. A timeout is treated as a match (fail closed) under rule `secret.pattern`. Each pattern runs
over the whole string with search semantics.

| # | ID | Pattern |
|---|---|---|
| 1 | `pem-private-key` | `-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----` |
| 2 | `auth-header` | `(?i)\b(?:bearer|basic|digest)\s+[A-Za-z0-9._~+/=-]{12,}` |
| 3 | `jwt` | `\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{0,}` |
| 4 | `url-userinfo` | `(?i)\b[a-z][a-z0-9+.-]{1,31}://[^\s/:@]{1,256}:[^\s/@]{1,256}@` |
| 5 | `aws-access-key-id` | `\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b` |
| 6 | `github-token` | `\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})` |
| 7 | `gitlab-token` | `\bglpat-[A-Za-z0-9_-]{20,}` |
| 8 | `slack-token` | `\bxox[abposr]-[A-Za-z0-9-]{10,}` |
| 9 | `stripe-key` | `\b(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{16,}` |
| 10 | `google-api-key` | `\bAIza[0-9A-Za-z_-]{35}\b` |
| 11 | `anthropic-key` | `\bsk-ant-[A-Za-z0-9_-]{20,}` |
| 12 | `openai-key` | `\bsk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9_-]{20,}` |
| 13 | `npm-token` | `\bnpm_[A-Za-z0-9]{36}\b` |
| 14 | `azure-connection-secret` | `(?i)(?:AccountKey|SharedAccessKey|SharedAccessSignature)=[A-Za-z0-9+/%=&:-]{16,}` |

## False positives

The patterns are deterministic, so a false positive is fixed by using a secret reference or by removing the
credential-shaped text. The rules are never relaxed per request.

## Tests

`SecretPatternTests` checks each pattern with:
- one synthetic positive;
- the near-miss negatives: a UUID, a SHA-256 hex digest, `Bearer` followed by a short word, a URL without a password, and `sk-` followed by a short word;
- the accepted forms above.

Every synthetic positive is obviously fake (it contains `EXAMPLE`, `SYNTHETIC` or repeated characters) and
is listed in the e2e no-secret scan.
