Azure SQL Edge (arm64)

This project can use Azure SQL Edge as the SQL Server backend on arm64.
It uses the same EF Core provider as SQL Server (`Microsoft.EntityFrameworkCore.SqlServer`).

Requirements
- Docker installed and running.
- `athenadotnet/SolutionFiles/Secrets/secret.json` with `SqlServer.SaPassword`.

Quick start
1) Run via docker compose (recommended):
   `./athenadotnet/src/LoginServer/scripts/compose-sql-edge.sh`

2) Create the database (if needed):
   `./athenadotnet/src/LoginServer/scripts/create-sql-edge-db.sh`

Login server via compose
- Run both SQL Edge and the login server in one compose:
  `./athenadotnet/src/LoginServer/scripts/compose-login.sh`

Notes
- The login server reads `athenadotnet/SolutionFiles/Secrets/secret.json` by default.
- Connection string should include `Encrypt=True;TrustServerCertificate=True`.
- If the Azure SQL Edge image lacks `sqlcmd`, the helper script will run a temporary
  `mssql-tools` container to create the database.
