#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ] || [ "$#" -gt 3 ]; then
  echo "Usage: $0 <username> [M|F] [email]" >&2
  echo "Password is never a command-line argument (shell history / 'ps' would expose it)." >&2
  echo "  - Piped:       echo 'mypassword' | $0 <username> [M|F] [email]" >&2
  echo "  - Interactive: $0 <username> [M|F] [email]   (you will be prompted, input hidden)" >&2
  exit 1
fi

LOGIN_USER="$1"
LOGIN_SEX="${2:-M}"
LOGIN_SEX="$(printf '%s' "$LOGIN_SEX" | tr '[:lower:]' '[:upper:]')"
LOGIN_EMAIL="${3:-${LOGIN_USER}@players.athena.local}"

if [ "${#LOGIN_USER}" -lt 4 ] || [ "${#LOGIN_USER}" -gt 23 ]; then
  echo "Username must contain between 4 and 23 characters." >&2
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

# Read the password ourselves (hidden prompt if this is an interactive
# terminal, otherwise the first line of stdin) rather than accepting it as a
# positional argument, so it never ends up in shell history.
if [ -t 0 ]; then
  read -r -s -p "Password: " LOGIN_PASS
  echo >&2
else
  IFS= read -r LOGIN_PASS
fi

if [ -z "$LOGIN_PASS" ]; then
  echo "Password must not be empty." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Player accounts are ASP.NET Core Identity users (password hashing is Identity's
# PBKDF2-based scheme, not something this script can reproduce with plain SQL).
# This runs LoginServer's own one-shot account-creation mode, which uses the same
# configuration/connection-string resolution (secrets.json, conf/inter_athena.conf,
# ATHENA_NET_LOGIN_DB_CONNECTION) as the running server, so credentials are never
# duplicated here. The password is piped to the child process's stdin, never
# passed as a --create-account-password argument, so it never appears in that
# process's argument list (e.g. "ps aux") either.
printf '%s\n' "$LOGIN_PASS" | dotnet run --project "$REPO_ROOT/src/LoginServer" --configuration Release -- \
  --create-account-username "$LOGIN_USER" \
  --create-account-sex "$LOGIN_SEX" \
  --create-account-email "$LOGIN_EMAIL"
