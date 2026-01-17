# rAthena C# (athenadotnet)

This folder contains the C# rewrite of rAthena components. The current focus is the LoginServer.

## Secrets
- Create `athenadotnet/SolutionFiles/Secrets/secret.json` (already generated in this repo).
- The file is ignored by git (see `.gitignore`).
- Example structure:
```json
{
  "LoginDb": {
    "Provider": "sqlserver",
    "ConnectionString": "Server=localhost,1433;Database=rathena;User ID=sa;Password=...;Encrypt=True;TrustServerCertificate=True;"
  },
  "SqlServer": {
    "SaPassword": "..."
  }
}
```

## Run locally (macOS/Linux)
From repo root:
```
dotnet run --project athenadotnet/src/LoginServer
```

From `athenadotnet/src/LoginServer`:
```
dotnet run -- --secrets ../../SolutionFiles/Secrets/secret.json
```

Subnet (LAN) config:
- Default path: `conf/subnet_athena.conf`
- Override with `--subnet-config <path>`

Auto-migrate schema at startup (dev only):
```
dotnet run --project athenadotnet/src/LoginServer -- --auto-migrate
```

Or via env var:
```
export RATHENA_LOGIN_DB_AUTOMIGRATE=true
dotnet run --project athenadotnet/src/LoginServer
```

Note: If no EF migrations exist, auto-migrate uses `EnsureCreated` to create tables.

## Migrations (when dotnet-ef is available)
Initialize migrations:
```
chmod +x athenadotnet/src/LoginServer/scripts/migrations-init.sh
./athenadotnet/src/LoginServer/scripts/migrations-init.sh
```

Then apply:
```
chmod +x athenadotnet/src/LoginServer/scripts/migrations-update.sh
./athenadotnet/src/LoginServer/scripts/migrations-update.sh
```

## SQL Edge (ARM) via Docker Compose
We use Azure SQL Edge for arm64 support.

1) Start SQL Edge:
```
chmod +x athenadotnet/src/LoginServer/scripts/compose-sql-edge.sh
./athenadotnet/src/LoginServer/scripts/compose-sql-edge.sh
```

2) Create the database (one time):
```
./athenadotnet/src/LoginServer/scripts/create-sql-edge-db.sh
```

## Login Server + SQL Edge via Docker Compose
Run both services together:
```
chmod +x athenadotnet/src/LoginServer/scripts/compose-login.sh
./athenadotnet/src/LoginServer/scripts/compose-login.sh
```

Helper commands (up/down/build/logs):
```
chmod +x athenadotnet/src/LoginServer/scripts/compose-login-tools.sh
./athenadotnet/src/LoginServer/scripts/compose-login-tools.sh up
./athenadotnet/src/LoginServer/scripts/compose-login-tools.sh logs
./athenadotnet/src/LoginServer/scripts/compose-login-tools.sh self-test
```

Notes:
- Default compose build image for .NET is `mcr.microsoft.com/dotnet/sdk:10.0-preview`.
  Override with `DOTNET_IMAGE` if needed (used in the Dockerfile build stage).
- After code changes, rebuild the image:
```
docker compose -f athenadotnet/src/LoginServer/docker-compose.login.yml build
```
- Compose passes DB settings via env vars:
  - `RATHENA_LOGIN_DB_PROVIDER`
  - `RATHENA_LOGIN_DB_CONNECTION`
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
- `docker compose -f athenadotnet/src/LoginServer/docker-compose.login.yml build` is clean.
- `./athenadotnet/src/LoginServer/scripts/compose-login-tools.sh up` shows no errors in logs.
- `./athenadotnet/src/LoginServer/scripts/compose-login-tools.sh self-test` passes.
- DB migrations are applied (or `RATHENA_LOGIN_DB_AUTOMIGRATE=true` in compose).

## Helper scripts
- `athenadotnet/src/LoginServer/scripts/run-sql-edge.sh` (manual run, no compose)
- `athenadotnet/src/LoginServer/scripts/create-sql-edge-db.sh`
- `athenadotnet/src/LoginServer/scripts/compose-sql-edge.sh`
- `athenadotnet/src/LoginServer/scripts/compose-login.sh`
- `athenadotnet/src/LoginServer/scripts/migrations-init.sh`
- `athenadotnet/src/LoginServer/scripts/migrations-update.sh`
