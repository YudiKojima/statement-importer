#!/usr/bin/env bash
# Tabela de CVEs por severidade: imagens chiseled × referência na imagem padrão. Só reporta.
set -euo pipefail

trivy() {
  docker run --rm -e TRIVY_DB_REPOSITORY -e TRIVY_JAVA_DB_REPOSITORY -v /var/run/docker.sock:/var/run/docker.sock -v trivy-cache:/root/.cache \
    "$TRIVY_IMAGE" image --quiet "$@"
}

counts() {
  trivy --format json "$1" | jq -r '
    [.Results[]?.Vulnerabilities[]?.Severity] as $s
    | [($s | map(select(. == "CRITICAL")) | length),
       ($s | map(select(. == "HIGH")) | length),
       ($s | map(select(. == "MEDIUM")) | length),
       ($s | map(select(. == "LOW")) | length),
       ($s | length)] | @tsv'
}

row() {
  local c h m l t out
  out=$(counts "$3") || exit 1
  IFS=$'\t' read -r c h m l t <<<"$out"
  echo "| \`$1\` | $2 | $c | $h | $m | $l | $t |"
}

{
  echo "### CVEs: chiseled × imagem padrão"
  echo
  echo "| Serviço | Base | CRITICAL | HIGH | MEDIUM | LOW | Total |"
  echo "|---|---|---|---|---|---|---|"
  row api "runtime-deps chiseled + Native AOT" ghcr.io/yudikojima/statement-importer-api:local
  row api "aspnet:10.0 (referência)" statement-importer-api:reference
  row worker "runtime chiseled" ghcr.io/yudikojima/statement-importer-worker:local
  row worker "runtime:10.0 (referência)" statement-importer-worker:reference
  echo
} >> "$GITHUB_STEP_SUMMARY"
