# Helper Scripts

- `scripts/seed-login-server-account.sh` (uses `ConnectionStrings__LoginDb` or `ATHENA_NET_LOGIN_DB_CONNECTION`)
- `scripts/create-player-account.sh <username> <password> [M|F]` creates a normal player account. It reads the SQL password from `SA_PASSWORD`, the root `.env`, or `solutionfiles/secrets/secret.json`, in that order.
- `src/LoginServer/scripts/migrations-init.sh`
- `src/LoginServer/scripts/migrations-update.sh`
