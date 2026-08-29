# Athena.NET Windows Launcher

The launcher preserves the official iRO updater and Easy Anti-Cheat launch chain while temporarily routing the stock client to Athena.NET. All launcher code, configuration, tests, and documentation live below this directory.

## Architecture

- `Athena.Launcher` is the Windows-only WPF composition/UI executable. Its manifest requests administrator privileges, so users see the normal Windows UAC prompt.
- `Athena.Launcher.Core` owns configuration, installation discovery, updater execution, post-update validation, effective client configuration resolution, lifecycle orchestration, temporary IP ownership, watchdog launch, EAC launch, Ragexe detection, and JSON-lines logging.
- `Athena.Launcher.Networking` owns opaque in-process `TcpListener`/`TcpClient` tunnels. It never reads or interprets Ragnarok packets.
- `Athena.Launcher.Watchdog` only waits for its launcher PID and idempotently removes exact IP aliases listed in that session's validated state file.

The coordinator orders the flow as:

```text
locate -> official updater -> validate -> resolve updated login endpoint
       -> stale-state recovery -> watchdog -> ActiveStore IP aliases
       -> all three proxies -> EAC 1rag1 -> new Ragexe -> wait -> cleanup
```

Any failure after state begins changing calls the same idempotent cleanup path. Proxy-manager startup is transactional: if listener two or three cannot bind, every earlier listener is stopped before the error escapes. Because all sockets belong to `Athena.Launcher.exe`, Windows closes them immediately if the launcher is killed; the watchdog exists only for temporary IP cleanup.

## Endpoints and server integration

`launcher.settings.json` contains the WAN host once (`62.194.40.186`) and the three target ports (`6900`, `6121`, `5121`). The official login listen address and port are not configured permanently: they are read after every update.

Character and Map use RFC 2544 benchmarking addresses, which are reserved and non-public:

| Flow | Client-side listener | Athena.NET target |
|---|---|---|
| Login | updated official endpoint | `AthenaHost:6900` |
| Character | `198.18.0.1:4500` | `AthenaHost:6121` |
| Map | `198.18.0.2:4501` | `AthenaHost:5121` |

LoginServer now reads `ATHENA_NET_LOGIN_IRO_CHAR_IP` and `ATHENA_NET_LOGIN_IRO_CHAR_PORT` for its iRO `0x0A4D` Character advertisement. Defaults remain the previous `128.241.92.43:4500`. CharServer already had `ATHENA_NET_CHAR_IRO_MAP_IP` and `ATHENA_NET_CHAR_IRO_MAP_PORT` for `0x0071`; its previous defaults also remain. `docker-compose.prod.yml` opts production into the reserved launcher addresses.

## Effective client configuration

The resolver requires `data.ini`, validates that every numbered GRF entry exists and is readable, and opens the same ordered collection through the focused MIT-licensed `GRF` 0.3.1 library. That dependency is isolated behind `IClientDataSource`; it was selected because it explicitly implements `data.ini` GRF priority and supports GRF 0x102, 0x103, and 0x200.

A loose `data\sclientinfo.xml` or `data\clientinfo.xml` overrides the archive entry. Within the effective data hierarchy, `data\sclientinfo.xml` is tried before `data\clientinfo.xml`. The first connection's `address` and `port` are parsed without modifying client data. IPv4 literals are accepted directly; hostnames use Windows DNS resolution and must produce an IPv4 address. Invalid XML, hostnames, ports, missing files, unsupported GRFs, or DNS failure abort before network changes.

## Temporary IP ownership and cleanup

The active IPv4 default route selects the adapter. Advanced configuration may specify `NetworkInterfaceIndex` or `NetworkInterfaceAlias`; an override must identify an `Up` adapter. No adapter name such as `Ethernet` is hardcoded.

Before adding an address the launcher writes an atomic, elevated session file under `%PROGRAMDATA%\Athena.NET\Launcher\Sessions`. Keeping ownership state out of the normally user-writable profile prevents an unelevated process from forging stale cleanup instructions. It then invokes `New-NetIPAddress` in-process with `PrefixLength 32`, `SkipAsSource true`, and `PolicyStore ActiveStore`. Cleanup only reads validated Athena session files and only removes their exact interface-index/address pairs. An address already present but not owned by the current session causes startup to fail rather than being claimed or removed.

