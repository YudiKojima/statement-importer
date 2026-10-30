#!/usr/bin/env bash
# Todos os serviços healthy em até 60 s e migrator concluído com código 0.
source "$(dirname "$0")/lib.sh"

up_wait
code=$(docker inspect -f '{{.State.ExitCode}}' "$(docker compose ps -aq migrator)")
[ "$code" = 0 ] || fail "migrator saiu com código $code"
pass "proxy, api, worker, postgres e rabbitmq healthy; migrator Exited (0)"
