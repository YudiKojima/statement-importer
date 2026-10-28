#!/usr/bin/env bash
# Tamanho comprimido (o que um docker pull baixa) dentro das metas do system design.
source "$(dirname "$0")/lib.sh"

declare -A limit_mb=([api]=25 [worker]=70 [migrator]=60)
over=""

summary "### Tamanho das imagens (comprimido)" "" "| Imagem | Tamanho | Meta |" "|---|---|---|"
for svc in api worker migrator; do
  bytes=$(docker save "$(image "$svc")" | gzip -c | wc -c)
  mb=$(awk "BEGIN { printf \"%.1f\", $bytes / 1000000 }")
  summary "| \`$svc\` | $mb MB | < ${limit_mb[$svc]} MB |"
  awk "BEGIN { exit !($bytes < ${limit_mb[$svc]} * 1000000) }" || over+=" $svc"
done
summary ""

[ -z "$over" ] || fail "acima da meta:$over"
pass "todas as imagens dentro da meta"
