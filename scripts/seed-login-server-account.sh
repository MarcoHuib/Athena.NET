#!/usr/bin/env bash
set -euo pipefail

SECRETS_PATH="${1:-solutionfiles/secrets/secret.json}"
LOGIN_USER_ARG="${2:-}"
LOGIN_PASS_ARG="${3:-}"
ACCOUNT_ID="${4:-1}"
DB_NAME_OVERRIDE="${5:-}"
LOGIN_CONF_PATH="${6:-conf/login_athena.conf}"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker not found. Please install Docker Desktop." >&2
  exit 1
fi

if [ ! -f "$SECRETS_PATH" ]; then
  echo "Secrets file not found: $SECRETS_PATH" >&2
  exit 1
fi

IFS=$'\t' read -r SA_PASSWORD DB_NAME SQL_SERVER SECRET_LOGIN_USER SECRET_LOGIN_PASS < <(python3 - <<'PY' "$SECRETS_PATH"
import json
import os
import re
import sys

path = sys.argv[1]
sa_password = os.environ.get("SA_PASSWORD", "")
conn = os.environ.get("ConnectionStrings__LoginDb", "")
if not conn:
    conn = os.environ.get("ATHENA_NET_LOGIN_DB_CONNECTION", "")

db = ""
server = ""
login_user = ""
login_pass = ""

if conn:
    m = re.search(r"(?:Database|Initial Catalog)\s*=\s*([^;]+)", conn, re.I)
    if m:
        db = m.group(1)
    m = re.search(r"Server\s*=\s*([^;]+)", conn, re.I)
    if m:
        server = m.group(1)

try:
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
except FileNotFoundError:
    data = {}

if not sa_password:
    sa_password = data.get("SqlServer", {}).get("SaPassword", "")

if not db:
    conn = data.get("LoginDb", {}).get("ConnectionString", "")
    m = re.search(r"(?:Database|Initial Catalog)\s*=\s*([^;]+)", conn, re.I)
    db = m.group(1) if m else ""
    m = re.search(r"Server\s*=\s*([^;]+)", conn, re.I)
    server = m.group(1) if m else ""

char_server = data.get("CharServer", {})
login_user = char_server.get("UserId", "") or ""
login_pass = char_server.get("Password", "") or ""

print("\t".join([sa_password, db, server, login_user, login_pass]))
PY
)

LOGIN_USER="$LOGIN_USER_ARG"
LOGIN_PASS="$LOGIN_PASS_ARG"
if [ -z "$LOGIN_USER" ]; then
  LOGIN_USER="$SECRET_LOGIN_USER"
fi
if [ -z "$LOGIN_PASS" ]; then
  LOGIN_PASS="$SECRET_LOGIN_PASS"
fi
if [ -z "$LOGIN_USER" ]; then
  LOGIN_USER="s1"
fi
if [ -z "$LOGIN_PASS" ]; then
  LOGIN_PASS="p1"
fi

USE_MD5_PASSWORDS=""
if [ -f "$LOGIN_CONF_PATH" ]; then
  USE_MD5_PASSWORDS="$(python3 - <<'PY' "$LOGIN_CONF_PATH"
import sys

path = sys.argv[1]
value = ""
with open(path, "r", encoding="utf-8") as f:
    for raw in f:
        line = raw.split("//", 1)[0].strip()
        if not line or ":" not in line:
            continue
        key, val = line.split(":", 1)
        key = key.strip().lower()
        if key == "use_md5_passwords":
            value = val.strip().lower()
print("yes" if value in ("yes", "on", "true") else "no" if value in ("no", "off", "false") else "")
PY
)"
fi

if [ "$USE_MD5_PASSWORDS" = "yes" ]; then
  LOGIN_PASS="$(python3 - <<'PY' "$LOGIN_PASS"
import hashlib
import sys

password = sys.argv[1].encode("utf-8")
print(hashlib.md5(password).hexdigest())
PY
)"
fi

if [ -n "$DB_NAME_OVERRIDE" ]; then
  DB_NAME="$DB_NAME_OVERRIDE"
fi

if [ -z "$SA_PASSWORD" ]; then
  echo "SqlServer.SaPassword is missing in $SECRETS_PATH" >&2
  exit 1
fi

if [ -z "$DB_NAME" ]; then
  DB_NAME="LoginDb"
fi

if [ -z "$SQL_SERVER" ]; then
  SQL_SERVER="localhost,1433"
fi

