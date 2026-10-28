#!/usr/bin/env bash
# Com 3 workers, mata um no meio da importação de 1M de linhas: contagem exata, zero duplicatas.
source "$(dirname "$0")/lib.sh"

up_wait --scale worker=3
csv=$(variant_csv RES)
id=$(upload "$csv")

deadline=$((SECONDS + 120))
until [ "$(sql "select count(*) from import_chunks where import_id = '$id' and status = 'done'")" -ge 1 ]; do
  (( SECONDS < deadline )) || fail "nenhum bloco concluído em 120 s"
  sleep 0.5
done

[ "$(json_field "$(curl -sf "$API/api/imports/$id")" status)" = processing ] \
  || fail "importação já tinha terminado antes do kill; a prova não é conclusiva"
victim=$(docker compose ps -q worker | head -1)
docker kill "$victim" >/dev/null
echo "worker $(docker inspect -f '{{.Name}}' "$victim") morto com a importação em andamento"

body=$(wait_import "$id" 900)
[ "$(json_field "$body" status)" = completed_with_errors ] \
  || fail "status $(json_field "$body" status), esperado completed_with_errors"
assert_exact "$id" "$body"

up_wait
summary "### Resiliência" "" "3 workers, 1 morto no meio da importação: $(json_field "$body" validCount) transações = validCount." ""
pass "$(json_field "$body" validCount) transações = validCount, mesmo com um worker morto no meio"
