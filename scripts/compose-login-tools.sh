#!/usr/bin/env bash
set -euo pipefail

DEFAULT_COMPOSE="docker-compose.yml"

if [ $# -eq 0 ]; then
  COMPOSE_FILE="$DEFAULT_COMPOSE"
  ACTION="help"
elif [ $# -eq 1 ]; then
  COMPOSE_FILE="$DEFAULT_COMPOSE"
  ACTION="$1"
else
  COMPOSE_FILE="$1"
  ACTION="$2"
fi

case "$ACTION" in
  up)
    docker compose -f "$COMPOSE_FILE" up -d
    ;;
  down)
    docker compose -f "$COMPOSE_FILE" down
    ;;
  build)
    docker compose -f "$COMPOSE_FILE" build
    ;;
  logs)
    docker compose -f "$COMPOSE_FILE" logs -f --tail 100
    ;;
  self-test)
    docker compose -f "$COMPOSE_FILE" run --rm login-server --self-test
    ;;
  ps)
    docker compose -f "$COMPOSE_FILE" ps
    ;;
  help|*)
    echo "Usage: $0 [compose-file] {up|down|build|logs|ps|self-test}"
    ;;
esac
