# Fase 03 — Provas executáveis e CI: plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: usar `superpowers:subagent-driven-development` (recomendado) ou `superpowers:executing-plans` para executar tarefa por tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** provar automaticamente, a cada push, o que a fase 01 mediu à mão (saúde, rede, segurança, tamanho, volume, escala, resiliência), com usuários de banco de privilégio mínimo, Trivy comparativo e publicação no GHCR.

**Arquitetura:** o Postgres cria dois papéis no primeiro boot (`importer_owner` para o `migrator`, `importer_app` para `api` e `worker`). As provas são scripts bash em `proofs/` que rodam contra o `compose.yml` real e escrevem medições no job summary. O workflow tem dois jobs em paralelo (`test` e `stack`) e um `publish` só em push na `master`. Desenho em [`design.md`](design.md); visão completa em [`../../architecture/system-design.md`](../../architecture/system-design.md).

**Stack:** Bash (Linux e Git Bash), Docker Compose (secrets e configs), PostgreSQL 17, GitHub Actions, buildx bake com cache `type=gha`, Trivy, GHCR.

## Restrições globais

- Raiz do repositório: `C:\Program Files\source\statement-importer`. Comandos rodam a partir da raiz, no **Git Bash**.
- **Nunca commitar durante a fase** (regra do [`CLAUDE.md`](../../../CLAUDE.md)). Onde o skill pediria commit, há um **checkpoint**. O commit acontece só no `/finishing-a-development-branch`.
- Docker Desktop rodando. Se `docker info` falhar, pedir ao usuário para abri-lo.
- O código C# **não muda** nesta fase, exceto o target `reference` nos Dockerfiles.
- Imagens e actions de terceiros com versão fixa: `busybox:1.37`, `aquasec/trivy:0.75.0`, `actions/checkout@v7.0.1`, `actions/setup-dotnet@v6.0.0`, `docker/setup-buildx-action@v4.4.1`, `docker/bake-action@v7.4.0`, `docker/build-push-action@v7.4.0`, `docker/login-action@v4.6.0` (versões consultadas em 2026-10-25, todas com mais de duas semanas).
- Scripts bash com `set -euo pipefail` (via `lib.sh`), sem `jq` nas provas (o Git Bash não traz). O `jq` só aparece no script de relatório do Trivy, que roda só no CI.
- Nada de segredo commitado: `secrets/*.txt` continua no `.gitignore`; só os `.example` entram.
- Metas de tamanho comprimido: `api` < 25 MB, `worker` < 70 MB, `migrator` < 60 MB.

## Mapa de arquivos

```
docker/postgres/init-roles.sh                      novo: cria importer_owner e importer_app
secrets/postgres_password.txt.example              novo
secrets/db_owner_password.txt.example              novo
secrets/db_app_password.txt.example                novo
secrets/db_password.txt.example                    removido
compose.yml                                        papéis, três secrets, config do init
proofs/lib.sh                                      funções comuns
proofs/run-all.sh                                  executa todas na ordem
proofs/healthy.sh · network.sh · security.sh · image-size.sh · volume.sh · scale.sh · resilience.sh
src/StatementImporter.Api/Dockerfile               target reference
src/StatementImporter.Worker/Dockerfile            target reference
.github/workflows/ci.yml                           jobs test, stack, publish
.github/scripts/trivy-report.sh                    tabela de CVEs chiseled × padrão
docs/architecture/decisions/0006-papeis-de-banco-com-privilegio-minimo.md
docs/architecture/decisions/0007-provas-executaveis-contra-o-stack-real.md
docs/architecture/decisions/README.md · docs/architecture/system-design.md · README.md · CLAUDE.md · docs/project-overview.md
```

---

### Task 0: Branch da fase

