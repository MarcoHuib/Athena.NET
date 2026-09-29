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

## Local two-MapServer topology (item 14)

`dotnet run --project src/AppHost` starts **two** MapServer gateway resources by default:
`map-server` on port 5121 and `map-server-b` on port 5122, both against the same Orleans World
cluster, the same CharServer, and the same SQL data - `map-server-b` passes `--map-port 5122` (a
new CLI override on `map_athena.conf`'s own `map_port` key) so both processes can share one config
file. This exercises the cross-replica player visibility/combat fanout described in
`docs/orleans-world-runtime.md`.

The Ragexe launcher (`tools/launcher/`) gets an opt-in `MapTargetPorts` list in its own config
(`LauncherOptions`), round-robin-assigning each new inbound map connection across the listed backend
ports (`TcpProxy.TunnelAsync`). The default single-`MapTargetPort` behavior is unchanged when this
list is not configured.

### Manual acceptance procedure

1. Start Aspire (`dotnet run --project src/AppHost`) and confirm both `map-server` and `map-server-b`
   register against the same World/CharServer in the Aspire dashboard.
2. Configure the launcher's `MapTargetPorts: [5121, 5122]` so the first and second Ragexe client
   connections land on different backend MapServer processes.
3. Start Ragexe client A; confirm (via MapServer log output, which logs its own bound port at
   startup) it landed on `map-server` (5121).
4. Start Ragexe client B; confirm it landed on `map-server-b` (5122).
5. Move both characters onto the same canonical map, within AOI of each other.
6. Verify both clients see each other spawn.
7. Move client A; verify client B sees the movement.
8. Move one client out of and back into AOI range; verify exactly one vanish and one re-entry.
9. Attack a monster with client A; verify client B sees the combat animation/action.
10. Kill the monster with client A; verify client B sees the killing action before the death vanish.
11. Disconnect client A; verify client B sees the player vanish.
12. Confirm neither client ever receives a duplicate actor spawn or combat action.

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
