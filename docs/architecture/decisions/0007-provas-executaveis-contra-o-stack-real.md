# 0007 — Provas executáveis contra o stack real

**Data:** 2026-10-25
**Status:** Aceito
**Fase:** 03-provas-e-ci

## Contexto

As afirmações centrais do projeto são sobre containers, não sobre código: imagem pequena e sem root, porta não publicada, rede sem saída, dados que sobrevivem ao `down`, worker morto sem duplicar dados. Testes .NET com Testcontainers não sobem o `compose.yml` real e não enxergam essas propriedades.

## Decisão

Scripts bash em `proofs/` rodam contra o `compose.yml` real, no CI e no Git Bash, e falham quando a propriedade não vale. Medições (tamanhos, 1 × 3 workers, CVEs) vão para o job summary. Para mostrar o efeito das imagens chiseled, o CI builda um target `reference` (mesmo código sobre `aspnet:10.0`/`runtime:10.0`) e compara as CVEs; o gate só falha com CVE `CRITICAL` que tenha correção.

## Alternativas consideradas

- **Só testes .NET**: não verificam imagem, rede, volume nem restart.
- **Provas em C# (file-based app)**: um runtime a mais para orquestrar `docker`; em bash, cada prova é lida como uma sequência de comandos Docker.
- **Gate em HIGH ou sem `--ignore-unfixed`**: CVEs recém-publicadas sem patch travariam o CI sem ação possível.

## Consequências

- O README cita números que o CI mede a cada push.
- As provas dependem de `bash`, `curl`, `awk`, `sed` e `timeout` (presentes no runner e no Git Bash), sem `jq`.
- O job `stack` leva alguns minutos (três importações de 1M de linhas).
- O target `reference` precisa acompanhar mudanças no publish da `api` e do `worker`.
- O Trivy baixa o banco de vulnerabilidades primeiro de `mirror.gcr.io`: o `ghcr.io` limita requisições nos runners do GitHub.
- O job `publish` rebuilda a partir do cache `gha` em vez de enviar as imagens exatas testadas, porque o label `VERSION` difere entre as duas builds.
