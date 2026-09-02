# Helper Scripts

- `scripts/seed-login-server-account.sh` (uses `ConnectionStrings__LoginDb` or `ATHENA_NET_LOGIN_DB_CONNECTION`)
- `scripts/create-player-account.sh <username> <password> [M|F]` creates a normal player account. It reads the SQL password from `SA_PASSWORD`, the root `.env`, or `solutionfiles/secrets/secret.json`, in that order.

Both account scripts target standard SQL Server connection strings and use Microsoft's `mssql-tools` image. When the Compose SQL resource is running, they locate it by the stable Compose `sql` service identity rather than by database image name.

On Apple Silicon the tools image, like the SQL Server 2025 Linux image, runs as `linux/amd64` through Docker emulation.
- `src/LoginServer/scripts/migrations-init.sh`
- `src/LoginServer/scripts/migrations-update.sh`