Normal shutdown removes addresses and deletes the session file. The watchdog repeats the same operation after the parent exits. If watchdog execution is prevented, ActiveStore avoids intentional persistence across restart and the next launcher invocation processes stale Athena-owned session files before creating new state. There is no possible userspace guarantee after catastrophic OS failure; stale recovery is therefore part of the design.

Logs are written to `%LOCALAPPDATA%\Athena.NET\Launcher\Logs` and never include packet payloads or credentials.

## Build and test

From a Windows machine with the .NET 10 SDK:

```powershell
dotnet restore .\tools\launcher\src\Athena.Launcher\Athena.Launcher.csproj
dotnet test .\tools\launcher\tests\Athena.Launcher.Core.Tests\Athena.Launcher.Core.Tests.csproj
dotnet test .\tools\launcher\tests\Athena.Launcher.Networking.Tests\Athena.Launcher.Networking.Tests.csproj
dotnet publish .\tools\launcher\src\Athena.Launcher\Athena.Launcher.csproj -c Release -r win-x64 --self-contained false
```

Set `RagnarokPath` in the published `launcher.settings.json` only if registry/common-path discovery cannot find the client. Set `UpdaterExecutable` to a path relative to the installation only when metadata-based updater discovery is ambiguous. Start `Athena.Launcher.exe`; do not start it from PowerShell.

## Manual Windows acceptance procedure

1. Configure the production LoginServer and CharServer advertisement variables shown above and confirm Athena.NET ports `6900`, `6121`, and `5121` are reachable from Windows.
2. Start the launcher and accept UAC. Confirm the official updater opens and the launcher does not add IP aliases while it is running.
3. Finish/close the updater. Confirm the log records client validation, the selected `data.ini` configuration source, and the current resolved official Login endpoint.
4. In an elevated PowerShell used only for observation, run `Get-NetIPAddress -AddressFamily IPv4` and confirm the resolved Login address plus `198.18.0.1` and `198.18.0.2` are `/32`, ActiveStore aliases on the logged adapter.
5. Confirm the three listeners exist with `Get-NetTCPConnection -State Listen`. Confirm no `netsh interface portproxy` rule was added.
6. Confirm EAC starts with `1rag1`, starts a new Ragexe from the configured installation, and the launcher logs that PID.
7. Log in, select the advertised Character world, select a character, and enter a map. This verifies Login -> Character -> Map routing without launcher packet parsing.
8. Exit Ragexe normally. Confirm all listeners, all three managed aliases, and the watchdog disappear, and cleanup completes in the log. Repeating close/cleanup must remain harmless.
9. Repeat, then terminate `Athena.Launcher.exe` from Task Manager. Confirm listeners disappear immediately and the watchdog removes the aliases before exiting.
10. Repeat and terminate both launcher and watchdog. Start the launcher again. Before it creates a new session, confirm stale-state recovery removes aliases recorded by the prior Athena session.
11. Patch/fixture the effective client configuration with a different valid Login endpoint in a disposable test installation, run again, and confirm the launcher binds the new endpoint without depending on the previous Gravity IP.

## Current limitations

- This repository was developed from macOS and contained no local iRO installation, so updater naming, the current production GRF set, EAC behavior, UAC/network commands, and forced-process cleanup require the Windows acceptance pass above.
- The isolated GRF dependency does not advertise formats newer than 0x200. A future official format change fails before networking and requires replacing/extending `IClientDataSource`.
- If several `<connection>` elements are introduced for region selection, the current iRO assumption is that the first effective connection is the active one; capture/runtime evidence should guide a selector extension.
- A user can close the updater early; post-update validation protects networking, but the launcher cannot prove Gravity completed every semantic patch operation beyond validating the effective client files.

## Developer fallback

`scripts/ragnarok-proxy.ps1` is unchanged and still works independently with its original hardcoded adapter, LAN target, captured Gravity addresses, and `netsh interface portproxy` behavior. It is retained strictly as a developer/debugging fallback and is not invoked by the launcher.
