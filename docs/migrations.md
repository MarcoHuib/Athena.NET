# Migrations

LoginServer has two separate EF Core `DbContext`s with their own migration
histories, and both must be applied to a fresh database:

- `LoginDbContext` — the legacy Ragnarok wire-protocol tables (`login`,
  `ipbanlist`, `loginlog`, account registry, etc.). Migrations live in
  `src/LoginServer/Migrations/`.
- `AthenaIdentityDbContext` — ASP.NET Core Identity's own tables plus
  `AthenaGameAccounts` (see `ai/login-server.md` for the schema). Migrations
  live in `src/LoginServer/Migrations/Identity/`.

Both contexts are configured for SQL Server only (MySQL/Pomelo support was
removed from LoginServer).

## Apply migrations to an existing/fresh database

```bash
dotnet tool install --global dotnet-ef --version 10.0.12  # once, if not already installed
export PATH="$PATH:$HOME/.dotnet/tools"

dotnet ef database update --project src/LoginServer/LoginServer.csproj --context LoginDbContext
dotnet ef database update --project src/LoginServer/LoginServer.csproj --context AthenaIdentityDbContext
```

`./src/LoginServer/scripts/migrations-update.sh` runs both of the above in
sequence against the connection string LoginServer itself resolves
(`secrets.json` / `conf/inter_athena.conf` / `ATHENA_NET_LOGIN_DB_CONNECTION`).
LoginServer also applies any pending migrations for both contexts
automatically on startup (with retry), so this script is only needed to
apply migrations ahead of time or outside of running the server.

## Adding a new migration

```bash
dotnet ef migrations add <Name> --project src/LoginServer/LoginServer.csproj --context LoginDbContext -o Migrations
dotnet ef migrations add <Name> --project src/LoginServer/LoginServer.csproj --context AthenaIdentityDbContext -o Migrations/Identity
```

`./src/LoginServer/scripts/migrations-init.sh` creates the very first
`LoginDbContext` migration; the `AthenaIdentityDbContext` migrations were
generated the same way (see the `dotnet ef migrations add ... --context
AthenaIdentityDbContext -o Migrations/Identity` command above) and already
exist in the repository, so `migrations-init.sh` does not need to be run
again for Identity unless the schema needs a brand-new baseline.
