# statement-importer — Instruções para Claude Code

## Documentos de Referência

| Documento | Quando ler |
|---|---|
| [`docs/architecture/system-design.md`](docs/architecture/system-design.md) | **Obrigatório** antes de qualquer tarefa — serviços, fluxo de dados, imagens, testes e CI |
| [`docs/architecture/decisions/`](docs/architecture/decisions/) | Antes de mudar imagem base, estratégia de build, acesso a dados ou processamento — o "por quê" das decisões |
| [`docs/project-overview.md`](docs/project-overview.md) | Para saber o estado atual e as próximas fases |
| [`docs/README.md`](docs/README.md) | Índice da pasta `docs/` — onde fica cada tipo de documento |

---

## Skills Disponíveis

Plugin: **`superpowers`**

| Skill | Quando usar |
|---|---|
| `brainstorming` | Antes de qualquer nova fase ou decisão de design |
| `writing-plans` | Após o brainstorming aprovado — converte o design em plano passo a passo |
| `executing-plans` | Para executar um plano existente com checkpoints de revisão |
| `using-git-worktrees` | Ao iniciar uma nova fase — branch isolado a partir do `master` |
| `systematic-debugging` | Ao encontrar qualquer bug ou comportamento inesperado, antes de propor fix |
| `verification-before-completion` | Antes de declarar qualquer fase como concluída |
| `requesting-code-review` | Antes de abrir PR ou marcar fase como entregue |
| `finishing-a-development-branch` | Quando a implementação está completa e é hora de estruturar o merge/PR |

**Convenção de branch:** `dev/{descricao-com-hifens}` criado a partir do `master`.

**Fluxo padrão de uma nova fase:**
1. `/brainstorming` → design aprovado → salvo em `docs/features/NN-nome/design.md`
2. `/writing-plans` → plano aprovado → salvo em `docs/features/NN-nome/plan.md`
3. `/using-git-worktrees` → branch `dev/{descricao}` a partir do `master`
4. `/executing-plans` → implementação com checkpoints
5. `/verification-before-completion` → `dotnet build StatementImporter.slnx` + `dotnet test` + `docker compose build` + `docker compose up -d --wait` + `bash proofs/run-all.sh`
6. `/requesting-code-review`
7. `/finishing-a-development-branch` → `project-overview.md` com a fase marcada `✅ Concluída (AAAA-MM-DD)` **no mesmo commit final** → PR aberto

**Onde salvar documentos (sobrepõe o caminho padrão das skills):**
- Specs e planos **nunca** em `docs/superpowers/` nem `docs/specs/` — sempre em `docs/features/NN-nome/`. Arquivos extras da mesma fase: `<assunto>-plan.md` ou `<assunto>-design.md`.
- Decisão arquitetural tomada durante uma fase → ADR em `docs/architecture/decisions/` (ver o README da pasta).

---

## Como se Comportar

### Antes de Implementar

1. Ler [`docs/architecture/system-design.md`](docs/architecture/system-design.md) e o `design.md` da fase.
2. Para tarefas novas ou com decisão de design, invocar `brainstorming` → `writing-plans` antes de qualquer código.
3. **Aguardar aprovação explícita** antes de escrever código.

### Durante a Implementação

- Implementar **somente o escopo do passo atual**.
- Projeto `StatementImporter.Api` e tudo que ele referencia: nada que gere warning de trimming/AOT. Warning de AOT é erro.
- Imagens de terceiros sempre com tag fixa, nunca `latest`.
- Nunca commitar segredos: `.env` e `secrets/*.txt` ficam no `.gitignore`.
- Verificar o `.csproj` antes de usar qualquer namespace de pacote externo.
- Em caso de ambiguidade, parar e confirmar antes de editar código.

### Estilo de Resposta

- Respostas curtas e diretas, em português.
- Referências a código como links clicáveis: `[arquivo.cs](caminho/relativo)`.

### Git e Commits

- **Nunca commitar durante a implementação de uma fase.** Só commitar quando o usuário pedir explicitamente ou ao finalizar com `/finishing-a-development-branch`.
- Após abrir o PR, conferir a CI com `gh pr checks`. PR com CI ❌ não é mergeado.

### O que Evitar

- Refatorar ou criar abstrações além do que a tarefa exige.
- Tratamento de erro para cenários impossíveis.
- Comentários que descrevem o que o código faz.
- Implementar sem aprovação do plano.
