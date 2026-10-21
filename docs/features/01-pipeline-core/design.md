# Design — Fase 01: núcleo do pipeline

**Data:** 2026-10-18
**Escopo:** API, worker, banco, fila e migrator rodando no compose, com testes. Sem frontend, sem reverse proxy, sem provas executáveis e sem CI (fases 02 e 03).

O desenho completo está em [`../../architecture/system-design.md`](../../architecture/system-design.md). Este documento só recorta o que entra nesta fase.

---

## Entra nesta fase

| Item | Referência no system design |
|---|---|
| Solution `StatementImporter.slnx`, `Directory.Build.props`, `Directory.Packages.props` (versões centralizadas) | Seção 1 — Estrutura |
| `Contracts`: mensagens `ImportRequested` e `ProcessChunk`, DTOs de resposta, formato e validação de linha do CSV, montagem da connection string | Seções 1 e 2 |
| `Data`: `DbContext`, entidades das 4 tabelas, migration inicial | Seção 2 — Modelo de dados |
| `Api`: todos os endpoints, `/health`, `/api/docs`, `--healthcheck`, Native AOT | Seção 2 — Endpoints; Seção 3 |
| `Worker`: consumidores de `ImportRequested` e `ProcessChunk`, heartbeat, `--healthcheck` | Seção 2 — Passo a passo |
| Dockerfiles de `api` e `worker` com os estágios `restore → build → dev → publish → runtime`; `migrator` com `restore → publish → runtime` | Seção 3 |
| `compose.yml` com `postgres`, `rabbitmq`, `migrator`, `api`, `worker`, volumes `pgdata` e `uploads`, secret `Db__Password`, limites do worker | Seção 3 — Compose |
| `compose.dev.yml` com target `dev`, Compose Watch e `dotnet watch` | Seção 3 |
| `UnitTests` e `IntegrationTests` (Testcontainers) | Seção 4 — Testes .NET |
| `tools/generate-csv.cs` | Seção 4 — Gerador de dados |
| `.gitignore`, `.dockerignore`, `secrets/db_password.txt.example` | Seções 1 e 3 |

## Diferença temporária em relação ao design final

Sem `proxy` nesta fase, a `api` publica a porta **8080** diretamente no `compose.yml` e fica nas redes `public` e `internal`. A fase 02 remove essa porta e coloca a `api` só na rede `internal`.

## Verificações já feitas antes da fase (2026-10-18)

Feitas num spike descartável, fora do repositório:

| Verificação | Resultado |
|---|---|
| `api` em AOT com Npgsql, Dapper.AOT, RabbitMQ.Client 7, OpenApi, Scalar, KeyPerFile | zero warnings; 17 MB comprimida; Dapper e RabbitMQ funcionando contra serviços reais |
| `HEALTHCHECK` exec com `--healthcheck` em imagem chiseled | container `healthy` |
| `KeyPerFile` lendo `/run/secrets/Db__Password` | conexão com o Postgres autenticada |
| `docker compose up --wait` com serviço one-shot + `service_completed_successfully` | sai com 0; one-shot `Exited (0)` |
| `deploy.resources.limits` no compose (sem swarm), inclusive com `--scale` | `NanoCpus` e `Memory` aplicados nas 3 réplicas |
| Rede `internal: true` | sem saída para a internet |
| Volume nomeado + `COPY --chown=1654:1654` na imagem | volume criado com dono 1654 (sem o `--chown`, dono 0) |
| `/tmp` nas imagens chiseled | `drwxrwxrwt`, gravável |
| `docker kill` com `restart: unless-stopped` | **não** reinicia (parada manual) |

Em 2026-10-19/20, todo o código do [plano](plan.md) foi escrito e executado numa solution de rascunho: build sem avisos, 39 testes unitários e 15 de integração verdes, `compose up --wait` saudável, 1M de linhas (upload `202` em 0,22 s; 1 worker ~38 s; 3 workers com um morto no meio ~15 s, contagem exata), recuperação de `api` e `worker` com o RabbitMQ parado, reconciliador recuperando importação órfã e hot reload via Compose Watch. Tamanhos comprimidos medidos: `api` 17 MB, `worker` 45 MB, `migrator` 50 MB.

## Critérios de aceite

1. `docker compose up -d --wait` termina com `api`, `worker`, `postgres` e `rabbitmq` *healthy* e `migrator` concluído com código 0.
2. `dotnet run tools/generate-csv.cs -- --lines 1000000 --error-rate 0.01 --seed 42` + `POST /api/imports` responde `202` em menos de 1 s depois do fim do upload.
3. A importação termina com `valid_count + error_count = total_lines` e `count(transactions)` igual a `valid_count`.
4. Reenviar o mesmo arquivo responde `200` com o mesmo `id`.
5. `docker compose up -d --scale worker=3` + `docker kill` de um worker durante a importação: o resultado continua satisfazendo o critério 3.
6. Tamanho comprimido (`docker save | gzip -c | wc -c`): `api` < 25 MB, `worker` < 70 MB, `migrator` < 60 MB; `docker image inspect` mostra usuário não-root em todas.
7. Parar o RabbitMQ com o stack de pé: o worker encerra, o restart policy o religa, e ele volta a consumir quando o RabbitMQ sobe de novo.
8. Reconciliador: importação `queued` sem mensagem na fila é republicada em até ~2 min.
9. `dotnet test` verde, incluindo os 4 cenários de integração da Seção 4.
10. `dotnet publish` da `api` sem warnings de AOT/trimming.
