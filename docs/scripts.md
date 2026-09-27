# Helper Scripts

- `scripts/create-player-account.sh <username> [M|F] [email]` creates a normal player account. The password is never a positional argument - it is read from the hidden `Password:` prompt, or from stdin for non-interactive automation. It reads the SQL password from `SA_PASSWORD`, the root `.env`, or `solutionfiles/secrets/secret.json`, in that order.
- CharServer's inter-server `ServiceToken` (HMAC-SHA256 service authentication with LoginServer) is configured, not seeded - see `ai/login-server.md` ("Inter-server service authentication").

Both account scripts target standard SQL Server connection strings and use Microsoft's `mssql-tools` image. When the Compose SQL resource is running, they locate it by the stable Compose `sql` service identity rather than by database image name.

On Apple Silicon the tools image, like the SQL Server 2025 Linux image, runs as `linux/amd64` through Docker emulation.
- `src/LoginServer/scripts/migrations-init.sh`
- `src/LoginServer/scripts/migrations-update.sh`
