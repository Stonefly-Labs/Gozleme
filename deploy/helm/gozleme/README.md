# Gözleme Helm chart (GOZ-0000 foundation + GOZ-0001 local identity)

Structure-level chart for the API, dashboard and migration Job. It is rendered and linted in CI;
it has **not** yet been validated against a live Kubernetes cluster (tracked for the
operational-validation spec). PostgreSQL is operator-provided.

## Prerequisites

1. PostgreSQL 17+ with roles equivalent to `deploy/postgres/init/01-roles.sh`:
   `gozleme_migrator` (database owner, `NOSUPERUSER NOBYPASSRLS`) and `gozleme_app`
   (`NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE`, `CONNECT`/`TEMPORARY` on the database).
2. A Secret with the two connection strings (never put credentials in values):

   ```bash
   kubectl create secret generic gozleme-database \
     --from-literal=app-connection-string='Host=…;Database=gozleme;Username=gozleme_app;Password=…' \
     --from-literal=migrator-connection-string='Host=…;Database=gozleme;Username=gozleme_migrator;Password=…'
   ```

3. Images `gozleme-api` and `gozleme-web` pushed to a registry you control; set `image.*`.

## Install

```bash
helm install gozleme deploy/helm/gozleme --set image.api.repository=<registry>/gozleme-api ...
```

The migration Job runs as a pre-install/pre-upgrade hook with the migrator credentials; the API
Deployment uses only the application role.

## Telemetry

Set `telemetry.otlpGrpcEndpoint` (API, migrator) and `telemetry.otlpHttpEndpoint` (web; a path
prefix such as `https://host/otlp` is preserved) to your OpenTelemetry collector. The chart does
not ship a collector. **Your collector must redact credential-like attribute keys and values**
equivalent to `deploy/otel/collector.yaml`; application-side hygiene is the primary control and
collector redaction is the backstop (GOZ-0000-IF-21, IF-23). Telemetry is never a health
dependency of Gözleme workloads.

## Local identity (GOZ-0001)

- **HTTPS is expected.** `auth.secureCookies: true` (default) marks the `gozleme_session` cookie
  `Secure`; serve the web tier over HTTPS. Only plain-HTTP local Compose turns it off.
- **Registration.** `auth.registrationEnabled` (default `true`). Set it to `false` to close
  self-registration; existing accounts still sign in.
- **Probes.** Web probes use the anonymous `/api/live`; API probes stay `/health/live` and
  `/health/ready`. `/` and `/api/backend-status` now require a session.
- **Sessions and key ring.** Sessions and the ASP.NET Core Data Protection key ring live in
  PostgreSQL (`gozleme.auth_sessions`, `gozleme.data_protection_keys`), so they survive restarts and
  are shared by replicas. Nothing is written to the read-only container filesystem.
  **Limitation:** the key ring is stored unencrypted at rest. Anyone with read access to these
  tables can forge session cookies, so protect database access and backups accordingly.
- **Database outage.** While PostgreSQL is unreachable, sessions cannot be verified. Protected
  requests get `503 {"error":"unavailable"}` and are never treated as signed in (fail closed).
- **Client addresses.** Set `network.trustedProxies` to the CIDR(s) of the web pods so per-client
  sign-in rate limits see real client addresses. Ensure your ingress *overwrites*
  `X-Forwarded-For`, because Next.js keeps a client-supplied value. With the default (empty), all
  clients behind one web pod share a budget. Account lockout and a global per-instance ceiling
  apply regardless. Rate limits are per API instance, not distributed.
- **Operator recovery (never over HTTP).** There is no email-based reset yet:

  ```bash
  kubectl exec -i deploy/gozleme-api -- dotnet Gozleme.Api.dll reset-password --email <email> --password-stdin < new-password.txt
  kubectl exec deploy/gozleme-api -- dotnet Gozleme.Api.dll unlock --email <email>
  ```

  Both commands end the account's sessions (reset) or clear its lockout (unlock). They are
  audited with the opaque account id. The chart ships no seed job; demo accounts are Compose-only.
