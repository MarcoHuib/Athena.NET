#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ] || [ "$#" -gt 4 ]; then
  echo "Usage: $0 <username> <password> [M|F] [email]" >&2
  exit 1
fi

LOGIN_USER="$1"
LOGIN_PASS="$2"
LOGIN_SEX="${3:-M}"
LOGIN_SEX="$(printf '%s' "$LOGIN_SEX" | tr '[:lower:]' '[:upper:]')"
LOGIN_EMAIL="${4:-${LOGIN_USER}@players.athena.local}"

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

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Please install the .NET SDK." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Player accounts are ASP.NET Core Identity users (password hashing is Identity's
# PBKDF2-based scheme, not something this script can reproduce with plain SQL).
# This runs LoginServer's own one-shot account-creation mode, which uses the same
# configuration/connection-string resolution (secrets.json, conf/inter_athena.conf,
# ATHENA_NET_LOGIN_DB_CONNECTION) as the running server, so credentials are never
# duplicated here.
dotnet run --project "$REPO_ROOT/src/LoginServer" --configuration Release -- \
  --create-account-username "$LOGIN_USER" \
  --create-account-password "$LOGIN_PASS" \
  --create-account-sex "$LOGIN_SEX" \
  --create-account-email "$LOGIN_EMAIL"
