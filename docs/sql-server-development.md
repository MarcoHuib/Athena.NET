# SQL Server development database

Athena.NET uses SQL Server for LoginDb and CharDb. Local orchestration pins SQL Server 2025 Developer to:

`mcr.microsoft.com/mssql/server:2025-CU8-ubuntu-24.04`

The application continues to use `Microsoft.EntityFrameworkCore.SqlServer`, ordinary SQL Server connection strings, and the existing EF Core migrations.

## Aspire

From the repository root:

```sh
dotnet run --project src/AppHost
```

Aspire creates separate `LoginDb` and `CharDb` databases, injects their connection strings into the corresponding services, and stores database files in the `athena-sql-server-2025` volume.

## Docker Compose

Copy `docs/.env.example` to `docs/.env`, set `SA_PASSWORD`, and run:

```sh
docker compose --env-file docs/.env up --build
```

Compose maps the host-side `SA_PASSWORD` value to the container's required `MSSQL_SA_PASSWORD` environment variable.

## Existing Azure SQL Edge data

The old Edge volume is deliberately not mounted by SQL Server 2025:

```text
old Edge volume != new SQL Server volume
```

Local development data is treated as disposable. The fresh volume is initialized by the existing EF migrations, after which the LoginServer service account and any player accounts can be seeded again.

If old data must be retained, back it up or export it from Azure SQL Edge and restore/import it into SQL Server 2025 as a separate, manually verified operation. Do not attach or copy Edge physical database files into the new volume.

## Apple Silicon

SQL Server 2025 Linux containers are x86-64. Docker Desktop can run this image on Apple Silicon through Rosetta/QEMU-style emulation, which can be a practical development convenience but is not a Microsoft-supported SQL Server host configuration.

The supported alternatives on ARM64 macOS are an external SQL Server running on a supported x86-64 host or an appropriate Azure SQL development database. Configure either through the existing SQL Server connection-string settings. Athena.NET does not introduce another database provider for macOS.