normalize_sql_server() {
  local server="$1"
  local prefix=""

  if [[ "$server" == tcp:* ]]; then
    prefix="tcp:"
    server="${server#tcp:}"
  fi

  case "$server" in
    localhost*) server="host.docker.internal${server#localhost}" ;;
    127.0.0.1*) server="host.docker.internal${server#127.0.0.1}" ;;
  esac

  if [[ "$server" == "host.docker.internal" ]]; then
    server="host.docker.internal,1433"
  fi

  printf "%s%s" "$prefix" "$server"
}

DOCKER_NETWORK=""
SQL_EDGE_CONTAINER=""

if [[ "$SQL_SERVER" == localhost* || "$SQL_SERVER" == 127.0.0.1* || "$SQL_SERVER" == tcp:localhost* || "$SQL_SERVER" == tcp:127.0.0.1* ]]; then
  if SQL_EDGE_CONTAINER="$(docker ps --filter 'ancestor=mcr.microsoft.com/azure-sql-edge:latest' --format '{{.Names}}' | head -n1)"; then
    if [ -n "$SQL_EDGE_CONTAINER" ]; then
      DOCKER_NETWORK="container:$SQL_EDGE_CONTAINER"
      SQL_SERVER="localhost,1433"
    else
      SQL_SERVER="$(normalize_sql_server "$SQL_SERVER")"
    fi
  else
    SQL_SERVER="$(normalize_sql_server "$SQL_SERVER")"
  fi
fi

escape_sql() {
  printf "%s" "$1" | sed "s/'/''/g"
}

SQL_USER="$(escape_sql "$LOGIN_USER")"
SQL_PASS="$(escape_sql "$LOGIN_PASS")"
SQL_EMAIL="$(escape_sql "${LOGIN_USER}@localhost")"

QUERY=$(cat <<SQL
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
IF NOT EXISTS (SELECT 1 FROM [login] WHERE userid = N'$SQL_USER')
BEGIN
  IF EXISTS (SELECT 1 FROM [login] WHERE account_id = $ACCOUNT_ID)
  BEGIN
    INSERT INTO [login] (userid, user_pass, sex, email, group_id, state, unban_time, expiration_time, logincount, last_ip, character_slots, pincode, pincode_change, vip_time, old_group, web_auth_token_enabled)
    VALUES (N'$SQL_USER', N'$SQL_PASS', 'S', N'$SQL_EMAIL', 0, 0, 0, 0, 0, '127.0.0.1', 0, '', 0, 0, 0, 0);
  END
  ELSE
  BEGIN
    SET IDENTITY_INSERT [login] ON;
    INSERT INTO [login] (account_id, userid, user_pass, sex, email, group_id, state, unban_time, expiration_time, logincount, last_ip, character_slots, pincode, pincode_change, vip_time, old_group, web_auth_token_enabled)
    VALUES ($ACCOUNT_ID, N'$SQL_USER', N'$SQL_PASS', 'S', N'$SQL_EMAIL', 0, 0, 0, 0, 0, '127.0.0.1', 0, '', 0, 0, 0, 0);
    SET IDENTITY_INSERT [login] OFF;
  END
END
SQL
)

QUERY_FILE="$(mktemp)"
cleanup_query_file() {
  rm -f "$QUERY_FILE"
}
trap cleanup_query_file EXIT
printf "%s" "$QUERY" > "$QUERY_FILE"

if docker image inspect mcr.microsoft.com/mssql-tools18 >/dev/null 2>&1; then
  TOOLS_IMAGE="mcr.microsoft.com/mssql-tools18"
else
  TOOLS_IMAGE="mcr.microsoft.com/mssql-tools"
fi

docker pull "$TOOLS_IMAGE" >/dev/null

docker run --rm --platform linux/amd64 \
  ${DOCKER_NETWORK:+--network "$DOCKER_NETWORK"} \
  -e SA_PASSWORD="$SA_PASSWORD" -e DB_NAME="$DB_NAME" -e SQL_SERVER="$SQL_SERVER" \
  -v "$QUERY_FILE:/tmp/query.sql:ro" \
  "$TOOLS_IMAGE" sh -c '
    if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then
      SQLCMD=/opt/mssql-tools18/bin/sqlcmd
    elif [ -x /opt/mssql-tools/bin/sqlcmd ]; then
      SQLCMD=/opt/mssql-tools/bin/sqlcmd
    else
      echo "sqlcmd not found in tools image." >&2
      exit 1
    fi
    "$SQLCMD" -S "$SQL_SERVER" -U sa -P "$SA_PASSWORD" -C -d "$DB_NAME" -i /tmp/query.sql
  '

echo "Seeded login server account for '$LOGIN_USER' in $DB_NAME."
