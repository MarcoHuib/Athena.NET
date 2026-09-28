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

## Inter-server service tokens
Interserver authentication uses two independent HMAC-SHA256 challenge/response
handshakes, each over its own shared `ServiceToken` - there is no database row
to seed for either, and no username/password interserver credential exists
anywhere in this stack:

- CharServer -> LoginServer: `ServiceAuthentication.CharServer.Token`
  (see `ai/login-server.md`, "Inter-server service authentication").
- MapServer -> CharServer: `ServiceAuthentication.MapServer.Token`
  (see `ai/char-server.md`, "Inter-server service authentication (MapServer)").

Both tokens must be Base64-encoded and decode to at least 32 bytes (256 bits):

```sh
openssl rand -base64 32
```

Generate a **separate** random value for each token - they protect two
different trust boundaries and must never be equal in production. Configure
them in `solutionfiles/secrets/secret.json`:

```json
{
  "ServiceAuthentication": {
    "CharServer": { "Token": "..." },
    "MapServer": { "Token": "..." }
  }
}
```

or via the `ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN` /
`ATHENA_NET_MAP_SERVER_SERVICE_TOKEN` environment variables, which take
priority over the secrets file when set.
