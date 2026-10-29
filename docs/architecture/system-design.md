# statement-importer — System Design

**Data:** 2026-10-18
**Status:** Proposto

> Visão completa do sistema, escrita antes da implementação. A entrega é dividida em fases (ver [`../project-overview.md`](../project-overview.md)); cada fase tem seu `design.md` com o recorte de escopo e critérios de aceite.

---

## Visão Geral

Pipeline de **importação assíncrona de extratos bancários (CSV)**. O usuário envia um arquivo de até 200 MB / ~1 milhão de linhas; a API responde em milissegundos e o processamento acontece em workers escaláveis, com progresso visível, relatório de linhas rejeitadas e garantia de que nenhuma transação é gravada duas vezes — mesmo se um worker morrer no meio.

**Objetivo do projeto:** portfólio de Docker. Cada conceito (imagem, build, container, volume, network, compose) aparece por uma necessidade real do sistema e é **verificado por uma prova executável** rodando no CI.

**Stack:** .NET 10 (C#), Blazor WebAssembly, PostgreSQL 17, RabbitMQ 4, Nginx.

### A dor resolvida

Importar o arquivo dentro da requisição HTTP causa: timeout em arquivos grandes, uma linha ruim derrubando a importação inteira, duplicação quando o usuário reenvia, API lenta para todos durante a importação e nenhuma visibilidade de progresso. A arquitetura abaixo resolve cada um desses pontos.

---

## Seção 1 — Arquitetura

### Serviços

| Serviço | Imagem final | Redes | Porta publicada |
|---|---|---|---|
| `proxy` | `nginxinc/nginx-unprivileged:1.29-alpine` + estáticos do Blazor | `public`, `internal` | **8080** (única) |
| `api` | `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled` + binário Native AOT | `internal` | — |
| `worker` | `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled` | `internal` | — |
| `migrator` | `runtime-deps:10.0-noble-chiseled` + EF Core migration bundle self-contained | `internal` | — |
| `postgres` | `postgres:17-alpine` + volume nomeado `pgdata` | `internal` | — |
| `rabbitmq` | `rabbitmq:4-management-alpine` | `internal` | — |

Volume nomeado `uploads` montado em `/data/uploads` na `api` (escrita) e no `worker` (leitura).

### Ordem de subida

`postgres` e `rabbitmq` *healthy* → `migrator` termina com sucesso (`depends_on: condition: service_completed_successfully`) → `api` e `worker` → `proxy` (depende de `api` *healthy*).

### Estrutura do repositório

```
statement-importer/
├─ src/
│  ├─ StatementImporter.Api/          Minimal API, Native AOT, Dapper.AOT, publica na fila
│  ├─ StatementImporter.Worker/       BackgroundService, EF Core, consome a fila
│  ├─ StatementImporter.Contracts/    mensagens da fila, DTOs de resposta, formato do CSV, config compartilhada
│  ├─ StatementImporter.Data/         DbContext + migrations do EF Core (usado por worker e migrator)
│  └─ StatementImporter.Web/          Blazor WebAssembly
├─ tests/
│  ├─ StatementImporter.UnitTests/
│  └─ StatementImporter.IntegrationTests/   Testcontainers (Postgres + RabbitMQ)
├─ tools/generate-csv.cs              file-based app .NET 10 para gerar arquivos de demo
├─ proofs/                            provas executáveis (bash)
├─ docker/nginx.conf
├─ docker/postgres/init-roles.sh      cria os papéis do Postgres no primeiro boot
├─ .github/scripts/trivy-report.sh    contagem de CVEs para o job summary
├─ secrets/*.txt.example
├─ compose.yml                        stack de produção
├─ compose.dev.yml                    desenvolvimento (Compose Watch + dotnet watch, portas de debug)
├─ Directory.Build.props / Directory.Packages.props
├─ StatementImporter.slnx
└─ .github/workflows/ci.yml
```

`StatementImporter.Contracts` não referencia EF Core nem nada que quebre AOT — é referenciado pela `api`.

---

## Seção 2 — Fluxo de dados

### Formato do CSV

UTF-8 (BOM aceito e ignorado), fim de linha `LF` ou `CRLF`, separador vírgula, primeira linha é o cabeçalho exato:

```
transaction_id,date,description,amount
TX-000001,2026-03-15,PIX RECEBIDO JOAO,1500.00
TX-000002,2026-03-15,COMPRA MERCADO,-234.90
```

Formato **restrito e documentado**: campos não contêm vírgula, aspas ou quebra de linha (isso permite dividir o arquivo por offset de linha sem parser de estado). Linha que viola o formato é rejeitada, não derruba a importação.

**Regras de validação por linha** (motivo gravado em `import_errors.reason`):

| Regra | Motivo |
|---|---|
| exatamente 4 colunas | `column_count` |
| `transaction_id` não vazio, até 64 caracteres | `invalid_transaction_id` |
| `date` no formato `yyyy-MM-dd`, data válida | `invalid_date` |
| `description` não vazia, até 200 caracteres | `invalid_description` |
| `amount` decimal com ponto, até 2 casas, `CultureInfo.InvariantCulture` | `invalid_amount` |
| `transaction_id` já existente no banco (de qualquer importação) | `duplicate` |

Linhas em branco são ignoradas e não contam como erro. **`total_lines` = linhas de dados não vazias, sem o cabeçalho**, o que garante o invariante `valid_count + error_count = total_lines` ao fim da importação.

### Modelo de dados (schema do EF Core)

| Tabela | Colunas principais | Restrições |
|---|---|---|
| `imports` | `id uuid`, `file_name`, `file_hash char(64)`, `file_path`, `status`, `total_lines`, `total_chunks`, `processed_chunks`, `processed_lines`, `valid_count`, `error_count`, `failure_reason`, `created_at`, `started_at`, `completed_at` | `unique(file_hash)` |
| `import_chunks` | `id`, `import_id`, `chunk_index`, `start_offset bigint`, `first_line_number`, `line_count`, `status`, `attempts`, `processed_at` | `unique(import_id, chunk_index)` |
| `transactions` | `id bigserial`, `transaction_id`, `date`, `description`, `amount numeric(18,2)`, `import_id`, `line_number` | `unique(transaction_id)` |
| `import_errors` | `id bigserial`, `import_id`, `line_number`, `raw_line` (truncada em 500), `reason` | índice `(import_id, line_number)` |

**Status da importação:** `queued` → `processing` → `completed` | `completed_with_errors` | `failed`.
**Status do bloco:** `pending` → `done` | `failed`.

### Passo a passo

1. **Upload — `POST /api/imports?fileName=extrato.csv`** (corpo = arquivo bruto, `Content-Type: text/csv`)
   - Limite de corpo de **200 MB** configurado no Kestrel (`MaxRequestBodySize`, cujo padrão é 30 MB) e no Nginx (`client_max_body_size`, padrão 1 MB, e `proxy_request_buffering off` para o upload seguir em streaming até a API, sem o Nginx bufferizar o arquivo inteiro em disco).
   - A API lê a primeira linha do stream; cabeçalho diferente do esperado → `400` imediato.
   - Copia o restante do stream para `/data/uploads/{importId}.csv` calculando SHA-256 incrementalmente (nunca carrega o arquivo em memória).
   - `INSERT ... ON CONFLICT (file_hash) DO NOTHING RETURNING id`. Se o hash já existe: apaga o arquivo recém-gravado e responde `200` com a importação existente.
   - Senão: publica `ImportRequested { ImportId }` e responde **`202 Accepted`** com `{ id, status: "queued" }` e `Location: /api/imports/{id}`.
   - Se a API morrer entre o `INSERT` e a publicação, a importação ficaria `queued` para sempre. O **reconciliador** (seção abaixo) cobre esse caso.

2. **Divisão — worker consome `ImportRequested`**
   - Percorre o arquivo registrando o offset em bytes do início de cada bloco de **10.000 linhas não vazias**.
   - Arquivo só com o cabeçalho (`total_lines = 0`) → `status = completed` direto, sem blocos.
   - Numa transação: insere todas as linhas de `import_chunks` (`pending`), grava `total_lines`, `total_chunks`, `status = processing`, `started_at`.
   - Após o commit, publica um `ProcessChunk { ImportId, ChunkIndex }` por bloco `pending` e dá ack.
   - Reentrega de `ImportRequested` com status já `processing` → só republica `ProcessChunk` dos blocos `pending` (mensagens duplicadas são inofensivas, ver passo 3).
   - Arquivo ausente ou ilegível → `status = failed`, `failure_reason` preenchido, ack.

3. **Processamento — qualquer worker consome `ProcessChunk`** (prefetch = 1)
   - Se a mensagem chegou com a flag `redelivered` do AMQP (entregue antes e não confirmada, por queda do worker ou `nack`), incrementa `attempts` do bloco. Se `attempts > 5` → bloco `failed`, importação `failed` com motivo, ack. Mensagens **novas** duplicadas (republicadas pelo reconciliador ou pela reentrega do passo 2) não contam como tentativa. Se contassem, um backlog longo faria importações saudáveis falharem.
   - Numa segunda transação:
     1. `SELECT ... FOR UPDATE` no bloco. Se já `done`, ou se a importação já está `failed` → commit e ack (reentrega ou bloco órfão ignorado).
     2. Lê as linhas a partir de `start_offset`, valida cada uma.
     3. Linhas válidas → `COPY` binário para tabela temporária, depois `INSERT INTO transactions SELECT ... ORDER BY transaction_id ON CONFLICT (transaction_id) DO NOTHING RETURNING line_number`. As válidas que não voltaram no `RETURNING` viram erro `duplicate`. O `ORDER BY` faz todos os workers travarem as chaves na mesma ordem: sem ele, dois blocos com `transaction_id` repetidos entre si podem entrar em deadlock. Se um deadlock ainda ocorrer, o Postgres aborta uma das transações, e ela cai no `nack` e é reprocessada.
     4. Insere os erros em `import_errors`. O teto de 10.000 erros detalhados por importação é **aproximado**: é checado contra `error_count` lido no início do bloco, então blocos concorrentes podem ultrapassá-lo um pouco. `error_count` sempre conta todos.
     5. Bloco → `done`; `UPDATE imports SET processed_chunks = processed_chunks + 1, processed_lines = ..., valid_count = ..., error_count = ... RETURNING processed_chunks, total_chunks`.
     6. Se `processed_chunks = total_chunks` → `status = completed` (ou `completed_with_errors` se `error_count > 0`), `completed_at`.
   - **Ack só depois do commit.** Exceção → `BasicNack(requeue: true)`.

4. **Progresso — `GET /api/imports/{id}`** lê os contadores de `imports`. O Blazor consulta a cada 1,5 s enquanto o status for `queued` ou `processing`.

### Por que um worker morto não duplica dados

Se o container morre no meio do passo 3, a transação não foi commitada (nada gravado) e o ack não foi enviado. O RabbitMQ reentrega `ProcessChunk` a outro worker, que processa o bloco do zero. Se o bloco já estiver `done`, a reentrega é descartada pelo `FOR UPDATE` + checagem de status. Se dois workers pegarem o mesmo bloco, o `FOR UPDATE` serializa e o segundo encontra `done`. Detalhes em [ADR 0003](decisions/0003-processamento-em-blocos-idempotente.md).

### Endpoints

| Método | Rota | Resposta |
|---|---|---|
| `POST` | `/api/imports?fileName=` | `202` nova importação · `200` arquivo já importado · `400` cabeçalho inválido · `413` acima de 200 MB |
| `GET` | `/api/imports` | 20 importações mais recentes |
| `GET` | `/api/imports/{id}` | status e contadores · `404` |
| `GET` | `/api/imports/{id}/errors?page=&pageSize=` | linhas rejeitadas paginadas (`pageSize` máx. 100) |
| `GET` | `/api/transactions?importId=&page=&pageSize=` | transações importadas paginadas |
| `GET` | `/health` | `200` se o processo responde e o banco aceita conexão |
| `GET` | `/api/docs` | documentação interativa (Scalar sobre o OpenAPI em `/api/openapi/v1.json`) |

JSON serializado com `System.Text.Json` source-generated (`JsonSerializerContext`), requisito do AOT. O gerador de OpenAPI também pede metadados dos parâmetros de query, por isso o `ContractsJsonContext` inclui `Guid?` e `int?`; sem eles, o documento devolvia 500 no Native AOT.

### Fila

RabbitMQ com **quorum queues** duráveis `import-requested` e `process-chunk`, mensagens persistentes. Prefetch 1 por consumidor no worker — é o que faz `--scale worker=N` distribuir blocos de forma igual.

No RabbitMQ 4, quorum queues têm `delivery-limit` padrão de 20: depois disso, a mensagem é descartada. O limite de 5 tentativas de `attempts` (passo 3) age antes e marca a falha de forma visível. Não configurar `delivery-limit` abaixo de 6.

A conexão com o RabbitMQ é criada **sem recuperação automática**. Se ela cair, `api` e `worker` **encerram o processo** com código 1, e o `restart: unless-stopped` os religa. Se o RabbitMQ ainda estiver fora, a conexão inicial falha e o processo sai com código 1 de novo, até o broker voltar. Isso é necessário porque o Docker não reinicia container `unhealthy`: o healthcheck só informa, e quem reinicia é a política de restart quando o processo sai. O `/health` da `api` também reporta `503` se a conexão com a fila estiver fechada.

Se a publicação de `ImportRequested` falhar no upload (fila caindo naquele instante), a importação já está gravada como `queued`. A `api` responde `202` mesmo assim, e o reconciliador publica a mensagem depois.

Os dois processos também observam os **canais**, não só a conexão: a `api` encerra se o canal de publicação fechar, e o worker encerra se algum canal (consumidores ou publicação) fechar.

Uma falha ao processar mensagem espera `NackDelay` (1 s) antes do `nack`, para não girar a mensagem em milissegundos. Mesmo assim, se o banco ficar fora tempo suficiente, a mensagem pode passar das 20 entregas do `delivery-limit` e ser descartada pelo RabbitMQ sem que `attempts` suba, porque registrar a tentativa exige o banco. O reconciliador cobre esse caso.

### Reconciliador

Roda em cada worker a cada `ReconcileInterval` (60 s). Para não amplificar mensagens quando há backlog, ele **só age sobre uma fila vazia**:

- Fila `import-requested` vazia → republica `ImportRequested` das importações `queued` há mais de `QueuedThreshold` (60 s). Cobre a API caindo entre gravar e publicar.
- Fila `process-chunk` vazia → republica `ProcessChunk` dos blocos `pending` de importações `processing` iniciadas há mais de `StalledThreshold` (2 min) e sem nenhum bloco concluído nesse intervalo. Cobre mensagens perdidas: descartadas pelo `delivery-limit`, ou publicação interrompida depois do commit da divisão.

Duplicatas são inofensivas (`FOR UPDATE` + bloco `done`) e não consomem tentativas. Verificado: com a fila `process-chunk` esvaziada à força no meio de uma importação de 1M de linhas, o reconciliador republicou os 81 blocos pendentes e a importação terminou com a contagem exata.

### Configuração e segredos

- Arquivos de `/run/secrets` lidos pelo provider `KeyPerFile` do .NET. O secret `Db__Password` vira a chave `Db:Password`.
- A conexão é montada com `NpgsqlConnectionStringBuilder` a partir de `Db:Host`, `Db:Database`, `Db:Username` (variáveis de ambiente) + `Db:Password` (secret).
- Papéis do Postgres ([ADR 0006](decisions/0006-papeis-de-banco-com-privilegio-minimo.md)): `postgres` (superusuário, só bootstrap), `importer_owner` (dono do schema, usado pelo `migrator`) e `importer_app` (DML e `TEMP`, usado por `api` e `worker`), criados por `docker/postgres/init-roles.sh` no primeiro boot.
- Três secrets: `secrets/postgres_password.txt`, `secrets/db_owner_password.txt` e `secrets/db_app_password.txt` (no `.gitignore`; os `.example` são commitados). Cada serviço monta o seu com `target: Db__Password`, que vira a chave `Db:Password`; `Db__Username` define o papel. O CI gera valores aleatórios.
- RabbitMQ usa credenciais de desenvolvimento por variáveis de ambiente (não expostas fora da rede interna).
- `KeyPerFile` já vem no shared framework do ASP.NET (a `api` não referencia o pacote); o `worker`, que não é web, referencia `Microsoft.Extensions.Configuration.KeyPerFile`.

### Globalização e horário

Imagens chiseled não têm ICU nem `tzdata`. `api`, `worker` e `migrator` usam `InvariantGlobalization=true`; parsing com `CultureInfo.InvariantCulture`; datas da transação como `DateOnly`; timestamps sempre em UTC (`timestamptz`).

### Fora do escopo

Autenticação, multiusuário, retenção/limpeza de arquivos em `uploads`, reprocessar uma importação `failed` (reenviar o mesmo arquivo devolve a importação que falhou), outros formatos (OFX, Excel), deploy em nuvem, Kubernetes, dashboards de métricas.

---

## Seção 3 — Imagens e Dockerfiles

Um `Dockerfile` por serviço em `src/<Projeto>/Dockerfile`, contexto de build na raiz do repositório, `.dockerignore` excluindo `bin/`, `obj/`, `.git/`, `secrets/`, `.env`, `docs/`.

### Estágios comuns

```
restore  → COPY só *.csproj, Directory.Build.props, Directory.Packages.props → dotnet restore
build    → COPY do código → dotnet build
dev      → imagem SDK + dotnet watch            (target do compose.dev.yml)
publish  → dotnet publish
runtime  → imagem final mínima                  (target do compose.yml)
```

Mudar código não invalida a camada de `restore`. O `migrator` e o `proxy` não têm estágio `dev`; o `proxy` tem Dockerfile próprio (publish do WASM → nginx).

**Desenvolvimento:** `docker compose -f compose.yml -f compose.dev.yml watch`. O arquivo se chama `compose.dev.yml`, e não `compose.override.yml`, porque o override é carregado automaticamente por qualquer `docker compose up`: o CI e as provas acabariam rodando as imagens de dev. O Compose Watch sincroniza `src/` para dentro do container (sem bind mount, então `bin/` e `obj/` do Windows não se misturam com os do Linux), e o `dotnet watch` recompila. Medido: ~9 s da edição até a resposta nova. No `proxy` o watch faz `rebuild` da imagem: ~24 s da edição à resposta nova.

### Imagem final por serviço

**Métrica de tamanho:** tamanho **comprimido** (o que é baixado num `docker pull`), medido por `docker save <imagem> | gzip -c | wc -c`. O `docker images` não serve: com o *containerd image store* (padrão do Docker Desktop), ele mostra o espaço em disco, somando camadas comprimidas e extraídas. Por essa métrica, a `runtime:10.0-noble-chiseled` aparece com 140 MB, contra 42 MB comprimidos. Já o `docker image inspect .Size` muda de significado conforme o store. O `docker save | gzip` dá o mesmo número nos dois stores (±5%).

| Serviço | Base final (comprimida) | Meta de tamanho | Justificativa |
|---|---|---|---|
| `api` | `runtime-deps:10.0-noble-chiseled` (5 MB) + AOT | < 25 MB | Exposta ao tráfego: startup rápido, pouca memória, superfície mínima. Spike com todas as dependências: 17 MB |
| `worker` | `runtime:10.0-noble-chiseled` (42 MB) | < 70 MB | EF Core depende de reflection → sem AOT, mas chiseled e sem root |
| `migrator` | `runtime-deps:10.0-noble-chiseled` (5 MB) + bundle self-contained | < 60 MB | Executa uma vez e sai |
| `proxy` | `nginxinc/nginx-unprivileged:1.29-alpine` (23 MB) | < 40 MB | Runtime final não é .NET; versão sem root (UID 101), escuta na 8080. Medido: 28,4 MB |

As metas são calibradas no primeiro build real de cada serviço (fases 01 e 02) e fixadas no `image-size.sh`.

Imagens chiseled rodam como usuário `app` (UID 1654) e não têm shell.

**Permissão do volume `uploads`:** um volume nomeado nasce com dono `root`, e a `api` (UID 1654) não conseguiria gravar nele. Como a imagem não tem shell para `mkdir`/`chown`, o estágio de publish cria um diretório vazio e o runtime faz `COPY --from=publish --chown=1654:1654 /empty /data/uploads`. Na primeira montagem, o Docker copia o conteúdo e o dono do diretório da imagem para o volume vazio. O mesmo vale para o `worker`, que lê o volume.

**Migrator e o secret:** o bundle do EF Core não lê `KeyPerFile` sozinho. `StatementImporter.Data` expõe um `IDesignTimeDbContextFactory` que monta a connection string pelo mesmo caminho dos outros serviços (variáveis `Db__*` + `/run/secrets/Db__Password`), e o bundle usa essa factory.

Decisões: [ADR 0001](decisions/0001-api-aot-worker-ef-core.md), [ADR 0002](decisions/0002-healthcheck-pelo-proprio-binario.md).

### Healthchecks

- `api`: `HEALTHCHECK CMD ["/app/StatementImporter.Api", "--healthcheck"]` — o binário detecta a flag, faz `GET http://localhost:8080/health` e sai com `0`/`1`.
- `worker`: grava `/tmp/heartbeat` a cada 10 s no loop do consumidor; `--healthcheck` sai com `0` se o arquivo foi modificado nos últimos 30 s.
- `postgres`: `pg_isready`. `rabbitmq`: `rabbitmq-diagnostics -q ping`. `proxy`: `wget -q --spider http://127.0.0.1:8080/` (IPv4 explícito: o `wget` do busybox resolve `localhost` para `::1` primeiro, e o nginx escuta só em IPv4).

### Compose

- `restart: unless-stopped` em `api`, `worker`, `proxy`; `restart: "no"` no `migrator`. A política só religa processos que **saem sozinhos**: `docker kill` e `docker stop` contam como parada manual e não disparam restart (verificado), e container `unhealthy` também não é reiniciado.
- `worker`: `deploy.resources.limits` de **0.5 CPU e 256 MB** ([ADR 0004](decisions/0004-limite-de-recursos-no-worker.md)).
- Redes: `public` (proxy) e `internal` (todos; `internal: true` no compose para não ter saída para a internet).
- Labels OCI (`org.opencontainers.image.source`, `.version`, `.revision`) via build args.
- Imagens de terceiros com tag de versão (`postgres:17-alpine`, `rabbitmq:4-management-alpine`, `nginx-unprivileged:1.29-alpine`), nunca `latest` nem tags sem versão como `alpine`.

---

## Seção 4 — Testes, provas e CI

### Testes .NET

- **`UnitTests`**: parser e validação de linha (cada regra da tabela de validação), cálculo de offsets de blocos.
- **`IntegrationTests`** (Testcontainers com Postgres e RabbitMQ reais):
  - processar o mesmo bloco duas vezes → zero duplicatas, contadores corretos;
  - a divisão cobre exatamente todas as linhas do arquivo, sem sobreposição;
  - enviar o mesmo arquivo duas vezes → mesma importação;
  - `transaction_id` repetido entre arquivos → erro `duplicate`.

### Provas executáveis (`proofs/`, bash)

Rodam contra o stack em pé; `proofs/run-all.sh` executa todas. Funcionam no CI (Linux) e no Windows via Git Bash.

| Prova | Verifica |
|---|---|
| `healthy.sh` | todos os serviços `healthy` em até 60 s |
| `image-size.sh` | metas de tamanho da Seção 3 (falha se passar) |
| `security.sh` | nenhum container de app (inclusive o `proxy`) roda como root; `api` e `worker` sem shell; `importer_app` não é superusuário e não consegue `DROP TABLE` |
| `network.sh` | só `proxy` publica porta; Postgres, RabbitMQ e API inacessíveis do host; a `api` só na rede `internal` |
| `proxy.sh` | index e fallback do SPA, /api e /api/docs via proxy, upload acima de 200 MiB → 413 |
| `volume.sh` | importa → `compose down` (sem `-v`) → `up` → dados continuam lá |
| `resilience.sh` | com 3 workers, importação de 1M de linhas e `docker kill` num worker no meio (ele fica parado; os outros dois assumem os blocos). No fim, total gravado = linhas válidas exatamente |
| `scale.sh` | mede 1 vs 3 workers e escreve a tabela (só reporta, não falha) |

### Gerador de dados

`dotnet run tools/generate-csv.cs -- --lines 1000000 --error-rate 0.01 --out data/1m.csv`. Gera linhas válidas e, na taxa pedida, linhas com cada tipo de erro. Saída determinística por `--seed`.

### CI (GitHub Actions, `ci.yml`)

Em todo push e PR:
1. `dotnet test` (Testcontainers usa o Docker do runner).
2. Build das imagens com buildx e cache `type=gha`.
3. `docker compose up -d --wait`, `proofs/run-all.sh`; tamanhos das imagens e tempos de 1 vs 3 workers no **job summary**.
4. Trivy em `api` e `worker`, comparados com builds de referência na imagem padrão (target `reference` em cada Dockerfile: publish sem AOT sobre `aspnet:10.0` para a api e `runtime:10.0` para o worker) → contagem de CVEs no job summary. A referência só é buildada no CI e nunca publicada.

Somente em push na `master`: publica `ghcr.io/yudikojima/statement-importer-{api,worker,migrator,proxy}` com tags `sha-<curto>` e `latest`. O gate do Trivy falha só com CVE `CRITICAL` com correção disponível ([ADR 0007](decisions/0007-provas-executaveis-contra-o-stack-real.md)).

### README (entregável de portfólio)

Problema → diagrama Mermaid → `docker compose up` → GIF da demo → tabela de medições → decisões (links para os ADRs) → como rodar as provas → badge do CI.
