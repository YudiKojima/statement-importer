# Design — Fase 02: frontend e reverse proxy

**Data:** 2026-10-28
**Escopo:** frontend Blazor WebAssembly servido por `nginx-unprivileged`, que também faz proxy de `/api` para a `api`. O `proxy` passa a ser o único serviço com porta publicada; a `api` fica só na rede `internal`. Provas, CI e documentação acompanham a mudança.

O desenho completo está em [`../../architecture/system-design.md`](../../architecture/system-design.md), Seções 1, 3 e 4. Este documento só recorta o que entra nesta fase e registra as decisões do brainstorming.

---

## Entra nesta fase

| Item | Referência no system design |
|---|---|
| Projeto `src/StatementImporter.Web` (Blazor WebAssembly) | Seção 1 — Estrutura do repositório |
| Serviço `proxy` com `docker/nginx.conf`, healthcheck e imagem `statement-importer-proxy` | Seção 1 — Serviços; Seção 3 — Imagens e Healthchecks |
| `api` sem porta publicada, só na rede `internal` | Seção 1 — Serviços; Seção 3 — Compose |
| OpenAPI em `/api/openapi/v1.json` | Seção 2 — Endpoints |
| `network.sh` na forma final, `proxy` em `image-size.sh` e `security.sh`, nova prova `proxy.sh` | Seção 4 — Provas executáveis |
| `proxy` no build, no gate do Trivy e no `publish` do `ci.yml` | Seção 4 — CI |
| `compose.dev.yml` e README provisório com o proxy | Seção 3 — Desenvolvimento; Seção 4 — README |
| ADR 0008 | — |

## Decisão 1 — Uma imagem para estáticos e proxy

`src/StatementImporter.Web/Dockerfile`, contexto na raiz do repositório:

```
restore  → COPY só global.json, Directory.*.props, csproj do Web e os arquivos linkados → dotnet restore
publish  → COPY do código → dotnet publish -c Release; apaga os *.br
runtime  → nginxinc/nginx-unprivileged:1.30-alpine + docker/nginx.conf + wwwroot publicado
```

- O runtime final não é .NET: nginx sem root (UID 101), escutando na 8080. Meta: **< 40 MB** comprimido.
- `apk upgrade` no runtime (como root, voltando ao UID 101): a tag oficial demora a ser reconstruída com as correções do Alpine. No primeiro CI, a `1.29-alpine` trazia `brotli-libs` com CVE CRITICAL corrigida e o gate do Trivy falhou; a base passou para a `1.30-alpine` (nginx stable, Alpine 3.24), que com o upgrade fica sem CVE HIGH/CRITICAL e com 32,7 MB, contra 38,0 MB da `1.29` com o mesmo upgrade.
- Os `.br` saem da imagem porque o nginx da imagem oficial não tem o módulo brotli; os `.gz` ficam para o `gzip_static`.
- Labels OCI (`VERSION`, `REVISION`) como nos outros Dockerfiles.
- Sem target `reference` e sem estágio `dev` (ver Decisão 5).

Alternativas descartadas: imagem `web` separada com um nginx genérico na frente (dois containers para o mesmo papel de borda) e servir os estáticos pela `api` (mistura borda e aplicação e obriga a `api` a ficar exposta).

## Decisão 2 — `nginx.conf`

| Location | Comportamento |
|---|---|
| `/` | `root` com os estáticos, `try_files $uri $uri/ /index.html` (fallback do SPA), `gzip_static on`, `Cache-Control: no-cache` (o navegador revalida pelo ETag; `index.html`, `css/` e `js/` não têm fingerprint no nome) |
| `/api/` | `proxy_pass` para a `api:8080`, `client_max_body_size 200m`, `proxy_request_buffering off`, `proxy_http_version 1.1` |

- `200m` no nginx é 200 MiB, o mesmo `UploadStorage.MaxUploadBytes` do Kestrel. Acima disso o nginx responde `413` pelo `Content-Length`, antes de o corpo chegar à `api`.
- `proxy_request_buffering off`: o upload segue em streaming até a `api`, sem o nginx gravar o arquivo inteiro em disco antes.
- `resolver 127.0.0.11 valid=10s` + upstream numa variável: o nginx resolve `api` de novo em vez de guardar o IP do boot. Sem isso, uma `api` recriada com outro IP (por exemplo, depois de sair pela queda do RabbitMQ) deixaria o proxy respondendo `502`.
- Healthcheck do `proxy`: `wget -q --spider http://127.0.0.1:8080/` (busybox da imagem alpine; o nginx só escuta em IPv4 e o `localhost` do busybox pode resolver para `::1`); fica no `HEALTHCHECK` da imagem. `depends_on: api: condition: service_healthy`.

**OpenAPI atrás do proxy:** hoje o documento fica em `/openapi/v1.json`, fora de `/api/`, e cairia no fallback do SPA. A `api` passa a mapeá-lo em `/api/openapi/{documentName}.json` e o Scalar (`/api/docs`) aponta para essa rota. Tudo que é da `api` fica sob `/api/`; o `/health` continua só interno (healthcheck do container).

## Decisão 3 — Compose e redes

- Serviço `proxy`: imagem `ghcr.io/yudikojima/statement-importer-proxy:${TAG:-local}`, redes `public` e `internal`, `ports: "8080:8080"` (única porta publicada), `restart: unless-stopped`.
- `api`: sai `ports` e a rede `public`; fica só na `internal`.
- O nome da imagem é o nome do serviço, como `api`, `worker` e `migrator`: as provas continuam usando `image "$svc"`.

## Decisão 4 — Frontend

