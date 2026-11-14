#!/usr/bin/env bash
# Creates the Gözleme database roles on first initialization of a PostgreSQL data volume.
#   gozleme_migrator — owns the database and schema objects; used only by the one-shot migrator.
#   gozleme_app      — ordinary serving role: no superuser, no BYPASSRLS, no DDL ownership.
# Passwords come from the environment; nothing is hard-coded here.
set -euo pipefail

: "${GOZLEME_MIGRATOR_PASSWORD:?GOZLEME_MIGRATOR_PASSWORD must be set}"
: "${GOZLEME_APP_PASSWORD:?GOZLEME_APP_PASSWORD must be set}"
db="${POSTGRES_DB:?POSTGRES_DB must be set}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" \
  --set=db="$db" \
  --set=migrator_password="$GOZLEME_MIGRATOR_PASSWORD" \
  --set=app_password="$GOZLEME_APP_PASSWORD" <<'SQL'
CREATE ROLE gozleme_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'migrator_password';
CREATE ROLE gozleme_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT PASSWORD :'app_password';
ALTER DATABASE :"db" OWNER TO gozleme_migrator;
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"db" TO gozleme_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
