#!/usr/bin/env bash
set -euo pipefail

PROJECT="${1:-athenadotnet/src/LoginServer/LoginServer.csproj}"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found." >&2
  exit 1
fi

if ! dotnet tool list --global | grep -q '^dotnet-ef'; then
  echo "dotnet-ef not installed. Install with:" >&2
  echo "  dotnet tool install --global dotnet-ef" >&2
  exit 1
fi

dotnet ef database update --project "$PROJECT"
