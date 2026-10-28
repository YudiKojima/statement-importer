#!/bin/sh
# Roda só com pgdata vazio. O entrypoint do Postgres pode executar ou fazer source deste arquivo,
# por isso ele não usa `set` nem `exit`: um erro do psql (ON_ERROR_STOP) já aborta o init.

owner_password=$(tr -d '\r\n' < /run/secrets/db_owner_password)
app_password=$(tr -d '\r\n' < /run/secrets/db_app_password)

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v owner_password="$owner_password" -v app_password="$app_password" <<'SQL'
CREATE ROLE importer_owner LOGIN PASSWORD :'owner_password';
CREATE ROLE importer_app LOGIN PASSWORD :'app_password';

ALTER DATABASE statement_importer OWNER TO importer_owner;
ALTER SCHEMA public OWNER TO importer_owner;

REVOKE ALL ON DATABASE statement_importer FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE statement_importer TO importer_app;
GRANT USAGE ON SCHEMA public TO importer_app;

ALTER DEFAULT PRIVILEGES FOR ROLE importer_owner IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO importer_app;
ALTER DEFAULT PRIVILEGES FOR ROLE importer_owner IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO importer_app;
SQL
