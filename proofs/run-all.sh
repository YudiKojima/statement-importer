#!/usr/bin/env bash
# Executa todas as provas contra o stack do compose.yml. Para na primeira falha.
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"

for proof in healthy network security image-size volume scale resilience; do
  echo "▶ $proof"
  bash "$dir/$proof.sh"
done
