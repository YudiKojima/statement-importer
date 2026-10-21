# Documentação — statement-importer

| Pasta / arquivo | Conteúdo | Ciclo de vida |
|---|---|---|
| [`project-overview.md`](project-overview.md) | Estado do projeto e fases (concluídas e próximas) | Atualizado ao fim de cada fase |
| [`architecture/system-design.md`](architecture/system-design.md) | Desenho completo do sistema: serviços, fluxo de dados, imagens, testes e CI | Vivo — atualizado quando o desenho muda |
| [`architecture/decisions/`](architecture/decisions/) | ADRs — uma decisão arquitetural por arquivo | Criado quando a decisão é tomada; nunca editado depois, só substituído |
| [`features/`](features/) | Uma pasta por fase: `design.md` (o quê e por quê) + `plan.md` (passo a passo) | Congelado após a entrega da fase |

## Fases

| Fase | Pasta |
|---|---|
| 01 — Núcleo do pipeline | [`features/01-pipeline-core/`](features/01-pipeline-core/) |

## Convenções

- Nova fase → `features/NN-nome-com-hifens/` com `design.md` e `plan.md`.
- Documento extra na mesma fase → `<assunto>-design.md` ou `<assunto>-plan.md`.
- Não criar `docs/specs/` nem `docs/superpowers/` — esta estrutura substitui ambos.
