# Docker Compose

Compose files live in repo root (`docker-compose.sql-edge.yml` and `docker-compose.yml`).

Start SQL Edge:
```
chmod +x scripts/compose-sql-edge.sh
./scripts/compose-sql-edge.sh
```

Create the database (one time):
```
./scripts/create-sql-edge-db.sh
```

Run login server + SQL Edge:
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

Notes
- Default compose build image for .NET is `mcr.microsoft.com/dotnet/sdk:10.0-preview` (override with `DOTNET_IMAGE`).
- After code changes, rebuild the image:
```
docker compose -f docker-compose.yml build
```
- Compose passes DB settings via env vars:
  - `ATHENA_NET_LOGIN_DB_PROVIDER`
  - `ATHENA_NET_LOGIN_DB_CONNECTION`
- Compose mounts `conf/` into the container as `/app/conf` (read-only).
- Create the login server DB account (required for char-server handshake):
```
chmod +x scripts/seed-login-server-account.sh
./scripts/seed-login-server-account.sh
```
  - Defaults to `s1`/`p1` and account_id `1`. Override: `./scripts/seed-login-server-account.sh <secrets> <container> <user> <pass> <account_id>`.
