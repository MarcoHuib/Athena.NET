#!/usr/bin/env bash
set -euo pipefail

PROJECT="src/LoginServer/LoginServer.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found." >&2
  exit 1
fi

if ! dotnet tool list --global | grep -q '^dotnet-ef'; then
  echo "dotnet-ef not installed. Install with:" >&2
  echo "  dotnet tool install --global dotnet-ef" >&2
  exit 1
fi

echo "Adding EF Design package..."
dotnet add "$PROJECT" package Microsoft.EntityFrameworkCore.Design --version 9.0.0

echo "Creating initial migration..."
dotnet ef migrations add Initial --project "$PROJECT"

echo "Done. Apply with: dotnet ef database update --project \"$PROJECT\""