Blazor WebAssembly standalone em `src/StatementImporter.Web`, adicionado ao `StatementImporter.slnx`. CSS próprio, sem o Bootstrap do template.

| Rota | Conteúdo |
|---|---|
| `/` | upload de CSV + lista das 20 importações mais recentes (`GET /api/imports`) |
| `/imports/{id}` | status, contadores e barra de progresso (`processedLines / totalLines`); polling a cada 1,5 s enquanto `queued` ou `processing`; `failureReason` quando `failed`; abas **Linhas rejeitadas** (`/api/imports/{id}/errors`) e **Transações** (`/api/transactions?importId=`), paginadas |

**Upload por XHR via JS interop.** O `HttpClient` do Blazor WASM só faz streaming de corpo de requisição em HTTP/2; em HTTP/1.1 ele carregaria os 200 MB na memória do WASM. Um módulo `wwwroot/js/upload.js` (~30 linhas) envia o `File` com `XMLHttpRequest` (o navegador lê do disco em streaming) para `POST /api/imports?fileName=` com `Content-Type: text/csv`, e reporta o progresso do envio ao componente por `DotNetObjectReference`. Respostas:

| Status | UI |
|---|---|
| `202` | navega para `/imports/{id}` |
| `200` | arquivo já importado: navega para a importação existente, com aviso |
| `400` | mostra a mensagem do `ErrorResponse` (cabeçalho inválido) |
| `413` | "arquivo acima de 200 MB" — checado também no JS antes do envio (`file.size`), porque o nginx responde `413` e fecha a conexão no meio do envio, e o navegador costuma reportar isso como erro de rede |
| outro / erro de rede | mensagem genérica de falha no envio |

**DTOs.** O Contracts referencia Npgsql e RabbitMQ.Client, que não têm lugar no navegador. O Web inclui `Http/Responses.cs` e `Statuses.cs` do Contracts com `<Compile Include="..." Link="..." />` e tem o próprio `WebJsonContext` (source-generated, `JsonSerializerDefaults.Web`). Fonte única dos contratos, nenhuma dependência de servidor no WASM, nenhum projeto novo.

## Decisão 5 — Desenvolvimento

No `compose.dev.yml`, o `proxy` ganha `develop.watch` com `action: rebuild` em `src/StatementImporter.Web` e `docker/nginx.conf`: cada edição refaz a imagem (publish do WASM). Um só `nginx.conf` para dev e produção, sem serviço extra. Quem quiser hot reload do Blazor roda `dotnet watch` no host.

Com a `api` sem porta publicada também no dev, o acesso continua por `localhost:8080` via proxy.

## Decisão 6 — Provas

| Prova | Mudança |
|---|---|
| `network.sh` | forma final: só o `proxy` pode publicar porta; a `api` não publica porta e não está na rede `public` |
| `image-size.sh` | `proxy` < 40 MB |
| `security.sh` | `proxy` não roda como root (`Config.User` = `101`) |
| `healthy.sh` | mensagem inclui o `proxy` |
| `proxy.sh` (nova, depois de `network` no `run-all.sh`) | ver abaixo |

`proxy.sh` verifica:

1. `GET /` → `200` com o `index.html` do Blazor (contém o script do `_framework`).
2. `GET /imports/qualquer-coisa` → `200` com o mesmo `index.html` (fallback do SPA).
3. `GET /api/imports` → `200` JSON vindo da `api` através do proxy.
4. `GET /api/docs` e `GET /api/openapi/v1.json` → `200`.
5. `POST /api/imports` com corpo acima de 200 MiB → `413` (arquivo esparso gerado na hora, apagado no fim).

As provas de upload (`volume`, `scale`, `resilience`) já passam pelo proxy, porque `lib.sh` usa `API=http://localhost:8080`. A UI é verificada à mão no navegador (upload de 1M de linhas, progresso, erros, transações).

## Decisão 7 — CI

- Job `stack`: `proxy` no `targets` do bake, com cache `type=gha,scope=proxy`; gate do Trivy em `api`, `worker`, `migrator` e `proxy`.
- Job `publish`: `proxy` no bake e na tag `latest`; publica `ghcr.io/yudikojima/statement-importer-proxy` com `sha-<curto>` e `latest`.
- Job `test`: o `dotnet build StatementImporter.slnx -warnaserror` passa a compilar o Web.

ADR: `0008-proxy-como-unica-borda.md` (estáticos e `/api` no mesmo nginx, `api` só na rede interna, upload por XHR em vez do `HttpClient` do WASM).

## Critérios de aceite

1. `docker compose up -d --wait` sobe tudo `healthy`, incluindo o `proxy`; só a 8080 do `proxy` está publicada.
2. Pelo navegador em `localhost:8080`: upload de 1M de linhas com progresso do envio, barra de progresso do processamento, linhas rejeitadas e transações paginadas; reenviar o mesmo arquivo abre a importação existente.
3. `localhost:8080/api/docs` abre o Scalar com o documento carregado.
4. `bash proofs/run-all.sh` passa localmente (`IGNORE_HOST_PORTS=5432`) e no CI, com `proxy.sh` e o `network.sh` na forma final.
5. `proxy` < 40 MB comprimido e sem root.
6. O `publish` envia as quatro imagens (`api`, `worker`, `migrator`, `proxy`) ao GHCR.
7. `system-design.md` atualizado (rota do OpenAPI, `network.sh` sem a exceção da `api`, `web` → `proxy` no CI), `compose.dev.yml` e README provisório (diagrama Mermaid, uso pela UI) refletindo o proxy; ADR 0008 criado e listado no índice; fase listada em `docs/README.md`.
