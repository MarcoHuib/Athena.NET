# Run Locally

From repo root:
```
dotnet run --project src/LoginServer
```

From `src/LoginServer`:
```
dotnet run -- --secrets ../../solutionfiles/secrets/secret.json
```

Auto-migrate schema at startup (dev only):
```
dotnet run --project src/LoginServer -- --auto-migrate
```

Or via env var:
```
export ATHENA_NET_LOGIN_DB_AUTOMIGRATE=true
dotnet run --project src/LoginServer
```
