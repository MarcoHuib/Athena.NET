#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Please install the .NET SDK." >&2
  exit 1
fi

if ! dotnet tool list --global | rg -q "^dotnet-ef\\s"; then
  echo "dotnet-ef not found. Install it with:" >&2
  echo "  dotnet tool install --global dotnet-ef" >&2
  exit 1
fi

dotnet ef database update --project src/LoginServer
