# Design — Fase 03: provas executáveis e CI

**Data:** 2026-10-25
**Escopo:** provas em `proofs/` rodando contra o stack real, workflow do GitHub Actions (testes, provas, Trivy, publicação no GHCR) e usuários de banco com privilégio mínimo. Sem frontend e sem reverse proxy (fase 02).

O desenho completo está em [`../../architecture/system-design.md`](../../architecture/system-design.md), Seção 4. Este documento só recorta o que entra nesta fase e registra as decisões do brainstorming.

---

## Por que a fase 03 vem antes da 02

- A prova automatizada é o diferencial do portfólio: as medições da fase 01 foram feitas à mão e passam a ser verificadas a cada push.
- Com o CI em pé, a fase 02 (frontend e proxy) já nasce protegida por testes, provas e gate de CVEs.

A numeração das fases não muda; só a ordem de execução (ver [`../../project-overview.md`](../../project-overview.md)).

## Entra nesta fase

| Item | Referência no system design |
|---|---|
| Papéis `importer_owner` e `importer_app`, script de init do Postgres, três secrets | Seção 2 — Configuração e segredos; "Fora do escopo" |
| `proofs/` com `lib.sh`, `run-all.sh` e as provas `healthy`, `network` (reduzida), `security`, `image-size`, `volume`, `scale`, `resilience` | Seção 4 — Provas executáveis |
| Target `reference` nos Dockerfiles de `api` e `worker` | Seção 4 — CI, item 4 |
| `.github/workflows/ci.yml` com jobs `test`, `stack` e `publish` | Seção 4 — CI |
| README provisório atualizado: três secrets, como rodar as provas, badge do CI | Seção 4 — README |
| ADRs 0006 e 0007 | — |

## Decisão 1 — Usuários de banco com privilégio mínimo

| Papel | Quem usa | Privilégios |
|---|---|---|
| `postgres` (superusuário) | entrypoint do Postgres, `pg_isready` | todos |
| `importer_owner` | `migrator` | dono do banco `statement_importer` e do schema `public`; não é superusuário |
| `importer_app` | `api`, `worker` | `CONNECT` e `TEMP` no banco, `USAGE` no schema; `SELECT/INSERT/UPDATE/DELETE` nas tabelas e `USAGE/SELECT` nas sequences via `ALTER DEFAULT PRIVILEGES FOR ROLE importer_owner` |

- `docker/postgres/init-roles.sh`, montado em `/docker-entrypoint-initdb.d`, cria os dois papéis com as senhas lidas de `/run/secrets` e transfere a posse do banco e do schema `public` para `importer_owner`. Só roda com `pgdata` vazio.
- `TEMP` é explícito porque o worker faz `COPY` binário para tabela temporária.
- `ALTER DEFAULT PRIVILEGES` faz as tabelas criadas por migrations futuras já nascerem acessíveis ao `importer_app`, sem `GRANT` manual.
- Secrets: `secrets/postgres_password.txt`, `secrets/db_owner_password.txt`, `secrets/db_app_password.txt`, cada um com `.example`. O antigo `db_password.txt` deixa de existir.
- No compose, cada serviço monta o seu secret com `target: Db__Password` e define `Db__Username`. O código C# (`KeyPerFile` + `ServiceConfiguration`) não muda.
- O Postgres recebe `POSTGRES_USER=postgres` e `POSTGRES_PASSWORD_FILE` apontando para `postgres_password`; o healthcheck passa a usar `-U postgres`.
- Os testes de integração continuam com o superusuário do Testcontainers. O privilégio mínimo é provado pelo `security.sh` contra o stack real.
- Quem já tem o stack em pé precisa de um `docker compose down -v` uma vez, porque o init não roda sobre um `pgdata` existente.

ADR: `0006-papeis-de-banco-com-privilegio-minimo.md`.

## Decisão 2 — Provas executáveis

Bash, rodando no CI (Linux) e no Git Bash (Windows, com `MSYS_NO_PATHCONV=1`). `proofs/lib.sh` concentra `fail`, `pass`, upload, espera por status terminal e escrita no `$GITHUB_STEP_SUMMARY` quando a variável existe. Cada importação usa um `--prefix` próprio no gerador, para não colidir por hash nem por `transaction_id`. `run-all.sh` executa na ordem abaixo e para na primeira falha.

