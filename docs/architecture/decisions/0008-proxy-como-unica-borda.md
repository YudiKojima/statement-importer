# 0008 — Proxy como única borda

**Data:** 2026-10-28
**Status:** Aceito
**Fase:** 02-frontend-e-proxy

## Contexto

Até a fase 02, a `api` publicava a 8080 e era a porta de entrada do sistema. O frontend Blazor WebAssembly precisa de um servidor de estáticos, e o upload de até 200 MB precisa chegar à `api` em streaming. Expor `api` e frontend em portas separadas exigiria CORS e deixaria a `api` acessível de fora.

## Decisão

Um único serviço `proxy` (`nginxinc/nginx-unprivileged:1.29-alpine`) serve os estáticos do Blazor e repassa `/api/` para a `api` na rede `internal`. Só ele publica porta e só ele está na rede `public`. O nginx usa `client_max_body_size 200m` e `proxy_request_buffering off`, e resolve o nome `api` pelo DNS do Docker com TTL curto. Tudo que é da `api` fica sob `/api/`, inclusive o documento OpenAPI. No navegador, o upload usa `XMLHttpRequest` via JS interop em vez do `HttpClient` do Blazor.

## Alternativas consideradas

- **Imagem `web` separada com um nginx genérico na frente**: dois containers para o mesmo papel de borda.
- **Estáticos servidos pela própria `api`**: mistura borda e aplicação e obriga a `api` a ficar exposta.
- **`HttpClient` do Blazor no upload**: só faz streaming do corpo em HTTP/2; em HTTP/1.1 o arquivo de 200 MB passaria inteiro pela memória do WASM.

## Consequências

- `network.sh` exige que só o `proxy` publique porta e que a `api` não esteja na rede `public`.
- Mesma origem para UI e API: sem CORS.
- O limite de 200 MiB existe em três lugares (Kestrel, nginx, JS) e precisa mudar junto.
- O JS checa o tamanho antes do envio: o `413` do nginx fecha a conexão no meio do upload e o navegador o reporta como erro de rede.
- O `/health` da `api` não é exposto; o healthcheck do `proxy` só cobre o nginx, e a dependência `api` *healthy* fica no `depends_on`.
- A imagem do `proxy` não é .NET e tem shell (alpine); a prova de segurança só exige que ela não rode como root.
