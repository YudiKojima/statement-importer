# statement-importer — Visão do Projeto

Pipeline de importação assíncrona de extratos bancários em CSV, construído como projeto de portfólio de Docker. Cada conceito (imagem, build, container, volume, network, compose) aparece por uma necessidade real e é verificado por provas executáveis no CI.

Desenho completo: [`architecture/system-design.md`](architecture/system-design.md).

## Fases

Ordem de execução: 01 → 03 → 02 → 04. A numeração das fases foi mantida.

### 01 — Núcleo do pipeline ✅ Concluída (2026-10-21)
API (Native AOT), worker (EF Core), Postgres, RabbitMQ e migrator no compose; processamento em blocos idempotente; testes unitários e de integração com Testcontainers; gerador de CSV.
Pasta: [`features/01-pipeline-core/`](features/01-pipeline-core/)

### 03 — Provas executáveis e CI ✅ Concluída (2026-10-28)
Scripts em `proofs/`, workflow do GitHub Actions com testes, provas, job summary com medições, Trivy e publicação no GHCR; usuários de banco com privilégio mínimo. Executada **antes** da fase 02: a prova automatizada é o diferencial do portfólio, e o CI passa a proteger a fase 02.
Pasta: [`features/03-provas-e-ci/`](features/03-provas-e-ci/)

### 02 — Frontend e reverse proxy
Blazor WebAssembly servido por `nginx-unprivileged`, que também faz proxy de `/api`. A `api` deixa de publicar porta; só o `proxy` fica exposto. Redes `public` e `internal` na forma final. O `network.sh` passa a exigir que só o `proxy` publique porta.

### 04 — README de portfólio e demo
README com diagrama, medições, decisões (links para os ADRs), GIF da demo e badge do CI.
