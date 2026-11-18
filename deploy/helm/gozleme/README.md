# Gözleme Helm chart (GOZ-0000 foundation + GOZ-0001 local identity + GOZ-0003 tenant RLS + GOZ-0004 roles)

Structure-level chart for the API, dashboard and migration Job. It is rendered and linted in CI;
it has **not** yet been validated against a live Kubernetes cluster (tracked for the
operational-validation spec). PostgreSQL is operator-provided.

## Prerequisites

1. PostgreSQL 17+ with roles equivalent to `deploy/postgres/init/01-roles.sh`:
   `gozleme_migrator` (database owner, `NOSUPERUSER NOBYPASSRLS`) and `gozleme_app`
   (`NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE`, `CONNECT`/`TEMPORARY` on the database).
   The `pgcrypto` contrib extension must be *available* (it ships with standard PostgreSQL
   packages). The migrator installs it into schema `gozleme_private`, which it may do as database
   owner because `pgcrypto` is a trusted extension. It must not already be installed in another
   schema of this database.
2. A Secret with the two connection strings and the tenant context key (never put credentials in values):

   ```bash
   kubectl create secret generic gozleme-database \
     --from-literal=app-connection-string='Host=…;Database=gozleme;Username=gozleme_app;Password=…' \
     --from-literal=migrator-connection-string='Host=…;Database=gozleme;Username=gozleme_migrator;Password=…' \
     --from-literal=rls-context-key="$(openssl rand -base64 48)"
   ```

   `rls-context-key` (GOZ-0003) is at least 32 characters. The API signs each transaction's
   account context with it, and the migration Job copies it into the database, where the
   application role cannot read it. **Upgrading from a release before GOZ-0003:** add this key to
   the existing Secret *before* `helm upgrade`. Without it the migration Job and the API refuse
   to start. **Rotation:** change the key, then run `helm upgrade` (the pre-upgrade Job
   re-provisions it, then the API pods restart). Until both have run, organization data is
   unreachable (fail closed), and `gozleme.tenant.denials{reason="invalid"}` rises.

3. A Secret with the signing key-encryption key (GOZ-0007), named by `signing.existingSecret`:

   ```bash
   kubectl create secret generic gozleme-signing \
     --from-literal=signing-key-encryption-key="$(openssl rand -base64 32)"
   ```

   It is base64 of exactly 32 random bytes and wraps each publisher's Ed25519 signing key with AES-256-GCM. It is
   never stored in the database. It is optional for start-up: without it, or with a malformed or wrong value, the
   API starts and publication answers `503 unavailable`, while reading and verifying revisions keep working.
   **Changing it** does not re-encrypt existing keys. Run operator key succession for the affected publishers as a
   one-off Job from the API image, with `ConnectionStrings__GozlemeMigrator`, the new key as
   `Gozleme__Signing__KeyEncryptionKey`, and the args
   `signing-key-succession --undecryptable --reason lost` (or `--publisher <id> --reason lost|compromised`).
   Old keys are never changed or deleted and still verify the revisions they signed.

4. Images `gozleme-api` and `gozleme-web` pushed to a registry you control; set `image.*`.

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
- **Organizations (GOZ-0002).** `organizations.invitationExpiryHours` (default `168`, 1–720) and
  `organizations.redeemPerMinute` (default `10` per account, 1–100). Organization data is
  protected by application authorization and by PostgreSQL row-level security, which is forced
  for the table owner too (GOZ-0003). This closes the interim GOZ-0002-IF-01 exception.
  Row-level security does not protect against SQL injected into a request, which runs with that
  request's own account context, nor against compromised owner, superuser or context-key
  credentials.
- **Built-in roles (GOZ-0004).** **Upgrading from a release before GOZ-0004:** the pre-upgrade
  migration Job is DDL-only (`ScopedRbac`). It needs no new values or secrets. Every existing
  member becomes `admin` and `member`, so no organization loses its managers. New invitees get
  `member` only; afterwards, admins tidy roles from each organization's member list. The database
  refuses role escalation and removing an organization's last admin even for the owner and
  superusers, so repair memberships with SQL only in a session with
  `SET session_replication_role = replica`.
- **Operator recovery (never over HTTP).** There is no email-based reset yet:

  ```bash
  kubectl exec -i deploy/gozleme-api -- dotnet Gozleme.Api.dll reset-password --email <email> --password-stdin < new-password.txt
  kubectl exec deploy/gozleme-api -- dotnet Gozleme.Api.dll unlock --email <email>
  ```

  Both commands end the account's sessions (reset) or clear its lockout (unlock). They are
  audited with the opaque account id. The chart ships no seed job; demo accounts are Compose-only.
