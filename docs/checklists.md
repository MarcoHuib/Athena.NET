# Checklists

## Parity checklist (LoginServer)
- `conf/login_athena.conf` and `conf/inter_athena.conf` loaded (no default warnings).
- DB schema exists (auto-migrate or pre-created).
- Client login -> char list -> select works with PACKETVER=20220406.
- Duplicate login returns “already online” and triggers kick.
- IP ban + DNSBL behave as expected (optional).
- Usercount colors match thresholds (green/yellow/red/purple).
- Self-test passes.
- Online cleanup runs (unknown char-server sessions are cleared every 10 minutes).

## Ready-to-ship checklist
- `docker compose -f docker-compose.yml build` is clean.
- `./scripts/compose-login-tools.sh up` shows no errors in logs.
- `./scripts/compose-login-tools.sh self-test` passes.
- DB migrations are applied (or `ATHENA_NET_LOGIN_DB_AUTOMIGRATE=true` in compose).
