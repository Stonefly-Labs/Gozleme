# Web routes, probes and operator commands (GOZ-0001)

## Browser-facing pages (Next.js)

| Path | Access | Behaviour |
|---|---|---|
| `/` | Signed in | Status dashboard + account bar (email, "Sign out", "Sign out everywhere"). Anonymous → 307 `/login?returnTo=%2F` |
| `/login` | Anonymous | Email + password form; generic error; link to `/register` only when registration is enabled. Signed in → `/` |
| `/register` | Anonymous | Email, password, confirm; field errors; "Registration is closed" when disabled. Signed in → `/` |

## Browser-facing route handlers (same-origin; unsafe methods require same-origin per research R5)

| Route | Method | Forwards to API | Notes |
|---|---|---|---|
| `/api/auth/options` | GET | `GET /api/v1/auth/options` | Anonymous |
| `/api/auth/register` | POST | `POST /api/v1/auth/register` | Relays `set-cookie` |
| `/api/auth/sign-in` | POST | `POST /api/v1/auth/sign-in` | Relays `set-cookie` |
| `/api/auth/sign-out` | POST | `POST /api/v1/auth/sign-out` | Relays cookie deletion |
| `/api/auth/sign-out-all` | POST | `POST /api/v1/auth/sign-out-all` | Relays cookie deletion |
| `/api/auth/session` | GET | `GET /api/v1/auth/session` | 401 when anonymous |
| `/api/backend-status` | GET | `GET /api/v1/status` | **Now requires session**; 401 `{"reachable":true,"authenticated":false,"checkedAt":…}` |
| `/api/live` | GET | — | **Anonymous** web liveness `{"status":"live"}`; used by Compose/Helm probes |
| `/api/otel/v1/traces` | POST | collector | Anonymous; + same-origin check and per-client rate limit (429) |

Unchanged anonymous API probes: `/health/live`, `/health/ready`.

## Operator commands (API image, one-shot; never HTTP)

```text
dotnet Gozleme.Api.dll seed
dotnet Gozleme.Api.dll reset-password --email <email> --password-stdin
dotnet Gozleme.Api.dll reset-password --email <email> --password-file <path>
dotnet Gozleme.Api.dll unlock --email <email>
```

Exit codes: `0` success, `1` unexpected error, `2` unknown account, `3` password policy failure,
`64` usage error. Output never includes the email or password.
