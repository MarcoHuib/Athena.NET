#!/usr/bin/env bash
set -euo pipefail

SECRETS_PATH="${1:-athenadotnet/SolutionFiles/Secrets/secret.json}"
COMPOSE_FILE="${2:-athenadotnet/src/LoginServer/docker-compose.login.yml}"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker not found. Please install Docker Desktop." >&2
  exit 1
fi

if [ ! -f "$SECRETS_PATH" ]; then
  echo "Secrets file not found: $SECRETS_PATH" >&2
  exit 1
fi

SA_PASSWORD="$(python3 - <<'PY' "$SECRETS_PATH"
import json, sys
path = sys.argv[1]
with open(path, "r", encoding="utf-8") as f:
    data = json.load(f)
print(data.get("SqlServer", {}).get("SaPassword", ""))
PY
)"

if [ -z "$SA_PASSWORD" ]; then
  echo "SqlServer.SaPassword is missing in $SECRETS_PATH" >&2
  exit 1
fi

export SA_PASSWORD
docker compose -f "$COMPOSE_FILE" up -d
echo "Login server is up. Use: docker compose -f $COMPOSE_FILE down"
