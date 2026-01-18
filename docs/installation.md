# Installation

This project expects a local secrets file at `solutionfiles/secrets/secret.json`. The file is already generated in this repo and ignored by git.

Example structure:
```json
{
  "LoginDb": {
    "Provider": "sqlserver",
    "ConnectionString": "Server=localhost,1433;Database=athena.net;User ID=sa;Password=...;Encrypt=True;TrustServerCertificate=True;"
  },
  "SqlServer": {
    "SaPassword": "..."
  }
}
```

## Local development (Aspire)

Use Aspire for local dev when you want the dashboard and managed dependencies.

```sh
dotnet run --project src/AppHost
```

## Production-like (Docker Compose)

Copy the env template and set a strong SA password:

```sh
cp .env.example .env
```

Then start services:

```sh
docker compose up --build
```

For production guidance, see `docs/production.md`.

### Manual migrations (when auto-migrate is disabled)

If you set `ATHENA_NET_LOGIN_DB_AUTOMIGRATE=false`, run migrations manually:

```sh
./scripts/migrate-login-db.sh
```

The script requires the `dotnet-ef` tool:

```sh
dotnet tool install --global dotnet-ef
```
