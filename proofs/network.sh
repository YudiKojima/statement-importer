#!/usr/bin/env bash
# Só a api publica porta (fase 02: só o proxy), Postgres e RabbitMQ não respondem no host,
# e a rede internal não tem saída para a internet.
# Local: IGNORE_HOST_PORTS="5432" pula portas ocupadas por serviços do próprio host (ex.: Postgres nativo).
source "$(dirname "$0")/lib.sh"

for id in $(docker compose ps -q); do
  service=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.service"}}' "$id")
  ports=$(docker port "$id")
  if [ -n "$ports" ] && [ "$service" != api ]; then fail "$service publica porta: $ports"; fi
done

for port in 5432 5672 15672; do
  case " ${IGNORE_HOST_PORTS:-} " in *" $port "*) echo "aviso: porta $port ignorada (IGNORE_HOST_PORTS)" >&2; continue ;; esac
  if timeout 3 bash -c "</dev/tcp/127.0.0.1/$port" 2>/dev/null; then fail "porta $port responde no host"; fi
done

[ "$(docker network inspect -f '{{.Internal}}' statement-importer_internal)" = true ] \
  || fail "rede statement-importer_internal sem internal: true"

# Controle: sem isso, uma máquina offline faria a verificação seguinte passar por engano.
docker run --rm --network bridge busybox:1.37 wget -q -T 5 -O /dev/null http://example.com \
  || fail "controle: sem internet nem na rede bridge, a prova não é conclusiva"
if docker run --rm --network statement-importer_internal busybox:1.37 wget -q -T 5 -O /dev/null http://example.com; then
  fail "a rede internal tem saída para a internet"
fi

pass "só a api publica porta; Postgres e RabbitMQ fechados no host; rede internal sem saída"
