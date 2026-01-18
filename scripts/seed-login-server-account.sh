#!/usr/bin/env bash
set -euo pipefail

SECRETS_PATH="${1:-solutionfiles/secrets/secret.json}"
LOGIN_USER="${2:-s1}"
LOGIN_PASS="${3:-p1}"
ACCOUNT_ID="${4:-1}"
DB_NAME_OVERRIDE="${5:-}"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker not found. Please install Docker Desktop." >&2
  exit 1
fi

if [ ! -f "$SECRETS_PATH" ]; then
  echo "Secrets file not found: $SECRETS_PATH" >&2
  exit 1
fi

read -r SA_PASSWORD DB_NAME SQL_SERVER < <(python3 - <<'PY' "$SECRETS_PATH"
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

print(sa_password)
print(db)
print(server)
PY
)

if [ -n "$DB_NAME_OVERRIDE" ]; then
  DB_NAME="$DB_NAME_OVERRIDE"
fi

if [ -z "$SA_PASSWORD" ]; then
  echo "SqlServer.SaPassword is missing in $SECRETS_PATH" >&2
  exit 1
fi

if [ -z "$DB_NAME" ]; then
  DB_NAME="athena.net"
fi

if [ -z "$SQL_SERVER" ]; then
  SQL_SERVER="localhost,1433"
fi

if [[ "$SQL_SERVER" == localhost* || "$SQL_SERVER" == 127.0.0.1* ]]; then
  SQL_SERVER="host.docker.internal${SQL_SERVER#localhost}"
  SQL_SERVER="${SQL_SERVER#127.0.0.1}"
  if [[ "$SQL_SERVER" != host.docker.internal* ]]; then
    SQL_SERVER="host.docker.internal,1433"
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

if docker image inspect mcr.microsoft.com/mssql-tools18 >/dev/null 2>&1; then
  TOOLS_IMAGE="mcr.microsoft.com/mssql-tools18"
else
  TOOLS_IMAGE="mcr.microsoft.com/mssql-tools"
fi

docker pull "$TOOLS_IMAGE" >/dev/null

docker run --rm --platform linux/amd64 \
  -e SA_PASSWORD="$SA_PASSWORD" -e SQLCMD_QUERY="$QUERY" -e DB_NAME="$DB_NAME" -e SQL_SERVER="$SQL_SERVER" \
  "$TOOLS_IMAGE" sh -c '
    if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then
      SQLCMD=/opt/mssql-tools18/bin/sqlcmd
    elif [ -x /opt/mssql-tools/bin/sqlcmd ]; then
      SQLCMD=/opt/mssql-tools/bin/sqlcmd
    else
      echo "sqlcmd not found in tools image." >&2
      exit 1
    fi
    "$SQLCMD" -S "$SQL_SERVER" -U sa -P "$SA_PASSWORD" -C -d "$DB_NAME" -Q "$SQLCMD_QUERY"
  '

echo "Seeded login server account for '$LOGIN_USER' in $DB_NAME."
