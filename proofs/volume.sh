#!/usr/bin/env bash
# Dados sobrevivem a compose down (sem -v) + up: o volume pgdata guarda o estado.
source "$(dirname "$0")/lib.sh"

csv=$(variant_csv VOL 10000)
id=$(upload "$csv")
before=$(wait_import "$id" 120)
assert_exact "$id" "$before"

docker compose down >&2
up_wait

after=$(curl -sf "$API/api/imports/$id") || fail "importação $id sumiu depois do restart"
for field in status totalLines validCount errorCount; do
  [ "$(json_field "$before" "$field")" = "$(json_field "$after" "$field")" ] \
    || fail "$field mudou: $(json_field "$before" "$field") → $(json_field "$after" "$field")"
done
assert_exact "$id" "$after"

pass "importação $id intacta depois de down + up ($(json_field "$after" validCount) transações)"
