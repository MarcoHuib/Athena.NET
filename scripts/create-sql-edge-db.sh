#!/usr/bin/env bash
set -euo pipefail

SECRETS_PATH="${1:-solutionfiles/secrets/secret.json}"
CONTAINER="athena.net-sql-edge"
DB_NAME="${2:-athena.net}"

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

SQLCMD_PATH="$(docker exec "$CONTAINER" sh -c "if command -v sqlcmd >/dev/null 2>&1; then command -v sqlcmd; elif [ -x /opt/mssql-tools18/bin/sqlcmd ]; then echo /opt/mssql-tools18/bin/sqlcmd; elif [ -x /opt/mssql-tools/bin/sqlcmd ]; then echo /opt/mssql-tools/bin/sqlcmd; fi")"

QUERY="IF DB_ID(N'$DB_NAME') IS NULL CREATE DATABASE [$DB_NAME];"

if [ -z "$SQLCMD_PATH" ]; then
  echo "sqlcmd not found in container. Using helper mssql-tools image..." >&2

  if docker image inspect mcr.microsoft.com/mssql-tools18 >/dev/null 2>&1; then
    TOOLS_IMAGE="mcr.microsoft.com/mssql-tools18"
  else
    TOOLS_IMAGE="mcr.microsoft.com/mssql-tools"
  fi

  docker pull "$TOOLS_IMAGE" >/dev/null
  docker run --rm --platform linux/amd64 --network "container:$CONTAINER" \
    -e SA_PASSWORD="$SA_PASSWORD" -e SQLCMD_QUERY="$QUERY" \
    "$TOOLS_IMAGE" sh -c '
      if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then
        SQLCMD=/opt/mssql-tools18/bin/sqlcmd
      elif [ -x /opt/mssql-tools/bin/sqlcmd ]; then
        SQLCMD=/opt/mssql-tools/bin/sqlcmd
      else
        echo "sqlcmd not found in tools image." >&2
        exit 1
      fi
      "$SQLCMD" -S localhost -U sa -P "$SA_PASSWORD" -C -Q "$SQLCMD_QUERY"
    '

  echo "Database '$DB_NAME' is ready."
  exit 0
fi

docker exec -it "$CONTAINER" \
  "$SQLCMD_PATH" \
  -S localhost -U sa -P "$SA_PASSWORD" -C \
  -Q "$QUERY"

echo "Database '$DB_NAME' is ready."
