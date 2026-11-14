# Gözleme Helm chart (GOZ-0000 foundation)

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
