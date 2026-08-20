#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
  echo "Usage: $0 <username> <password> [M|F]" >&2
  exit 1
fi

LOGIN_USER="$1"
LOGIN_PASS="$2"
LOGIN_SEX="${3:-M}"
LOGIN_SEX="$(printf '%s' "$LOGIN_SEX" | tr '[:lower:]' '[:upper:]')"

if [ "${#LOGIN_USER}" -lt 4 ] || [ "${#LOGIN_USER}" -gt 23 ]; then
  echo "Username must contain between 4 and 23 characters." >&2
  exit 1
fi

if [ -z "$LOGIN_PASS" ]; then
  echo "Password must not be empty." >&2
  exit 1
fi

if [ "$LOGIN_SEX" != "M" ] && [ "$LOGIN_SEX" != "F" ]; then
  echo "Sex must be M or F." >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker not found. Please install Docker Desktop." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SECRETS_PATH="$REPO_ROOT/solutionfiles/secrets/secret.json"
ENV_PATH="$REPO_ROOT/.env"
LOGIN_CONF_PATH="$REPO_ROOT/conf/login_athena.conf"

IFS=$'\t' read -r SA_PASSWORD DB_NAME SQL_SERVER < <(python3 - <<'PY' "$ENV_PATH" "$SECRETS_PATH"
import json
import os
import re
import sys

env_path, secrets_path = sys.argv[1:]

def connection_value(connection, key_pattern):
    match = re.search(rf"(?:{key_pattern})\s*=\s*([^;]+)", connection, re.I)
    return match.group(1).strip() if match else ""

def dotenv_value(path, key):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            for raw in handle:
                line = raw.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                name, value = line.split("=", 1)
                if name.strip() != key:
                    continue
                value = value.strip()
                if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
                    value = value[1:-1]
                return value
    except FileNotFoundError:
        pass
    return ""

try:
    with open(secrets_path, "r", encoding="utf-8") as handle:
        secrets = json.load(handle)
except FileNotFoundError:
    secrets = {}

connection = os.environ.get("ConnectionStrings__LoginDb", "")
if not connection:
    connection = os.environ.get("ATHENA_NET_LOGIN_DB_CONNECTION", "")
if not connection:
    connection = secrets.get("LoginDb", {}).get("ConnectionString", "")

sa_password = os.environ.get("SA_PASSWORD", "")
if not sa_password:
    sa_password = dotenv_value(env_path, "SA_PASSWORD")
if not sa_password:
    sa_password = secrets.get("SqlServer", {}).get("SaPassword", "")

database = connection_value(connection, "Database|Initial Catalog") or "athena.net"
server = connection_value(connection, "Server") or "localhost,1433"

print("\t".join((sa_password, database, server)))
PY
)

if [ -z "$SA_PASSWORD" ]; then
  echo "SA_PASSWORD is missing. Set it in $ENV_PATH or $SECRETS_PATH." >&2
  exit 1
fi

USE_MD5_PASSWORDS="no"
if [ -f "$LOGIN_CONF_PATH" ]; then
  USE_MD5_PASSWORDS="$(python3 - <<'PY' "$LOGIN_CONF_PATH"
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    for raw in handle:
        line = raw.split("//", 1)[0].strip()
        if not line or ":" not in line:
            continue
        key, value = line.split(":", 1)
        if key.strip().lower() == "use_md5_passwords":
            print("yes" if value.strip().lower() in ("yes", "on", "true") else "no")
            break
    else:
        print("no")
PY
)"
fi

if [ "$USE_MD5_PASSWORDS" = "yes" ]; then
  LOGIN_PASS="$(python3 - <<'PY' "$LOGIN_PASS"
import hashlib
import sys

print(hashlib.md5(sys.argv[1].encode("utf-8")).hexdigest())
PY
)"
elif [ "${#LOGIN_PASS}" -gt 32 ]; then
  echo "Password must not exceed 32 characters when MD5 storage is disabled." >&2
  exit 1
fi

escape_sql() {
  printf '%s' "$1" | sed "s/'/''/g"
}

SQL_USER="$(escape_sql "$LOGIN_USER")"
SQL_PASS="$(escape_sql "$LOGIN_PASS")"
SQL_EMAIL="$(escape_sql "${LOGIN_USER}@localhost")"

QUERY_FILE="$(mktemp)"
cleanup_query_file() {
  rm -f "$QUERY_FILE"
}
trap cleanup_query_file EXIT

printf '%s\n' \
  'SET NOCOUNT ON;' \
  'SET XACT_ABORT ON;' \
  'SET QUOTED_IDENTIFIER ON;' \
  'SET ANSI_NULLS ON;' \
  'SET ANSI_PADDING ON;' \
  'SET ANSI_WARNINGS ON;' \
  'SET CONCAT_NULL_YIELDS_NULL ON;' \
  'SET ARITHABORT ON;' \
  "IF EXISTS (SELECT 1 FROM [login] WHERE userid = N'$SQL_USER')" \
  "    THROW 50001, 'Account already exists.', 1;" \
  'ELSE' \
  'BEGIN' \
  '    INSERT INTO [login] (userid, user_pass, sex, email, group_id, state, unban_time, expiration_time, logincount, last_ip, character_slots, pincode, pincode_change, vip_time, old_group, web_auth_token_enabled)' \
  "    VALUES (N'$SQL_USER', N'$SQL_PASS', '$LOGIN_SEX', N'$SQL_EMAIL', 0, 0, 0, 0, 0, '127.0.0.1', 0, '', 0, 0, 0, 0);" \
  'END' > "$QUERY_FILE"

SQL_CONTAINER="$(docker compose --project-directory "$REPO_ROOT" -f "$REPO_ROOT/docker-compose.yml" ps -q sql 2>/dev/null || true)"
DOCKER_NETWORK=""
if [ -n "$SQL_CONTAINER" ]; then
  DOCKER_NETWORK="container:$SQL_CONTAINER"
  SQL_SERVER="localhost,1433"
elif [[ "$SQL_SERVER" == localhost* ]]; then
  SQL_SERVER="host.docker.internal${SQL_SERVER#localhost}"
elif [[ "$SQL_SERVER" == 127.0.0.1* ]]; then
  SQL_SERVER="host.docker.internal${SQL_SERVER#127.0.0.1}"
fi

if docker image inspect mcr.microsoft.com/mssql-tools18 >/dev/null 2>&1; then
  TOOLS_IMAGE="mcr.microsoft.com/mssql-tools18"
else
  TOOLS_IMAGE="mcr.microsoft.com/mssql-tools"
  docker pull "$TOOLS_IMAGE" >/dev/null
fi

run_sqlcmd() {
  docker run --rm --platform linux/amd64 \
    "$@" \
    -e SA_PASSWORD="$SA_PASSWORD" \
    -e DB_NAME="$DB_NAME" \
    -e SQL_SERVER="$SQL_SERVER" \
    -v "$QUERY_FILE:/tmp/query.sql:ro" \
    "$TOOLS_IMAGE" sh -c '
    if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then
      SQLCMD=/opt/mssql-tools18/bin/sqlcmd
    else
      SQLCMD=/opt/mssql-tools/bin/sqlcmd
    fi
    "$SQLCMD" -S "$SQL_SERVER" -U sa -P "$SA_PASSWORD" -C -b -d "$DB_NAME" -i /tmp/query.sql
  '
}

if [ -n "$DOCKER_NETWORK" ]; then
  run_sqlcmd --network "$DOCKER_NETWORK"
else
  run_sqlcmd
fi

echo "Created player account '$LOGIN_USER' ($LOGIN_SEX) in database '$DB_NAME'."
