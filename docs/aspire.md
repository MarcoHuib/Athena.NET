# .NET Aspire

This project uses a .NET Aspire AppHost instead of Docker Compose.

## Prerequisites
- .NET SDK 10.x installed

## Run
From repo root:
```
dotnet run --project src/AppHost
```

SQL Edge is exposed on a fixed host port (58043) for local tooling.

## Secrets
The AppHost reads the SQL Edge SA password from `solutionfiles/secrets/secret.json`
(`SqlServer.SaPassword`). You can still override it via environment variable:
```
export Parameters__sql-edge-password="<your password>"
```

The LoginServer consumes the connection string from Aspire via
`ConnectionStrings__LoginDb`. The CharServer uses `ConnectionStrings__CharDb`.

## Seed login server account (optional)
If you need server credentials for the char/map handshake:
```
./scripts/seed-login-server-account.sh
```
