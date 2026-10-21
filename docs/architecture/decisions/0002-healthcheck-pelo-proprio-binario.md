# 0002 — Healthcheck executado pelo próprio binário

**Data:** 2026-10-18
**Status:** Aceito
**Fase:** 01-pipeline-core

## Contexto

As imagens chiseled não têm shell, `curl` nem `wget`. O padrão `HEALTHCHECK CMD curl -f http://localhost/health` não funciona, e `docker exec ... sh` também não. Sem healthcheck, `depends_on: condition: service_healthy` e `restart` perdem utilidade.

## Decisão

Cada binário .NET aceita a flag `--healthcheck`:
- `api`: faz `GET http://localhost:8080/health` e sai com `0` (2xx) ou `1`.
- `worker` (sem HTTP): o loop do consumidor grava `/tmp/heartbeat` a cada 10 s; `--healthcheck` sai com `0` se o arquivo foi modificado nos últimos 30 s.

O Dockerfile usa a forma exec: `HEALTHCHECK CMD ["/app/<binário>", "--healthcheck"]`.

## Alternativas consideradas

- **Copiar `curl` estático para a imagem**: reintroduz binário e superfície de ataque que a chiseled removeu.
- **Healthcheck só no compose com imagem auxiliar**: não funciona em `docker run` puro e acopla o health à orquestração.
- **Endpoint HTTP no worker**: adiciona Kestrel ao worker só para isso.

## Consequências

- O `Program.cs` de `api` e `worker` trata `--healthcheck` antes de construir o host, para sair rápido.
- O Docker **não reinicia** container `unhealthy`; o healthcheck alimenta `depends_on` e a observação (`docker ps`). Para se recuperarem sozinhos, `api` e `worker` encerram o processo com código 1 quando perdem a conexão com a fila, e o `restart: unless-stopped` os religa (verificado parando o RabbitMQ com o stack de pé).
- O heartbeat do worker reflete o loop do host e a conexão com a fila aberta. Ele não detecta um handler travado no meio de um bloco: isso fica fora do escopo.
- Debug dentro do container exige sidecar (`docker run --pid container:<id> --network container:<id>` com uma imagem que tenha ferramentas), documentado no README.
