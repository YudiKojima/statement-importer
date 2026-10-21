# 0003 — Processamento em blocos idempotente, ack só após commit

**Data:** 2026-10-18
**Status:** Aceito
**Fase:** 01-pipeline-core

## Contexto

Um arquivo de 1M de linhas precisa ser dividido entre vários workers, e um worker pode morrer a qualquer momento (`docker kill`, OOM, deploy). O RabbitMQ garante entrega *at-least-once*: a mesma mensagem pode chegar mais de uma vez. Sem cuidado, isso duplica transações ou deixa importações pela metade.

## Decisão

- O arquivo é dividido em blocos de 10.000 linhas, identificados por offset em bytes e registrados em `import_chunks`.
- Cada bloco é processado numa **única transação** do banco: insere as transações válidas, grava os erros, marca o bloco `done` e atualiza os contadores da importação.
- O bloco é travado com `SELECT ... FOR UPDATE`; bloco já `done` é descartado.
- `transactions.transaction_id` é `unique`; inserção com `ORDER BY transaction_id ... ON CONFLICT DO NOTHING RETURNING`. A ordenação fixa a ordem de travamento das chaves entre workers e evita deadlock entre blocos com IDs repetidos entre si.
- Um reconciliador no worker cobre as mensagens perdidas, agindo só quando a fila correspondente está vazia:
  - republica `ImportRequested` de importações `queued` há mais de 60 s (queda da API entre gravar e publicar);
  - republica `ProcessChunk` dos blocos `pending` de importações sem progresso há mais de 2 min (mensagem descartada pelo `delivery-limit` durante uma queda do banco, ou publicação interrompida).
- Falha de processamento espera 1 s antes do `nack`.
- **Ack na fila só depois do commit.** Prefetch 1 por consumidor.
- `attempts` do bloco é incrementado, em transação separada, **só quando a mensagem chega com a flag `redelivered`** do AMQP. Acima de 5, o bloco e a importação viram `failed` (evita loop infinito com mensagem venenosa). Mensagens novas duplicadas não contam, então republicar não consome tentativas.

## Alternativas consideradas

- **Processar o arquivo inteiro num worker só**: sem paralelismo; `--scale` não teria efeito.
- **Ack antes de processar**: worker morto perde o bloco.
- **Outbox / exactly-once**: mais complexo; a idempotência no banco já garante o resultado correto.
- **Dead-letter queue para mensagens venenosas**: exigiria consumidor extra só para marcar a falha; o contador `attempts` resolve com menos peças.

## Consequências

- Reentrega e mensagens duplicadas são inofensivas, o que permite republicar blocos `pending` livremente.
- O formato do CSV fica restrito (sem vírgula, aspas ou quebra de linha dentro de campos), para que a divisão por linhas seja correta.
- A prova `resilience.sh` e o teste de integração "mesmo bloco duas vezes" verificam essa garantia.
