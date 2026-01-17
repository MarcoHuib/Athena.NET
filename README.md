# athena.net C# (athenadotnet)

This folder contains the C# rewrite of athena.net components. The current focus is the LoginServer.

## Secrets
- Create `solutionfiles/secrets/secret.json` (already generated in this repo).
- The file is ignored by git (see `.gitignore`).
- Example structure:
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

## Run locally (macOS/Linux)
From repo root:
```
dotnet run --project src/LoginServer
```

From `src/LoginServer`:
```
dotnet run -- --secrets ../../solutionfiles/secrets/secret.json
```

Subnet (LAN) config:
- Default path: `conf/subnet_athena.conf`
- Override with `--subnet-config <path>`

Auto-migrate schema at startup (dev only):
```
dotnet run --project src/LoginServer -- --auto-migrate
```

Or via env var:
```
export ATHENA_NET_LOGIN_DB_AUTOMIGRATE=true
dotnet run --project src/LoginServer
```

Note: If no EF migrations exist, auto-migrate uses `EnsureCreated` to create tables.

## Migrations (when dotnet-ef is available)
Initialize migrations:
```
chmod +x src/LoginServer/scripts/migrations-init.sh
./src/LoginServer/scripts/migrations-init.sh
```

Then apply:
```
chmod +x src/LoginServer/scripts/migrations-update.sh
./src/LoginServer/scripts/migrations-update.sh
```

## SQL Edge (ARM) via Docker Compose
We use Azure SQL Edge for arm64 support.
Compose files live in repo root (`docker-compose.sql-edge.yml` and `docker-compose.yml`).

1) Start SQL Edge:
```
chmod +x scripts/compose-sql-edge.sh
./scripts/compose-sql-edge.sh
```

2) Create the database (one time):
```
./scripts/create-sql-edge-db.sh
```

## Login Server + SQL Edge via Docker Compose
Run both services together:
```
chmod +x scripts/compose-login.sh
./scripts/compose-login.sh
```

Helper commands (up/down/build/logs):
```
chmod +x scripts/compose-login-tools.sh
./scripts/compose-login-tools.sh up
./scripts/compose-login-tools.sh logs
./scripts/compose-login-tools.sh self-test
```

Notes:
- Default compose build image for .NET is `mcr.microsoft.com/dotnet/sdk:10.0-preview`.
  Override with `DOTNET_IMAGE` if needed (used in the Dockerfile build stage).
- After code changes, rebuild the image:
```
docker compose -f docker-compose.yml build
```
- Compose passes DB settings via env vars:
  - `ATHENA_NET_LOGIN_DB_PROVIDER`
  - `ATHENA_NET_LOGIN_DB_CONNECTION`
- Compose mounts `conf/` into the container as `/app/conf` (read-only), so
  `conf/login_athena.conf` and `conf/inter_athena.conf` are used automatically.

## Parity checklist (LoginServer)
- `conf/login_athena.conf` and `conf/inter_athena.conf` loaded (no default warnings).
- DB schema exists (auto-migrate or pre-created).
- Client login -> char list -> select works with PACKETVER=20220406.
- Duplicate login returns “already online” and triggers kick.
- IP ban + DNSBL behave as expected (optional).
- Usercount colors match thresholds (green/yellow/red/purple).
- Self-test passes.
- Online cleanup runs (unknown char-server sessions are cleared every 10 minutes).

## Ready-to-ship checklist
- `docker compose -f docker-compose.yml build` is clean.
- `./scripts/compose-login-tools.sh up` shows no errors in logs.
- `./scripts/compose-login-tools.sh self-test` passes.
- DB migrations are applied (or `ATHENA_NET_LOGIN_DB_AUTOMIGRATE=true` in compose).

## Helper scripts
  - `scripts/run-sql-edge.sh` (manual run, no compose)
  - `scripts/create-sql-edge-db.sh`
  - `scripts/compose-sql-edge.sh`
  - `scripts/compose-login.sh`
  - `src/LoginServer/scripts/migrations-init.sh`
  - `src/LoginServer/scripts/migrations-update.sh`
