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

## Git submodules

Athena.NET uses `legacy/rathena` and `legacy/openkore` as **pinned** Git submodules. The Athena.NET repository — not the submodules' own `main` branches — determines exactly which commit of each is used. This is intentional: it keeps the reference code reproducible across clones and over time.

For a new clone, either fetch submodules in one step:

```sh
git clone --recurse-submodules <repository-url>
```

or initialize them after a normal clone:

```sh
git clone <repository-url>
cd Athena.NET
git submodule update --init --recursive
```

For a repository you already have checked out:

```sh
git submodule update --init --recursive
```

Run this again after `git pull` or after switching branches (`git checkout <branch>`) if the pinned submodule commits changed between commits/branches:

```sh
git pull
git submodule update --init --recursive
```

```sh
git checkout <branch>
git submodule update --init --recursive
```

**Do not** run the following as part of normal setup — it replaces the pinned commits with each submodule's latest `main`:

```sh
git submodule update --remote
```

**Do not** manually update the submodules in place either:

```sh
# inside legacy/rathena or legacy/openkore — do not do this
git checkout main
git pull
```

After initialization, `legacy/rathena` and `legacy/openkore` will typically be in a **detached HEAD** state. This is normal and expected for a pinned submodule: it means the submodule is checked out at exactly the commit Athena.NET specifies, not tracking a branch.

Upgrading `legacy/rathena` or `legacy/openkore` to a different commit is a deliberate dependency update, not routine maintenance. It should be done explicitly and recorded as its own Athena.NET commit/PR that updates the submodule's gitlink.

## Local development (Aspire)

Use Aspire for local dev when you want the dashboard and managed dependencies.

```sh
dotnet run --project src/AppHost
```

Aspire runs the pinned SQL Server 2025 Developer container and creates separate LoginDb and CharDb databases. See [SQL Server development database](sql-server-development.md), including the Apple Silicon limitations.

## Production-like (Docker Compose)

Copy the env template and set a strong SA password:

```sh
cp .env.example .env
```

Then start services:

```sh
docker compose up --build
```

The Compose file maps `SA_PASSWORD` from the host environment to SQL Server's required `MSSQL_SA_PASSWORD` container variable.

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
