# Docker Compose

Create a copy of `docs/.env.example` as `docs/.env` and set `SA_PASSWORD`.

The stack uses SQL Server 2025 Developer pinned to `mcr.microsoft.com/mssql/server:2025-CU8-ubuntu-24.04`. Compose maps `SA_PASSWORD` to the container's `MSSQL_SA_PASSWORD` variable and stores data in the new `sql-server-2025-data` volume.

The retired Azure SQL Edge volume is not reused. Local database state is disposable by default; see [SQL Server development database](sql-server-development.md) before attempting to preserve old data or running on Apple Silicon.

## Production-style stack
From repo root:
```
docker compose --env-file docs/.env -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

Note: `docker-compose.prod.yml` disables auto-migrate. Run migrations manually if needed.

## Local config/secrets mounts
```
docker compose --env-file docs/.env -f docker-compose.yml -f docker-compose.override.yml up -d --build
```
