# .NET Aspire

This project uses a .NET Aspire AppHost instead of Docker Compose.

## Prerequisites
- .NET SDK 10.x installed

## Run
From repo root:
```
dotnet run --project src/AppHost
```

SQL Server 2025 Developer is exposed on a fixed host port (58043) for local tooling. The image is pinned to `mcr.microsoft.com/mssql/server:2025-CU8-ubuntu-24.04`.

## Secrets
The AppHost reads the SQL Server SA password from `solutionfiles/secrets/secret.json`
(`SqlServer.SaPassword`). You can still override it via environment variable:
```
export Parameters__sql-server-password="<your password>"
```

`Parameters__sql-edge-password` remains a temporary compatibility alias for existing local environments. New configuration should use the engine-neutral name.

The LoginServer consumes the connection string from Aspire via
`ConnectionStrings__LoginDb`. The CharServer uses `ConnectionStrings__CharDb`.

Aspire uses a new `athena-sql-server-2025` data volume. It never mounts the retired Azure SQL Edge volume; see [SQL Server development database](sql-server-development.md) for data and Apple Silicon guidance.

## Seed login server account (optional)
If you need server credentials for the char/map handshake:
```
./scripts/seed-login-server-account.sh
```
