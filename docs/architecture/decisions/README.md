# Architecture Decision Records (ADRs)

Cada arquivo registra **uma** decisão arquitetural: o contexto, o que foi decidido e as consequências. Serve para que o "por quê" sobreviva ao fim das conversas e sessões em que a decisão foi tomada. Neste projeto, também é parte do portfólio: mostra o raciocínio por trás de cada escolha de Docker.

## Quando criar

Ao tomar uma decisão que afeta mais de um arquivo ou que alguém poderia questionar depois (ex: imagem base, estratégia de build, trade-off aceito).

## Regras

- Nome: `NNNN-titulo-com-hifens.md`, numeração sequencial (`0001`, `0002`, …).
- ADR aceito não é editado. Se a decisão mudar, criar um novo ADR e marcar o antigo como `Substituído por NNNN`.

## Índice

| ADR | Decisão |
|---|---|
| [0001](0001-api-aot-worker-ef-core.md) | API em Native AOT com Dapper.AOT; worker com EF Core sem AOT |
| [0002](0002-healthcheck-pelo-proprio-binario.md) | Healthcheck executado pelo próprio binário (imagens sem shell) |
| [0003](0003-processamento-em-blocos-idempotente.md) | Processamento em blocos idempotente, ack só após commit |
| [0004](0004-limite-de-recursos-no-worker.md) | Limite de CPU e memória no worker |
| [0005](0005-schema-do-ef-core-aplicado-por-migrator.md) | Schema dono do EF Core, aplicado por container migrator one-shot |

## Template

```markdown
# NNNN — Título da decisão

**Data:** AAAA-MM-DD
**Status:** Proposto | Aceito | Substituído por NNNN
**Fase:** NN-nome

## Contexto
Qual problema ou força motivou a decisão.

## Decisão
O que foi decidido, em uma ou duas frases diretas.

## Alternativas consideradas
- Alternativa A — por que não.

## Consequências
O que fica mais fácil, o que fica mais difícil, o que passa a ser obrigatório.
```
