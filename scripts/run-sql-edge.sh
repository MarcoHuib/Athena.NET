#!/usr/bin/env bash
set -euo pipefail

SECRETS_PATH="${1:-solutionfiles/secrets/secret.json}"
IMAGE="mcr.microsoft.com/azure-sql-edge:latest"
CONTAINER="athena.net-sql-edge"
PORT="1433"

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

if docker ps -a --format "{{.Names}}" | grep -qx "$CONTAINER"; then
  echo "Container $CONTAINER already exists. Start it with: docker start $CONTAINER"
  exit 0
fi

docker pull "$IMAGE"
docker run -d --name "$CONTAINER" \
  -e "ACCEPT_EULA=Y" \
  -e "SA_PASSWORD=$SA_PASSWORD" \
  -p "$PORT:1433" \
  "$IMAGE"

echo "Started $CONTAINER on port $PORT."
