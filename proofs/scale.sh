#!/usr/bin/env bash
# Mede 1 × 3 workers numa importação de 1M de linhas. Só reporta: tempo não é critério de falha.
source "$(dirname "$0")/lib.sh"

seconds_with() {
  up_wait --scale worker="$1"
  local csv id body start end
  csv=$(variant_csv "S$1W")
  start=$(date +%s%N)
  id=$(upload "$csv")
  body=$(wait_import "$id" 900)
  end=$(date +%s%N)
  [ "$(json_field "$body" status)" != failed ] || fail "importação com $1 worker(s) falhou"
  awk "BEGIN { printf \"%.1f\", ($end - $start) / 1e9 }"
}

one=$(seconds_with 1)
three=$(seconds_with 3)

summary "### Escala: 1M de linhas, worker com 0.5 CPU" "" "| Workers | Tempo |" "|---|---|" \
  "| 1 | $one s |" "| 3 | $three s |" ""
pass "1 worker: $one s; 3 workers: $three s"
