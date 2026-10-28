#!/usr/bin/env bash
# Funções comuns das provas. Cada prova faz: source "$(dirname "$0")/lib.sh"
set -euo pipefail
# Sem isso, um fail dentro de $(...) aninhado só encerra o subshell e o erro é engolido
shopt -s inherit_errexit
export MSYS_NO_PATHCONV=1

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

API="${API:-http://localhost:8080}"
TAG="${TAG:-local}"
BASE_CSV="data/1m.csv"
PROOF="$(basename "$0" .sh)"

pass() { echo "✅ $PROOF: $*"; }
fail() { echo "❌ $PROOF: $*" >&2; exit 1; }

summary() {
  printf '%s\n' "$@"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then printf '%s\n' "$@" >> "$GITHUB_STEP_SUMMARY"; fi
}

image() { echo "ghcr.io/yudikojima/statement-importer-$1:$TAG"; }

up_wait() { docker compose up -d --wait --wait-timeout 60 "$@" >&2 || fail "serviços não ficaram healthy em 60 s"; }

sql() { docker compose exec -T postgres psql -U postgres -d statement_importer -tAc "$1"; }

json_field() {
  { grep -o "\"$2\":\"\?[^,\"}]*" <<<"$1" || true; } | head -1 | sed -E 's/^[^:]*:"?//'
}

# O prefixo novo muda o hash do arquivo e os transaction_id, então cada execução é uma importação nova.
variant_csv() {
  [ -f "$BASE_CSV" ] || dotnet run tools/generate-csv.cs -- --lines 1000000 --seed 42 --out "$BASE_CSV" >&2
  local prefix="$1$(date +%s)" out="data/proof-$1.csv"
  if [ -n "${2:-}" ]; then
    head -n "$(($2 + 1))" "$BASE_CSV" | sed "s/^TX42-/$prefix-/" > "$out"
  else
    sed "s/^TX42-/$prefix-/" "$BASE_CSV" > "$out"
  fi
  echo "$out"
}

upload() {
  local body
  body=$(curl -sf -X POST --data-binary @"$1" -H 'Content-Type: text/csv' \
    "$API/api/imports?fileName=$(basename "$1")") || fail "upload de $1 falhou"
  json_field "$body" id
}

wait_import() {
  local timeout="${2:-600}" body="" status=""
  local deadline=$((SECONDS + timeout))
  while (( SECONDS < deadline )); do
    body=$(curl -sf "$API/api/imports/$1" || true)
    status=$(json_field "$body" status)
    case "$status" in
      completed|completed_with_errors|failed) echo "$body"; return 0 ;;
    esac
    sleep 1
  done
  fail "importação $1 não terminou em $timeout s (último status: ${status:-?})"
}

assert_exact() {
  local valid errors total rows
  valid=$(json_field "$2" validCount)
  errors=$(json_field "$2" errorCount)
  total=$(json_field "$2" totalLines)
  rows=$(sql "select count(*) from transactions where import_id = '$1'")
  (( valid + errors == total )) || fail "validCount + errorCount ($valid + $errors) ≠ totalLines ($total)"
  (( rows == valid )) || fail "transações gravadas ($rows) ≠ validCount ($valid)"
}
