# 0005 — Schema dono do EF Core, aplicado por container migrator one-shot

**Data:** 2026-10-18
**Status:** Aceito
**Fase:** 01-pipeline-core

## Contexto

`api` (Dapper.AOT) e `worker` (EF Core) acessam as mesmas tabelas. Precisa existir uma única fonte da verdade do schema, aplicada antes de qualquer serviço subir. Aplicar migrations no startup da aplicação causa corrida quando há vários workers (`--scale`).

## Decisão

O schema é definido no `DbContext` e nas migrations do EF Core em `StatementImporter.Data`. As migrations são empacotadas como **migration bundle self-contained** (`dotnet ef migrations bundle`) num container `migrator` que roda uma vez e sai. `api` e `worker` dependem dele com `condition: service_completed_successfully`.

## Alternativas consideradas

- **`Database.Migrate()` no startup do worker**: corrida entre réplicas; a `api` (AOT) não poderia fazer o mesmo.
- **Scripts SQL com DbUp**: segunda ferramenta além do EF Core, que o worker já usa.
- **Migrations da `api` em SQL e EF Core só para leitura/escrita**: schema duplicado em dois lugares.

## Consequências

- O SQL da `api` precisa acompanhar o schema; os testes de integração da `api` rodam sobre o banco migrado pelas mesmas migrations.
- Mais um padrão de container demonstrado: o *one-shot job* com dependência por conclusão.
- O `migrator` usa `restart: "no"`; falha na migration impede `api` e `worker` de subir.