| Prova | Como verifica | Falha se |
|---|---|---|
| `healthy.sh` | `docker compose up -d --wait --wait-timeout 60`; `migrator` em `Exited (0)` | algum serviço não ficar `healthy` em 60 s |
| `network.sh` (reduzida) | portas publicadas no `docker compose ps`; conexão em `localhost:5432/5672/15672`; `docker network inspect` com `Internal=true`; `busybox:1.37` na rede `internal` tenta `wget` externo | algo além de `api:8080` publicar porta, Postgres ou RabbitMQ responderem do host, ou a rede `internal` ter saída |
| `security.sh` | `Config.User` de `api`, `worker`, `migrator`; `docker run --entrypoint /bin/sh` em `api` e `worker`; `psql -h postgres` como `importer_app` (o localhost é `trust` no pg_hba da imagem) via `docker compose exec postgres` | algum container rodar como root, existir shell, `importer_app` ser superusuário ou conseguir `DROP TABLE transactions` |
| `image-size.sh` | `docker save \| gzip -c \| wc -c`, tamanhos no summary | `api` ≥ 25 MB, `worker` ≥ 70 MB, `migrator` ≥ 60 MB |
| `volume.sh` | importa 10 mil linhas → `compose down` (sem `-v`) → `up --wait` → `GET` da importação e `count(*)` | contadores ou linhas diferentes depois do restart |
| `scale.sh` | 1M de linhas com 1 worker, depois 1M com 3 workers; tabela de tempos no summary | só se a importação não terminar (tempo não é critério) |
| `resilience.sh` | 3 workers, 1M de linhas; `docker kill` num worker quando o primeiro bloco fica `done` | `count(*)` da importação diferente de `validCount`, ou status diferente de `completed_with_errors` |

**`network.sh` reduzida:** sem `proxy`, a `api` continua publicando a 8080 e é a única exceção permitida. Na fase 02 a exceção passa a ser `proxy:8080`, e a prova ganha a verificação de que a `api` não responde do host.

O CSV de 1M de linhas é gerado uma vez por execução em `data/`.

## Decisão 3 — Trivy comparativo

- Target `reference` em cada Dockerfile: `api` publicada sem AOT sobre `aspnet:10.0`; `worker` sobre `runtime:10.0`. Buildado só no CI, nunca publicado, fora do `compose.yml`.
- Relatório: Trivy em JSON nas quatro imagens (`api`, `worker` e as duas `reference`), convertido em tabela de CVEs por severidade, chiseled × padrão, no job summary.
- Gate: `--severity CRITICAL --ignore-unfixed --exit-code 1` em `api`, `worker` e `migrator`. Só CVE com correção disponível trava o CI.

## Decisão 4 — Estrutura do CI

Gatilhos: `pull_request` e `push` na `master`; `concurrency` cancela a execução anterior do mesmo ref. Permissão padrão `contents: read`.

| Job | Roda | Passos |
|---|---|---|
| `test` | sempre | `setup-dotnet` (pelo `global.json`) → `dotnet build StatementImporter.slnx -warnaserror` → testes unitários → testes de integração |
| `stack` | sempre, em paralelo com `test` | secrets com `openssl rand -hex 24` → build com buildx e cache `type=gha` (imagens do compose + `reference`) → CSV de 1M → `proofs/run-all.sh` → Trivy (relatório + gate) → `compose logs`/`ps` se falhar → `down -v` sempre |
| `publish` | push na `master`, depois de `test` e `stack` | `packages: write`, login no GHCR com `GITHUB_TOKEN`, rebuild a partir do cache, push de `ghcr.io/yudikojima/statement-importer-{api,worker,migrator}` com `sha-<curto>` e `latest`, `VERSION`/`REVISION` nos labels OCI |

- Actions de terceiros e imagens auxiliares (`busybox`, Trivy) com versão fixa.
- A imagem `web` entra no `publish` na fase 02.
- Proteger a `master` exigindo `test` e `stack` é configuração do repositório no GitHub, feita pelo usuário depois do merge.

ADR: `0007-provas-executaveis-contra-o-stack-real.md` (provas em bash contra o compose real, além dos testes .NET, e comparação com o target `reference`).

## Critérios de aceite

1. `docker compose up -d --wait` com os três secrets sobe tudo `healthy`; `api` e `worker` conectam como `importer_app`, `migrator` como `importer_owner`.
2. `proofs/run-all.sh` passa localmente no Git Bash e no CI.
3. `security.sh` falha se um container de app rodar como root ou se `importer_app` puder fazer `DROP TABLE` (verificado alterando temporariamente o cenário).
4. O workflow roda num PR com `test` e `stack` verdes, e o job summary mostra tamanhos, tempos de 1 × 3 workers e a tabela de CVEs chiseled × padrão.
5. Depois do merge na `master`, as três imagens aparecem no GHCR com `sha-<curto>` e `latest`.
6. README com os três secrets, como rodar as provas e o badge do CI apontando para o workflow real.
7. `docs/architecture/system-design.md` atualizado: privilégio mínimo sai de "Fora do escopo", secrets e papéis na Seção 2, ordem das fases.
8. ADRs 0006 e 0007 criados e listados no índice.
