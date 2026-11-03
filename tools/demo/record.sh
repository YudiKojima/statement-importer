#!/usr/bin/env bash
# Regrava docs/assets/demo.gif. Ferramenta manual, fora do CI. Apaga os volumes do stack.
set -euo pipefail
shopt -s inherit_errexit

cd "$(dirname "$0")/../.."
root=$(pwd -W 2>/dev/null || pwd)

for s in postgres_password db_owner_password db_app_password; do
  [ -f "secrets/$s.txt" ] || cp "secrets/$s.txt.example" "secrets/$s.txt"
done
mkdir -p data/demo docs/assets
rm -rf data/demo/video
[ -f data/demo.csv ] || dotnet run tools/generate-csv.cs -- --lines 1000000 --seed 42 --out data/demo.csv
docker build -t statement-importer-demo tools/demo

echo "Apagando os volumes do stack: a demo parte de um banco vazio."
docker compose down -v
# --build: sem ele, o GIF sairia com a UI da última imagem construída.
docker compose up -d --build --wait --scale worker=3
trap 'docker compose up -d --wait' EXIT

MSYS_NO_PATHCONV=1 docker run --rm --init \
  --network statement-importer_public \
  --mount "type=bind,src=$root/data,dst=/data,readonly" \
  --mount "type=bind,src=$root/data/demo,dst=/out" \
  --mount "type=bind,src=$root/docs/assets,dst=/assets" \
  statement-importer-demo

ls -l docs/assets/demo.gif
