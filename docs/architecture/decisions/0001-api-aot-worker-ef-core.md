# 0001 — API em Native AOT com Dapper.AOT; worker com EF Core sem AOT

**Data:** 2026-10-18
**Status:** Aceito
**Fase:** 01-pipeline-core

## Contexto

Medição feita antes do projeto, com o mesmo endpoint mínimo em .NET 10 (Docker Desktop, containerd image store):

| Imagem | Espaço em disco (`docker images`) | Comprimido (`docker save \| gzip`) | RAM após 200 req | Usuário |
|---|---|---|---|---|
| `aspnet:10.0` | 340 MB | não medido | 31 MB | root |
| `aspnet:10.0-noble-chiseled` | 180 MB | 55 MB | 31 MB | 1654 |
| AOT + `runtime-deps:10.0-noble-chiseled` | 34 MB | 10 MB | 12 MB | 1654 |

Spike de AOT com todas as dependências previstas para a `api` (Npgsql, Dapper.AOT, RabbitMQ.Client 7, Microsoft.AspNetCore.OpenApi, Scalar.AspNetCore, KeyPerFile), em 2026-10-18: **zero warnings de trimming/AOT**, imagem de 17 MB comprimida, consulta Dapper e publicação no RabbitMQ funcionando contra serviços reais, healthcheck pelo próprio binário `healthy`.

Native AOT é o maior ganho, mas o EF Core depende de reflection e geração dinâmica de código; o suporte a AOT é experimental (modelos pré-compilados, várias limitações). O worker é quem faz o trabalho pesado de persistência, que é onde o EF Core mais ajuda (migrations, transações, `ExecuteUpdate`).

## Decisão

A `api` é publicada em **Native AOT** e acessa o banco com **Dapper.AOT** (SQL explícito, source-generated). O `worker` usa **EF Core** e é publicado **sem AOT**, em imagem chiseled.

## Alternativas consideradas

- **Tudo em AOT com Dapper.AOT**: imagens mínimas em todos os serviços, mas perde EF Core, e as migrations exigiriam outra ferramenta.
- **Tudo com EF Core, sem AOT**: mais simples, mas a imagem exposta ao tráfego fica ~5x maior e mais lenta para subir.
- **EF Core com AOT experimental**: depende de recurso não estável; risco alto para o núcleo do projeto.

## Consequências

- Duas formas de acesso a dados no mesmo projeto: a `api` escreve SQL e precisa acompanhar o schema mantido pelo EF Core ([ADR 0005](0005-schema-do-ef-core-aplicado-por-migrator.md)).
- `StatementImporter.Contracts`, referenciado pela `api`, não pode depender de nada incompatível com AOT/trimming. Warnings de trimming/AOT são tratados como erro no projeto da `api`.
- JSON da `api` usa `JsonSerializerContext` source-generated.
- Toda biblioteca nova na `api` precisa publicar sem warnings de AOT. O conjunto inicial já foi validado no spike; qualquer adição repete a checagem.
- O README explica por que cada serviço tem uma imagem diferente: escolha por caso, não receita única.
