#!/usr/bin/env bash
# O proxy serve o Blazor com fallback do SPA, repassa /api para a api e barra upload acima de 200 MiB.
source "$(dirname "$0")/lib.sh"

index=$(curl -sf "$API/") || fail "GET / falhou"
grep -q '_framework/blazor.webassembly' <<<"$index" || fail "GET / não devolveu o index do Blazor"

deep=$(curl -sf "$API/imports/00000000-0000-0000-0000-000000000000") || fail "rota do SPA /imports/{id} falhou"
[ "$deep" = "$index" ] || fail "rota do SPA não caiu no fallback do index.html"

list=$(curl -sf "$API/api/imports") || fail "GET /api/imports falhou"
[[ "$list" == \[* ]] || fail "GET /api/imports não devolveu uma lista JSON: ${list:0:80}"

curl -sf "$API/api/docs" >/dev/null || fail "GET /api/docs falhou"
openapi=$(curl -sf "$API/api/openapi/v1.json") || fail "GET /api/openapi/v1.json falhou"
grep -q '"openapi"' <<<"$openapi" || fail "/api/openapi/v1.json não é um documento OpenAPI"

mkdir -p data
big="data/proof-proxy-too-big.csv"
trap 'rm -f "$big" "$big.response"' EXIT
truncate -s $((200 * 1024 * 1024 + 1)) "$big"
code=$(curl -s -o "$big.response" -w '%{http_code}' -X POST -T "$big" -H 'Content-Type: text/csv' \
  "$API/api/imports?fileName=too-big.csv" || true)
[ "$code" = 413 ] || fail "upload acima de 200 MiB devolveu $code, esperado 413"

pass "index e fallback do SPA, /api e /api/docs via proxy, upload acima de 200 MiB → 413"
