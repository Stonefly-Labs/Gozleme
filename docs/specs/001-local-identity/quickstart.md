# Quickstart: Local Identity and Session Security (GOZ-0001)

## Run the stack and seed demo accounts

```bash
make up                                            # web :3000, api :8080 (127.0.0.1 only)
docker compose --profile demo run --rm seed        # alice@ / bob@ / lockout@demo.gozleme.test
```

The seeded password is `GOZLEME_DEMO_PASSWORD` (throwaway default in `.env.example`).

## Expected observations

| Step | Observation |
|---|---|
| Open http://localhost:3000/ anonymously | Redirect to `/login?returnTo=%2F` |
| `curl -i localhost:8080/api/v1/status` | `401`, empty body |
| `curl -i localhost:3000/api/backend-status` | `401`, `{"reachable":true,"authenticated":false,…}`, no status data |
| `curl localhost:8080/health/live` and `localhost:3000/api/live` | `200` anonymously |
| Register at `/register` with a new `@example.test` email and a 12+ char password | Lands on `/`, status dashboard and account email shown |
| Sign out | Back to `/login`; replaying the old cookie is anonymous |
| Two browsers signed in → "Sign out everywhere" in one | Both redirected to sign-in on next request |
| Six wrong passwords for `lockout@demo.gozleme.test` | Generic "Email or password is incorrect" every time; correct password then also refused for 15 min |
| `GOZLEME_REGISTRATION_ENABLED=false docker compose up -d --wait api` | `/register` shows "Registration is closed"; sign-in still works |
| Jaeger http://localhost:16686, service `gozleme-web` | `gozleme.auth.sign_in` span in a browser → web → api → postgres trace; no emails/cookies in attributes |

## Operator recovery (never over HTTP)

```bash
printf '%s' 'a-new-long-passphrase' | docker compose run --rm -T api reset-password --email alice@demo.gozleme.test --password-stdin
docker compose run --rm api unlock --email lockout@demo.gozleme.test
```

Helm: `kubectl exec deploy/<release>-api -- dotnet Gozleme.Api.dll unlock --email …`.

## Autonomous demo (cumulative)

```bash
make demo SPEC=GOZ-0001   # runs GOZ-0000 then GOZ-0001 suites; evidence → docs/specs/001-local-identity/evidence
```

## Configuration

| Env (Compose) | Setting | Default |
|---|---|---|
| `GOZLEME_REGISTRATION_ENABLED` | `Gozleme__Auth__RegistrationEnabled` | `true` |
| `GOZLEME_SECURE_COOKIES` | `Gozleme__Auth__SecureCookies` | `false` in Compose (plain HTTP), `true` in Helm |
| `GOZLEME_TRUSTED_PROXIES` | `Gozleme__Network__TrustedProxies` | private ranges in Compose; loopback in Helm |
| `GOZLEME_DEMO_PASSWORD` | seed only | `local-dev-demo-pass` |

Known limitations:
- Emails are unverified. Password reset is operator-only until the proposed email spec lands.
- Data Protection keys are stored unencrypted in PostgreSQL.
- Per-client rate limits rely on trusted-proxy configuration.
- Rate limits apply per instance.
