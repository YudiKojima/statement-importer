# 0006 — Papéis de banco com privilégio mínimo

**Data:** 2026-10-25
**Status:** Aceito
**Fase:** 03-provas-e-ci

## Contexto

Na fase 01, `api`, `worker` e `migrator` conectavam com o `POSTGRES_USER`, que é superusuário. Uma falha de injeção ou um bug na `api` teria poder para apagar o banco inteiro, ler arquivos do servidor ou criar papéis.

## Decisão

Três papéis: o superusuário `postgres` só no bootstrap do container; `importer_owner`, dono do banco e do schema, usado pelo `migrator`; `importer_app`, só com `SELECT/INSERT/UPDATE/DELETE` e `TEMP`, usado por `api` e `worker`. Os papéis são criados por um script em `/docker-entrypoint-initdb.d`, e `ALTER DEFAULT PRIVILEGES` estende os grants às tabelas de migrations futuras.

## Alternativas consideradas

- **Dois papéis (migrator como superusuário)**: menos um secret, mas um container da aplicação continuaria com poder total.
- **Criar papéis numa migration do EF Core**: a senha acabaria no código ou exigiria superusuário no migrator.

## Consequências

- Nenhum container da aplicação conecta como superusuário; `security.sh` prova que `importer_app` não consegue `DROP TABLE`.
- Três secrets em vez de um; cada serviço monta o seu como `Db__Password`, sem mudança no código.
- O script de init só roda com `pgdata` vazio: mudar papéis depois exige `docker compose down -v` (ou um script de migração de papéis).
- Testes de integração continuam com o superusuário do Testcontainers; o privilégio mínimo é verificado pela prova.
- O `security.sh` conecta com `-h postgres`, não em localhost: o `pg_hba.conf` da imagem confia em 127.0.0.1, então só o hostname do serviço exige a senha.
