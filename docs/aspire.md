# .NET Aspire

This project uses a .NET Aspire AppHost instead of Docker Compose.

## Prerequisites
- .NET SDK 10.x installed

## Run
From repo root:
```
dotnet run --project src/AppHost
```

## Secrets
Aspire will prompt for the SQL Edge password parameter on first run.
You can also set it via environment variable:
```
export sql-edge-password="<your password>"
```

The LoginServer consumes the connection string from Aspire via
`ConnectionStrings__LoginDb`.

## Seed login server account (optional)
If you need the legacy server account (`s1`/`p1`) for char-server handshakes:
```
./scripts/seed-login-server-account.sh
```
