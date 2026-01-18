Azure SQL Edge (arm64)

This project can use Azure SQL Edge as the SQL Server backend on arm64.
It uses the same EF Core provider as SQL Server (`Microsoft.EntityFrameworkCore.SqlServer`).

Requirements
- Docker installed and running.
- `solutionfiles/secrets/secret.json` with `SqlServer.SaPassword`.

Quick start
1) Run via docker compose (recommended):
   `./scripts/compose-sql-edge.sh`

2) Create the database (if needed):
   `./scripts/create-sql-edge-db.sh`

Login server via compose
- Run both SQL Edge and the login server in one compose:
  `./scripts/compose-login.sh`
- Seed the login server account (required for char-server handshake):
  `./scripts/seed-login-server-account.sh`

Notes
- The login server reads `solutionfiles/secrets/secret.json` by default.
- Connection string should include `Encrypt=True;TrustServerCertificate=True`.
- If the Azure SQL Edge image lacks `sqlcmd`, the helper script will run a temporary
  `mssql-tools` container to create the database.
