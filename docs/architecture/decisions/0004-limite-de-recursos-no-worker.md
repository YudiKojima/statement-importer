# 0004 — Limite de CPU e memória no worker

**Data:** 2026-10-18
**Status:** Aceito
**Fase:** 01-pipeline-core

## Contexto

Sem limite, um único worker usa todos os núcleos da máquina, e `docker compose up --scale worker=3` quase não mostra ganho. A medição dependeria do hardware de quem roda. Além disso, um container sem limite de memória pode derrubar o host.

## Decisão

O serviço `worker` tem `deploy.resources.limits` de **0.5 CPU e 256 MB**.

## Alternativas consideradas

- **Sem limite**: escala não demonstrável, resultado varia por máquina.
- **Limite por variável de ambiente**: flexível, mas a medição publicada no README deixa de ser reproduzível.

## Consequências

- A medição de 1 vs 3 workers é justa e reproduzível (no CI e localmente).
- O worker precisa caber em 256 MB: leitura do arquivo por streaming e blocos de 10.000 linhas, nunca o arquivo inteiro em memória.
- O runtime .NET respeita o limite de memória do cgroup; `DOTNET_GCHeapHardLimit` não é necessário.
- O limite é documentado no README como parte do método de medição.