**Pré-requisito:** o PR [#2](https://github.com/YudiKojima/statement-importer/pull/2) (README provisório) mergeado na `master`. A Task 10 edita o `README.md`.

- [ ] **Step 1:** Atualizar o `master` e criar o branch, levando os docs da fase (ainda sem commit no `master`):

```bash
git switch master
git stash push -u -- docs/
git pull --ff-only
git switch -c dev/provas-e-ci
git stash pop
git status --short
```

Esperado: `M docs/README.md`, `M docs/project-overview.md`, `?? docs/features/03-provas-e-ci/`, e `README.md` presente (vindo do PR #2).

---

### Task 1: Usuários de banco com privilégio mínimo

**Files:**
- Create: `docker/postgres/init-roles.sh`, `secrets/{postgres_password,db_owner_password,db_app_password}.txt.example`
- Delete: `secrets/db_password.txt.example` (e o `secrets/db_password.txt` local)
- Modify: `compose.yml`

**Interfaces:**
- Produces: papéis `importer_owner` e `importer_app`; secrets do compose `postgres_password`, `db_owner_password`, `db_app_password`, montados no Postgres em `/run/secrets/<nome>`. As provas leem `/run/secrets/db_app_password` dentro do container `postgres`.

- [ ] **Step 1: Script de init**

`docker/postgres/init-roles.sh`:

```sh
#!/bin/sh
# Roda só com pgdata vazio. O entrypoint do Postgres pode executar ou fazer source deste arquivo,
# por isso ele não usa `set` nem `exit`: um erro do psql (ON_ERROR_STOP) já aborta o init.

owner_password=$(tr -d '\r\n' < /run/secrets/db_owner_password)
app_password=$(tr -d '\r\n' < /run/secrets/db_app_password)

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v owner_password="$owner_password" -v app_password="$app_password" <<'SQL'
CREATE ROLE importer_owner LOGIN PASSWORD :'owner_password';
CREATE ROLE importer_app LOGIN PASSWORD :'app_password';

ALTER DATABASE statement_importer OWNER TO importer_owner;
ALTER SCHEMA public OWNER TO importer_owner;

REVOKE ALL ON DATABASE statement_importer FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE statement_importer TO importer_app;
GRANT USAGE ON SCHEMA public TO importer_app;

ALTER DEFAULT PRIVILEGES FOR ROLE importer_owner IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO importer_app;
ALTER DEFAULT PRIVILEGES FOR ROLE importer_owner IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO importer_app;
SQL
```

O `TEMPORARY` é explícito porque o worker faz `COPY` para tabela temporária, e o `REVOKE ... FROM PUBLIC` tira esse privilégio padrão de todos os outros papéis.

- [ ] **Step 2: Secrets de exemplo**

```bash
for s in postgres_password db_owner_password db_app_password; do printf 'change-me' > secrets/$s.txt.example; done
git rm -q secrets/db_password.txt.example
rm -f secrets/db_password.txt
for s in postgres_password db_owner_password db_app_password; do cp secrets/$s.txt.example secrets/$s.txt; done
git status --short secrets/
```

Esperado: os três `.example` como `??`, `db_password.txt.example` como `D`, nenhum `.txt` listado (ignorados).

- [ ] **Step 3: `compose.yml`**

Trocar o bloco `x-db-env` (o `Db__Username` passa a ser por serviço):

```yaml
x-db-env: &db-env
  Db__Host: postgres
  Db__Database: statement_importer
```

No serviço `postgres`, trocar `environment`, `secrets` e o healthcheck, e adicionar `configs`:

```yaml
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: statement_importer
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD_FILE: /run/secrets/postgres_password
    secrets: [postgres_password, db_owner_password, db_app_password]
    configs:
      - source: init-roles
        target: /docker-entrypoint-initdb.d/10-init-roles.sh
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d statement_importer"]
      interval: 5s
      timeout: 3s
      retries: 10
    networks: [internal]
```

No `migrator`:

```yaml
    environment:
      <<: *db-env
      Db__Username: importer_owner
    secrets:
      - source: db_owner_password
        target: Db__Password
```

Em `api` e em `worker` (cada um):

```yaml
    environment:
      <<: [*db-env, *rabbitmq-env]
      Db__Username: importer_app
    secrets:
      - source: db_app_password
        target: Db__Password
```

Trocar o bloco `secrets:` do fim do arquivo e adicionar `configs:`:

```yaml
secrets:
  postgres_password:
    file: ./secrets/postgres_password.txt
  db_owner_password:
    file: ./secrets/db_owner_password.txt
  db_app_password:
    file: ./secrets/db_app_password.txt

configs:
  init-roles:
    file: ./docker/postgres/init-roles.sh
```

- [ ] **Step 4: Validar o compose**

Run: `docker compose config --quiet && docker compose config | grep -A3 "target: Db__Password" | head`
Esperado: sem erro; `source: db_owner_password` e `source: db_app_password` com `target: Db__Password`.

- [ ] **Step 5: Subir do zero**

```bash
docker compose down -v
docker compose up -d --wait --build
docker compose ps -a
docker compose logs postgres | grep -E "10-init-roles|ERROR"
```

Esperado: `api`, `worker`, `postgres`, `rabbitmq` `(healthy)`; `migrator` `Exited (0)`; o log mostra `running /docker-entrypoint-initdb.d/10-init-roles.sh` e nenhum `ERROR`. Se `api`/`worker` falharem com `password authentication failed`, o `target` do secret não foi respeitado: parar e reportar.

- [ ] **Step 6: Conferir papéis e posse**

```bash
docker compose exec -T postgres psql -U postgres -d statement_importer -tAc \
  "select rolname, rolsuper from pg_roles where rolname like 'importer%' order by 1"
docker compose exec -T postgres psql -U postgres -d statement_importer -tAc \
  "select tableowner, count(*) from pg_tables where schemaname = 'public' group by 1"
docker compose exec -T postgres psql -U postgres -d statement_importer -tAc \
  "select usename, count(*) from pg_stat_activity where datname = 'statement_importer' group by 1 order by 1"
```

Esperado: `importer_app|f` e `importer_owner|f`; todas as tabelas com dono `importer_owner`; conexões ativas de `importer_app` (e a do próprio `postgres` da consulta).

- [ ] **Step 7: Importação de ponta a ponta com o `importer_app`**

```bash
dotnet run tools/generate-csv.cs -- --lines 20000 --seed 7 --out data/20k.csv
ID=$(curl -s -X POST --data-binary @data/20k.csv -H 'Content-Type: text/csv' "localhost:8080/api/imports?fileName=20k.csv" | grep -o '"id":"[^"]*"' | cut -d'"' -f4)
sleep 10; curl -s localhost:8080/api/imports/$ID
```

Esperado: `"status":"completed_with_errors"` e `validCount + errorCount = totalLines` (20000). Isso exercita `COPY` em tabela temporária, `INSERT ... ON CONFLICT` e `SELECT ... FOR UPDATE` como `importer_app`.

- [ ] **Checkpoint:** `docker compose ps` saudável e Step 7 verde.

---

### Task 2: Base das provas — `lib.sh`, `healthy.sh`, `run-all.sh`

**Files:**
- Create: `proofs/lib.sh`, `proofs/healthy.sh`, `proofs/run-all.sh`

**Interfaces:**
- Produces (em `lib.sh`, usados por todas as provas): `pass <msg>`, `fail <msg>` (sai com 1), `summary <linhas...>` (stdout + `$GITHUB_STEP_SUMMARY` se existir), `image <servico>` → nome da imagem com `$TAG`, `sql <consulta>` (como `postgres`, via socket local), `json_field <json> <campo>`, `variant_csv <rotulo> [linhas]` → caminho de um CSV com prefixo novo, `upload <arquivo>` → id, `wait_import <id> [timeout_s]` → JSON final, `assert_exact <id> <json>`, `up_wait [args...]`. Variáveis: `API`, `TAG`, `BASE_CSV`.

- [ ] **Step 1: `proofs/lib.sh`**

```bash
#!/usr/bin/env bash
# Funções comuns das provas. Cada prova faz: source "$(dirname "$0")/lib.sh"
set -euo pipefail
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
```

- [ ] **Step 2: `proofs/healthy.sh`**

```bash
#!/usr/bin/env bash
# Todos os serviços healthy em até 60 s e migrator concluído com código 0.
source "$(dirname "$0")/lib.sh"

up_wait
code=$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose ps -aq migrator)")
[ "$code" = 0 ] || fail "migrator saiu com código $code"
pass "api, worker, postgres e rabbitmq healthy; migrator Exited (0)"
```

- [ ] **Step 3: `proofs/run-all.sh`**

```bash
#!/usr/bin/env bash
# Executa todas as provas contra o stack do compose.yml. Para na primeira falha.
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"

for proof in healthy network security image-size volume scale resilience; do
  echo "▶ $proof"
  bash "$dir/$proof.sh"
done
```

- [ ] **Step 4: Permissão de execução no git**

```bash
git add proofs/
git update-index --chmod=+x proofs/*.sh docker/postgres/init-roles.sh
git ls-files -s proofs/ docker/ | cut -c1-6 | sort -u
```

Esperado: `100755`. (`git add` só põe no índice; não é commit. Repetir o `--chmod` para cada script novo das tasks seguintes.)

- [ ] **Step 5: Rodar**

Run: `bash proofs/healthy.sh`
Esperado: `✅ healthy: ...`.

Ver o caminho de falha: `docker compose down && docker compose up -d --wait --wait-timeout 1; echo "exit=$?"`. Esperado: `exit` ≠ 0, porque o RabbitMQ não fica `healthy` em 1 s; é o caso em que o `up_wait` chama `fail`. Depois, `bash proofs/healthy.sh` verde de novo.

- [ ] **Checkpoint:** `bash proofs/healthy.sh` verde.

---

### Task 3: `network.sh` (forma reduzida)

**Files:**
- Create: `proofs/network.sh`

**Interfaces:**
- Consumes: `lib.sh` (`pass`, `fail`).

- [ ] **Step 1: Escrever**

```bash
#!/usr/bin/env bash
# Só a api publica porta (fase 02: só o proxy), Postgres e RabbitMQ não respondem no host,
# e a rede internal não tem saída para a internet.
source "$(dirname "$0")/lib.sh"

for id in $(docker compose ps -q); do
  service=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.service"}}' "$id")
  ports=$(docker port "$id")
  if [ -n "$ports" ] && [ "$service" != api ]; then fail "$service publica porta: $ports"; fi
done

for port in 5432 5672 15672; do
  if timeout 3 bash -c "</dev/tcp/127.0.0.1/$port" 2>/dev/null; then fail "porta $port responde no host"; fi
done

[ "$(docker network inspect -f '{{.Internal}}' statement-importer_internal)" = true ] \
  || fail "rede statement-importer_internal sem internal: true"

# Controle: sem isso, uma máquina offline faria a verificação seguinte passar por engano.
docker run --rm --network bridge busybox:1.37 wget -q -T 5 -O /dev/null http://example.com \
  || fail "controle: sem internet nem na rede bridge, a prova não é conclusiva"
if docker run --rm --network statement-importer_internal busybox:1.37 wget -q -T 5 -O /dev/null http://example.com; then
  fail "a rede internal tem saída para a internet"
fi

pass "só a api publica porta; Postgres e RabbitMQ fechados no host; rede internal sem saída"
```

- [ ] **Step 2: Rodar**

Run: `git add proofs/network.sh && git update-index --chmod=+x proofs/network.sh && bash proofs/network.sh`
Esperado: `✅ network: ...`.

- [ ] **Step 3: Ver falhar**

Run: `docker compose -f compose.yml -f compose.dev.yml up -d --no-build postgres && bash proofs/network.sh; echo "exit=$?"`
Esperado: `❌ network: postgres publica porta: 5432/tcp -> 0.0.0.0:5432` e `exit=1`. Restaurar com `docker compose up -d --wait postgres`.

Se a linha do `/dev/tcp` não funcionar no Git Bash (sempre falhar mesmo com porta aberta), conferir com `timeout 3 bash -c "</dev/tcp/127.0.0.1/8080" && echo aberta`: deve imprimir `aberta`.

- [ ] **Checkpoint:** `bash proofs/network.sh` verde.

---

### Task 4: `security.sh`

**Files:**
- Create: `proofs/security.sh`

**Interfaces:**
- Consumes: `lib.sh` (`image`, `sql`, `pass`, `fail`); secret `/run/secrets/db_app_password` no container `postgres` (Task 1).

- [ ] **Step 1: Escrever**

```bash
#!/usr/bin/env bash
# Containers de app sem root e sem shell; api e worker conectam com um papel sem privilégio de dono.
source "$(dirname "$0")/lib.sh"

for svc in api worker migrator; do
  user=$(docker image inspect -f '{{.Config.User}}' "$(image "$svc")")
  case "$user" in ""|root|0|0:0) fail "$svc roda como root (User='$user')" ;; esac
done

for svc in api worker; do
  if docker run --rm --entrypoint /bin/sh "$(image "$svc")" -c true 2>/dev/null; then fail "$svc tem shell"; fi
done

# -h localhost força autenticação por senha (o socket local confia em qualquer usuário).
app_sql() {
  docker compose exec -T postgres sh -c \
    'PGPASSWORD="$(tr -d "\r\n" < /run/secrets/db_app_password)" psql -h localhost -U importer_app -d statement_importer -v ON_ERROR_STOP=1 -tA' <<<"$1"
}

[ "$(app_sql 'select rolsuper from pg_roles where rolname = current_user;')" = f ] || fail "importer_app é superusuário"
[ "$(sql "select rolsuper from pg_roles where rolname = 'importer_owner'")" = f ] || fail "importer_owner é superusuário"
app_sql 'select count(*) from imports;' >/dev/null || fail "importer_app não lê imports"
if app_sql 'begin; drop table transactions; rollback;' >/dev/null 2>&1; then
  fail "importer_app conseguiu DROP TABLE transactions"
fi

pass "api, worker e migrator sem root; api e worker sem shell; importer_app só com DML"
```

O `DROP` roda dentro de `begin ... rollback`: se a prova falhar, a tabela não é perdida.

- [ ] **Step 2: Rodar**

Run: `git add proofs/security.sh && git update-index --chmod=+x proofs/security.sh && bash proofs/security.sh`
Esperado: `✅ security: ...`.

- [ ] **Step 3: Ver falhar (critério de aceite 3)**

```bash
docker compose exec -T postgres psql -U postgres -c "alter role importer_app superuser"
bash proofs/security.sh; echo "exit=$?"
docker compose exec -T postgres psql -U postgres -c "alter role importer_app nosuperuser"
bash proofs/security.sh
```

Esperado: primeiro `❌ security: importer_app é superusuário` e `exit=1`; depois do `nosuperuser`, verde de novo.

- [ ] **Checkpoint:** `bash proofs/security.sh` verde.

---

### Task 5: `image-size.sh`

**Files:**
- Create: `proofs/image-size.sh`

**Interfaces:**
- Consumes: `lib.sh` (`image`, `summary`, `pass`, `fail`).

- [ ] **Step 1: Escrever**

```bash
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
```

- [ ] **Step 2: Rodar**

Run: `git add proofs/image-size.sh && git update-index --chmod=+x proofs/image-size.sh && bash proofs/image-size.sh`
Esperado: tabela com `api` ~17 MB, `worker` ~45 MB, `migrator` ~50 MB e `✅ image-size`.

- [ ] **Step 3: Ver falhar**

Run: `sed -i 's/\[api\]=25/[api]=5/' proofs/image-size.sh && bash proofs/image-size.sh; echo "exit=$?"; sed -i 's/\[api\]=5/[api]=25/' proofs/image-size.sh`
Esperado: `❌ image-size: acima da meta: api` e `exit=1`.

- [ ] **Checkpoint:** `bash proofs/image-size.sh` verde.

---

### Task 6: `volume.sh`

**Files:**
- Create: `proofs/volume.sh`

**Interfaces:**
- Consumes: `lib.sh` (`variant_csv`, `upload`, `wait_import`, `assert_exact`, `json_field`, `up_wait`).

- [ ] **Step 1: Escrever**

```bash
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
```

- [ ] **Step 2: Rodar**

Run: `git add proofs/volume.sh && git update-index --chmod=+x proofs/volume.sh && bash proofs/volume.sh`
Esperado: `✅ volume: importação ... intacta ...`. Como contraprova manual, `docker compose down -v` seguido de `up --wait` e `curl` no mesmo id devolve `404`.

- [ ] **Checkpoint:** `bash proofs/volume.sh` verde.

---

### Task 7: `scale.sh` e `resilience.sh`

**Files:**
- Create: `proofs/scale.sh`, `proofs/resilience.sh`

**Interfaces:**
- Consumes: `lib.sh` (`variant_csv`, `upload`, `wait_import`, `assert_exact`, `json_field`, `sql`, `summary`, `up_wait`).

- [ ] **Step 1: `proofs/scale.sh`**

```bash
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
```

- [ ] **Step 2: `proofs/resilience.sh`**

```bash
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

victim=$(docker compose ps -q worker | head -1)
docker kill "$victim" >/dev/null
echo "worker $(docker inspect -f '{{.Name}}' "$victim") morto com a importação em andamento"

body=$(wait_import "$id" 900)
[ "$(json_field "$body" status)" = completed_with_errors ] \
  || fail "status $(json_field "$body" status), esperado completed_with_errors"
assert_exact "$id" "$body"

up_wait
pass "$(json_field "$body" validCount) transações = validCount, mesmo com um worker morto no meio"
```

O `up_wait` final volta o stack para 1 worker e remove o container morto, deixando o ambiente como o `healthy.sh` encontra.

- [ ] **Step 3: Rodar**

```bash
git add proofs/scale.sh proofs/resilience.sh
git update-index --chmod=+x proofs/scale.sh proofs/resilience.sh
bash proofs/scale.sh
bash proofs/resilience.sh
```

Esperado: tempos na ordem de ~36 s (1 worker) e ~15 s (3 workers); `✅ resilience: 990009 transações = validCount ...` (o número exato depende do seed 42 e da taxa de erro padrão de 1%).

- [ ] **Step 4: Ver o `assert_exact` falhar**

Pegar o id da última importação e passar ao `assert_exact` um JSON com contadores errados:

```bash
ID=$(curl -s localhost:8080/api/imports | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
bash -c 'source proofs/lib.sh; assert_exact "$1" "{\"validCount\":1,\"errorCount\":0,\"totalLines\":1}"' assert "$ID"; echo "exit=$?"
```

Esperado: `❌ ...: transações gravadas (...) ≠ validCount (1)` e `exit=1`. Confirma que a comparação lê o banco de verdade e não é trivialmente verdadeira.

- [ ] **Step 5: Tudo junto**

Run: `bash proofs/run-all.sh`
Esperado: sete `✅`, na ordem healthy → network → security → image-size → volume → scale → resilience. Anotar o tempo total (esperado: alguns minutos).

- [ ] **Checkpoint:** `bash proofs/run-all.sh` verde no Git Bash.

---

### Task 8: Target `reference` nos Dockerfiles

**Files:**
- Modify: `src/StatementImporter.Api/Dockerfile`, `src/StatementImporter.Worker/Dockerfile`

**Interfaces:**
- Produces: target `reference` em cada Dockerfile, tagueado no CI como `statement-importer-api:reference` e `statement-importer-worker:reference`. O target `runtime` continua sendo o último estágio (default do `docker build`).

- [ ] **Step 1: API** — inserir entre o estágio `publish` e o `runtime`:

```dockerfile
FROM build AS publish-reference
RUN dotnet publish src/StatementImporter.Api/StatementImporter.Api.csproj -c Release -r linux-x64 --self-contained false -p:PublishAot=false -o /out

# Só para o CI: a mesma api sem AOT na imagem padrão, para comparar CVEs com a chiseled. Nunca publicada.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS reference
WORKDIR /app
COPY --from=publish-reference /out ./
ENTRYPOINT ["dotnet", "StatementImporter.Api.dll"]
```

- [ ] **Step 2: Worker** — inserir entre o estágio `publish` e o `runtime`:

```dockerfile
# Só para o CI: o mesmo worker na imagem padrão, para comparar CVEs com a chiseled. Nunca publicada.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS reference
WORKDIR /app
COPY --from=publish /out ./
ENTRYPOINT ["dotnet", "StatementImporter.Worker.dll"]
```

- [ ] **Step 3: Buildar e conferir**

```bash
docker build -f src/StatementImporter.Api/Dockerfile --target reference -t statement-importer-api:reference .
docker build -f src/StatementImporter.Worker/Dockerfile --target reference -t statement-importer-worker:reference .
docker image inspect -f '{{.Config.Entrypoint}}' statement-importer-api:reference statement-importer-worker:reference
docker compose build
```

Esperado: os dois builds sem erro. Se o publish da api falhar por pacote do ILCompiler ausente, é efeito do restore feito com AOT: o `RUN` já não usa `--no-restore`, então o erro indicaria outra causa e deve ser reportado. O `docker compose build` continua gerando as imagens `runtime` (o compose fixa `target: runtime`).

- [ ] **Checkpoint:** `bash proofs/image-size.sh` ainda verde (o target `runtime` não mudou).

---

### Task 9: Workflow do CI

**Files:**
- Create: `.github/workflows/ci.yml`, `.github/scripts/trivy-report.sh`

**Interfaces:**
- Consumes: `proofs/run-all.sh` (Tasks 2–7), targets `reference` (Task 8), `tools/generate-csv.cs`.
- Produces: checks `test` e `stack` no PR; imagens `ghcr.io/yudikojima/statement-importer-{api,worker,migrator}` com `sha-<curto>` e `latest`.

- [ ] **Step 1: Build local com `-warnaserror`**

Run: `dotnet build StatementImporter.slnx -warnaserror`
Esperado: `0 Warning(s)`, `0 Error(s)`. Se algum projeto de teste gerar warning, **parar e reportar** antes de mexer: corrigir warnings em teste está fora do escopo previsto.

- [ ] **Step 2: `.github/scripts/trivy-report.sh`**

```bash
#!/usr/bin/env bash
# Tabela de CVEs por severidade: imagens chiseled × referência na imagem padrão. Só reporta.
set -euo pipefail

trivy() {
  docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v trivy-cache:/root/.cache \
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
  local c h m l t
  IFS=$'\t' read -r c h m l t < <(counts "$3")
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
```

- [ ] **Step 3: `.github/workflows/ci.yml`**

```yaml
name: ci

on:
  pull_request:
  push:
    branches: [master]

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}

permissions:
  contents: read

env:
  DOTNET_NOLOGO: true
  TRIVY_IMAGE: aquasec/trivy:0.75.0

jobs:
  test:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v7.0.1
      - uses: actions/setup-dotnet@v6.0.0
        with:
          global-json-file: global.json
      - run: dotnet build StatementImporter.slnx -warnaserror
      - run: dotnet test --project tests/StatementImporter.UnitTests --no-build
      - run: dotnet test --project tests/StatementImporter.IntegrationTests --no-build

  stack:
    runs-on: ubuntu-24.04
    env:
      VERSION: ${{ github.sha }}
      REVISION: ${{ github.sha }}
    steps:
      - uses: actions/checkout@v7.0.1
      - uses: actions/setup-dotnet@v6.0.0
        with:
          global-json-file: global.json
      - uses: docker/setup-buildx-action@v4.4.1

      - name: Secrets aleatórios
        run: for s in postgres_password db_owner_password db_app_password; do openssl rand -hex 24 > "secrets/$s.txt"; done

      - name: Build das imagens
        uses: docker/bake-action@v7.4.0
        with:
          source: .
          files: compose.yml
          targets: migrator,api,worker
          load: true
          set: |
            migrator.cache-from=type=gha,scope=migrator
            migrator.cache-to=type=gha,scope=migrator,mode=max
            api.cache-from=type=gha,scope=api
            api.cache-to=type=gha,scope=api,mode=max
            worker.cache-from=type=gha,scope=worker
            worker.cache-to=type=gha,scope=worker,mode=max

      - name: Build da referência (api)
        uses: docker/build-push-action@v7.4.0
        with:
          context: .
          file: src/StatementImporter.Api/Dockerfile
          target: reference
          load: true
          tags: statement-importer-api:reference
          cache-from: |
            type=gha,scope=api
            type=gha,scope=api-reference
          cache-to: type=gha,scope=api-reference,mode=max

      - name: Build da referência (worker)
        uses: docker/build-push-action@v7.4.0
        with:
          context: .
          file: src/StatementImporter.Worker/Dockerfile
          target: reference
          load: true
          tags: statement-importer-worker:reference
          cache-from: |
            type=gha,scope=worker
            type=gha,scope=worker-reference
          cache-to: type=gha,scope=worker-reference,mode=max

      - run: docker compose pull postgres rabbitmq
      - run: dotnet run tools/generate-csv.cs -- --lines 1000000 --seed 42 --out data/1m.csv
      - run: bash proofs/run-all.sh

      - name: Trivy — relatório chiseled × padrão
        run: bash .github/scripts/trivy-report.sh

      - name: Trivy — gate (CRITICAL com correção)
        run: |
          for svc in api worker migrator; do
            docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v trivy-cache:/root/.cache "$TRIVY_IMAGE" \
              image --quiet --severity CRITICAL --ignore-unfixed --exit-code 1 "ghcr.io/yudikojima/statement-importer-$svc:local"
          done

      - name: Diagnóstico
        if: failure()
        run: |
          docker compose ps -a
          docker compose logs --no-color

      - name: Limpeza
        if: always()
        run: docker compose down -v

  publish:
    if: github.event_name == 'push' && github.ref == 'refs/heads/master'
    needs: [test, stack]
    runs-on: ubuntu-24.04
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v7.0.1
      - uses: docker/setup-buildx-action@v4.4.1
      - uses: docker/login-action@v4.6.0
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Tag sha-<curto>
        id: tag
        run: echo "tag=sha-${GITHUB_SHA::7}" >> "$GITHUB_OUTPUT"

      - name: Build e push (sha-<curto>)
        uses: docker/bake-action@v7.4.0
        env:
          TAG: ${{ steps.tag.outputs.tag }}
          VERSION: ${{ steps.tag.outputs.tag }}
          REVISION: ${{ github.sha }}
        with:
          source: .
          files: compose.yml
          targets: migrator,api,worker
          push: true
          set: |
            migrator.cache-from=type=gha,scope=migrator
            api.cache-from=type=gha,scope=api
            worker.cache-from=type=gha,scope=worker

      - name: Tag latest
        run: |
          for svc in api worker migrator; do
            image="ghcr.io/yudikojima/statement-importer-$svc"
            docker buildx imagetools create -t "$image:latest" "$image:${{ steps.tag.outputs.tag }}"
          done
```

O `latest` é criado no registro a partir da tag `sha-<curto>`, sem rebuild.

- [ ] **Step 4: Validar sintaxe localmente**

```bash
git add .github/ && git update-index --chmod=+x .github/scripts/trivy-report.sh
MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W 2>/dev/null || pwd):/repo" -w /repo rhysd/actionlint:1.7.7 -color
```

Esperado: nenhuma saída de erro. (O `actionlint` só valida o YAML; a execução real é conferida no PR, na Task 11.)

- [ ] **Checkpoint:** `actionlint` limpo e Step 1 sem warnings.

---

### Task 10: Documentação

**Files:**
- Create: `docs/architecture/decisions/0006-papeis-de-banco-com-privilegio-minimo.md`, `docs/architecture/decisions/0007-provas-executaveis-contra-o-stack-real.md`
- Modify: `docs/architecture/decisions/README.md`, `docs/architecture/system-design.md`, `README.md`, `CLAUDE.md`

- [ ] **Step 1: ADR 0006**

```markdown
# 0006 — Papéis de banco com privilégio mínimo

**Data:** 2026-10-25
**Status:** Aceito
**Fase:** 03-provas-e-ci

## Contexto

Na fase 01, `api`, `worker` e `migrator` conectavam com o `POSTGRES_USER`, que é superusuário. Uma falha de injeção ou um bug na `api` teria poder para apagar o banco inteiro, ler arquivos do servidor ou criar papéis.

## Decisão

Três papéis: o superusuário `postgres` só no bootstrap do container; `importer_owner`, dono do banco e do schema, usado pelo `migrator`; `importer_app`, só com `SELECT/INSERT/UPDATE/DELETE` e `TEMP`, usado por `api` e `worker`. Os papéis são criados por um script em `/docker-entrypoint-initdb.d`, e `ALTER DEFAULT PRIVILEGES` estende os grants às tabelas de migrations futuras.

## Alternativas consideradas

- **Dois papéis (migrator como superusuário)**: menos um secret, mas um container da aplicação continuaria com poder total.
- **Criar papéis numa migration do EF Core**: a senha acabaria no código ou exigiria superusuário no migrator.

## Consequências

- Nenhum container da aplicação conecta como superusuário; `security.sh` prova que `importer_app` não consegue `DROP TABLE`.
- Três secrets em vez de um; cada serviço monta o seu como `Db__Password`, sem mudança no código.
- O script de init só roda com `pgdata` vazio: mudar papéis depois exige `docker compose down -v` (ou um script de migração de papéis).
- Testes de integração continuam com o superusuário do Testcontainers; o privilégio mínimo é verificado pela prova.
```

- [ ] **Step 2: ADR 0007**

```markdown
# 0007 — Provas executáveis contra o stack real

**Data:** 2026-10-25
**Status:** Aceito
**Fase:** 03-provas-e-ci

## Contexto

As afirmações centrais do projeto são sobre containers, não sobre código: imagem pequena e sem root, porta não publicada, rede sem saída, dados que sobrevivem ao `down`, worker morto sem duplicar dados. Testes .NET com Testcontainers não sobem o `compose.yml` real e não enxergam essas propriedades.

## Decisão

Scripts bash em `proofs/` rodam contra o `compose.yml` real, no CI e no Git Bash, e falham quando a propriedade não vale. Medições (tamanhos, 1 × 3 workers, CVEs) vão para o job summary. Para mostrar o efeito das imagens chiseled, o CI builda um target `reference` (mesmo código sobre `aspnet:10.0`/`runtime:10.0`) e compara as CVEs; o gate só falha com CVE `CRITICAL` que tenha correção.

## Alternativas consideradas

- **Só testes .NET**: não verificam imagem, rede, volume nem restart.
- **Provas em C# (file-based app)**: um runtime a mais para orquestrar `docker`; em bash, cada prova é lida como uma sequência de comandos Docker.
- **Gate em HIGH ou sem `--ignore-unfixed`**: CVEs recém-publicadas sem patch travariam o CI sem ação possível.

## Consequências

- O README cita números que o CI mede a cada push.
- As provas dependem de `bash`, `curl`, `awk`, `sed` e `timeout` (presentes no runner e no Git Bash), sem `jq`.
- O job `stack` leva alguns minutos (três importações de 1M de linhas).
- O target `reference` precisa acompanhar mudanças no publish da `api` e do `worker`.
```

- [ ] **Step 3: Índice dos ADRs** — adicionar ao fim da tabela em `docs/architecture/decisions/README.md`:

```markdown
| [0006](0006-papeis-de-banco-com-privilegio-minimo.md) | Papéis de banco com privilégio mínimo: superusuário só no bootstrap |
| [0007](0007-provas-executaveis-contra-o-stack-real.md) | Provas executáveis em bash contra o stack real; Trivy comparativo |
```

- [ ] **Step 4: `system-design.md`**

1. Seção 2 — "Configuração e segredos": substituir os bullets do `Db__Password`, do `POSTGRES_PASSWORD_FILE` e do compose por:

```markdown
- Papéis do Postgres ([ADR 0006](decisions/0006-papeis-de-banco-com-privilegio-minimo.md)): `postgres` (superusuário, só bootstrap), `importer_owner` (dono do schema, usado pelo `migrator`) e `importer_app` (DML e `TEMP`, usado por `api` e `worker`), criados por `docker/postgres/init-roles.sh` no primeiro boot.
- Três secrets: `secrets/postgres_password.txt`, `secrets/db_owner_password.txt` e `secrets/db_app_password.txt` (no `.gitignore`; os `.example` são commitados). Cada serviço monta o seu com `target: Db__Password`, que vira a chave `Db:Password`; `Db__Username` define o papel. O CI gera valores aleatórios.
```

2. "Fora do escopo": remover `Usuário de banco com privilégio mínimo (hoje as aplicações usam o \`POSTGRES_USER\`, que é superusuário; candidato à fase 03), `.

3. Seção 1, árvore do repositório: trocar `secrets/db_password.txt.example` por `secrets/*.txt.example` e acrescentar `docker/postgres/init-roles.sh` e `.github/scripts/trivy-report.sh`.

4. Seção 4 — CI: trocar o parágrafo de publicação por:

```markdown
Somente em push na `master`: publica `ghcr.io/yudikojima/statement-importer-{api,worker,migrator}` com tags `sha-<curto>` e `latest` (a `web` entra na fase 02). O gate do Trivy falha só com CVE `CRITICAL` com correção disponível ([ADR 0007](decisions/0007-provas-executaveis-contra-o-stack-real.md)).
```

5. Seção 4 — tabela de provas, linha do `network.sh`: acrescentar "Até a fase 02, a `api` é a exceção que publica a 8080." e, na linha do `security.sh`, "; `importer_app` não é superusuário e não consegue `DROP TABLE`".

- [ ] **Step 5: `README.md`**

1. Logo abaixo do título, antes da nota de README provisório:

```markdown
[![ci](https://github.com/YudiKojima/statement-importer/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/YudiKojima/statement-importer/actions/workflows/ci.yml)
```

2. Na nota do topo, trocar "O README completo, com demo e badge do CI, chega na fase 04." por "O README completo, com demo, chega na fase 04."

3. Em "Como rodar", trocar a linha do `cp` por:

```bash
for s in postgres_password db_owner_password db_app_password; do cp secrets/$s.txt.example secrets/$s.txt; done
```

4. Trocar o parágrafo "Por enquanto, essas medições foram feitas à mão. Na fase 03 ..." por:

````markdown
Essas propriedades são verificadas por provas executáveis em [`proofs/`](proofs/), a cada push, no [CI](.github/workflows/ci.yml). O job summary de cada execução traz os tamanhos, os tempos de 1 × 3 workers e a comparação de CVEs entre as imagens chiseled e as imagens padrão.

## Como rodar as provas

Com o stack em pé (seção anterior):

```bash
bash proofs/run-all.sh
```

Leva alguns minutos: inclui três importações de 1M de linhas. Cada prova também roda sozinha, por exemplo `bash proofs/resilience.sh`.
````

5. Em "O que existe hoje", trocar o título para "O que existe hoje (fases 01 e 03)" e acrescentar os bullets:

```markdown
- Papéis de banco com privilégio mínimo: `api` e `worker` só leem e gravam dados; o `migrator` é dono do schema; ninguém usa o superusuário.
- Provas executáveis em `proofs/` e CI no GitHub Actions com Trivy e publicação no GHCR.
```

- [ ] **Step 6: `CLAUDE.md`** — no passo 5 do "Fluxo padrão de uma nova fase", acrescentar `+ bash proofs/run-all.sh` depois de `docker compose up -d --wait`.

- [ ] **Checkpoint:** links relativos dos arquivos novos conferidos (`ls` de cada caminho citado).

---

### Task 11: Verificação e entrega

- [ ] **Step 1:** `/verification-before-completion`:

```bash
dotnet build StatementImporter.slnx -warnaserror
dotnet test --project tests/StatementImporter.UnitTests
dotnet test --project tests/StatementImporter.IntegrationTests
docker compose build
docker compose down -v && docker compose up -d --wait
bash proofs/run-all.sh
```

Esperado: tudo verde; 39 unitários e 21 de integração.

- [ ] **Step 2:** `/requesting-code-review`.

- [ ] **Step 3:** `/finishing-a-development-branch`: em `docs/project-overview.md`, marcar a fase 03 como `✅ Concluída (AAAA-MM-DD)` no mesmo commit final; commit e push só com o OK do usuário; abrir o PR.

- [ ] **Step 4:** `gh pr checks <n>`: `test` e `stack` verdes. Abrir o job summary do `stack` e conferir as três tabelas (tamanhos, escala, CVEs). PR com CI ❌ não é mergeado.

- [ ] **Step 5:** Depois do merge (feito pelo usuário): conferir o run do push na `master` com `publish` verde e as tags no GHCR:

```bash
gh run list --branch master --limit 1
gh api /users/YudiKojima/packages/container/statement-importer-api/versions --jq '.[0].metadata.container.tags'
```

Esperado: `["sha-<curto>","latest"]`. Se o `gh api` responder 403, falta o escopo: `gh auth refresh -s read:packages`. Avisar o usuário de que os pacotes nascem privados no GHCR (tornar públicos é configuração dele) e de que pode exigir os checks `test` e `stack` na proteção da `master`.
