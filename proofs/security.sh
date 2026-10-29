#!/usr/bin/env bash
# Containers de app sem root e sem shell; api e worker conectam com um papel sem privilégio de dono.
source "$(dirname "$0")/lib.sh"

for svc in api worker migrator; do
  user=$(docker image inspect -f '{{.Config.User}}' "$(image "$svc")")
  case "$user" in ""|root|0|0:0) fail "$svc roda como root (User='$user')" ;; esac
done

for svc in api worker; do
  if shell_out=$(docker run --rm --entrypoint /bin/sh "$(image "$svc")" -c true 2>&1); then fail "$svc tem shell"; fi
  case "$shell_out" in *"no such file"*|*"not found"*) ;; *) fail "$svc: docker run falhou por outro motivo: $shell_out" ;; esac
done

# -h postgres cai na regra scram-sha-256 do pg_hba; localhost é "trust" na imagem e aceitaria qualquer senha.
app_sql() {
  docker compose exec -T postgres sh -c \
    'PGPASSWORD="$(tr -d "\r\n" < /run/secrets/db_app_password)" psql -h postgres -U importer_app -d statement_importer -v ON_ERROR_STOP=1 -tA' <<<"$1"
}

app_super=$(app_sql 'select rolsuper from pg_roles where rolname = current_user;' || true)
[ "$app_super" = f ] || fail "importer_app: rolsuper='$app_super'"
owner_super=$(sql "select rolsuper from pg_roles where rolname = 'importer_owner'" || true)
[ "$owner_super" = f ] || fail "importer_owner: rolsuper='$owner_super'"
app_sql 'select count(*) from imports;' >/dev/null || fail "importer_app não lê imports"
if drop_out=$(app_sql 'begin; drop table transactions; rollback;' 2>&1); then
  fail "importer_app conseguiu DROP TABLE transactions"
fi
case "$drop_out" in *"must be owner of table transactions"*) ;; *) fail "DROP negado por outro motivo: $drop_out" ;; esac

pass "api, worker e migrator sem root; api e worker sem shell; importer_app só com DML"
