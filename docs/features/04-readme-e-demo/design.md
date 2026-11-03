# Design — Fase 04: README de portfólio e demo

**Data:** 2026-10-31
**Escopo:** reescrever o `README.md` em inglês como vitrine do projeto e criar um gravador reprodutível do GIF da demo em `tools/demo/`. Nenhuma mudança em `src/`, no `compose.yml` ou no CI.

A ordem do README vem de [`../../architecture/system-design.md`](../../architecture/system-design.md), Seção 4 — README. Este documento registra as decisões do brainstorming.

---

## Decisão 1 — README em inglês, enxuto, com links para os docs

O público de portfólio inclui recrutadores de fora, então o `README.md` passa para inglês. `docs/` e os ADRs continuam em português, e o README avisa isso. Cada decisão ocupa uma linha com link para o ADR: os ADRs seguem como fonte única dos porquês.

Alternativas descartadas: README em português (limita o público), `README.pt-BR.md` paralelo (o dobro para manter), README longo com o resumo dos ADRs (repete e desatualiza junto) e `docs/showcase.md` separado (um clique a mais para o recrutador).

Ordem dos blocos:

| Bloco | Conteúdo |
|---|---|
| Título + badge do CI | Uma frase: *Async CSV bank-statement import pipeline: a .NET 10 Docker portfolio project* |
| The problem | O parágrafo atual sobre timeout, duplicação e falta de progresso, traduzido |
| Demo | `docs/assets/demo.gif` |
| Architecture | O Mermaid atual com rótulos em inglês e a lista curta dos serviços (`proxy`, `api`, `worker`, `migrator`, `postgres`, `rabbitmq`) |
| Quick start | Copiar os secrets, `docker compose up -d --wait`, abrir `localhost:8080`; alternativa por `curl`; `--scale worker=3` |
| Verified measurements | A tabela atual, traduzida, separando o que é prova no CI do que foi medido à mão |
| Design decisions | Uma linha por ADR (0001–0008): decisão e porquê em inglês, com link para o ADR em português |
| Proofs | `bash proofs/run-all.sh`, `IGNORE_HOST_PORTS` e uma linha sobre o que cada prova verifica |
| Recording the demo | Um parágrafo sobre `tools/demo/record.sh` |
| Documentation | Links para o system design, os ADRs e a visão do projeto, com o aviso de que estão em português |

Saem o aviso "README provisório" e a seção "O que existe hoje (fases…)". A UI continua em português, e o README diz isso em uma linha.

## Decisão 2 — GIF gerado por script, em container

O GIF é gerado por script para poder ser refeito quando a UI mudar. O gravador roda em container: no host só são necessários o Docker e o SDK do .NET 10 (este já exigido pelo gerador de CSV).

Alternativas descartadas: gravação manual (é preciso regravar à mão a cada mudança na UI), app .NET file-based com `Microsoft.Playwright` (o host precisaria instalar os navegadores e o `ffmpeg`) e um segundo GIF do terminal (README mais pesado sem ganho proporcional).

| Arquivo em `tools/demo/` | Papel |
|---|---|
| `Dockerfile` | `mcr.microsoft.com/playwright` com tag fixa, `ffmpeg` por `apt-get` e `npm ci`. Imagem local `statement-importer-demo`, que não vai para o GHCR |
| `package.json`, `package-lock.json` | Só o `playwright`, na mesma versão da imagem |
| `record.mjs` | Roteiro no navegador |
| `record.sh` | Orquestrador |

`record.sh`:

1. Avisa que apaga os volumes e roda `docker compose down -v`, depois `docker compose up -d --wait --scale worker=3`. O stack limpo deixa a lista da home vazia e evita a deduplicação por SHA-256.
2. Gera `data/demo.csv` (1M de linhas, `--seed 42`) com `tools/generate-csv.cs`, se o arquivo não existir.
3. Faz o build da imagem e roda o container na rede `statement-importer_public`, com destino `http://proxy:8080`, `data/` montado só para leitura e `data/demo/` para a saída.
4. Converte o vídeo com o `ffmpeg` do mesmo container em `docs/assets/demo.gif`: 2× mais rápido, 960 px de largura, 10 fps, paleta em duas passadas.

`record.mjs`, com viewport de 1280×800 e `recordVideo`:

1. Abre `/` e escolhe `demo.csv` no `input[type=file]`.
2. Clica em **Enviar** e espera a navegação para `/imports/{id}`.
3. Espera o status `completed_with_errors`, com timeout de 120 s, mostrando a barra de progresso.
4. Abre **Linhas rejeitadas**, pausa, abre **Transações** e pausa.
5. Volta para `/` e mostra a importação na lista.

O cenário é 1M de linhas com 3 workers porque mostra o número real do projeto; a aceleração uniforme de 2× mantém o GIF curto sem edição por trechos. O script fica fora do CI: é uma ferramenta manual. O `.gitignore` ganha `tools/demo/node_modules/`; `data/demo/` já está coberto pela regra `data/`.

## Decisão 3 — Documentação e ADR

- `docs/project-overview.md`: fase 04 marcada como concluída no commit final.
- `docs/architecture/system-design.md`: a seção README passa a citar o idioma inglês e `tools/demo/`, que também entra na árvore da Seção 1.
- `docs/README.md`: inclui a fase 04.

Sem ADR novo: nem o idioma do README nem o gravador da demo mudam a arquitetura.

## Fora do escopo

- Traduzir `docs/`, os ADRs ou a UI.
- Rodar a gravação no CI.
- Os itens adiados da fase 02: formatação de números na UI, rolagem horizontal da tabela e headers de segurança no nginx. O GIF mostra os números como estão hoje.

## Critérios de aceite

1. `bash tools/demo/record.sh` funciona do zero, só com Docker e o SDK do .NET 10, e gera `docs/assets/demo.gif` com menos de 5 MB.
2. O GIF mostra upload, progresso, linhas rejeitadas, transações e a lista de importações.
3. O README renderiza no GitHub com GIF, Mermaid e badge, e todos os links relativos funcionam.
4. As medições do README batem com o último job summary do CI no `master`; o número que divergir é atualizado.
5. `dotnet build StatementImporter.slnx`, `dotnet test`, `docker compose build`, `docker compose up -d --wait` e `bash proofs/run-all.sh` continuam passando.
