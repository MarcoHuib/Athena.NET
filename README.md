<a id="readme-top"></a>

<div align="center">

  <img src="docs/assets/logo.png" alt="Athena.NET Logo" width="520">

  <h1 align="center">Athena.NET</h1>

  <p align="center">
    <strong>A modern Ragnarok Online server written in C#/.NET for the official, unmodified International Ragnarok Online client.</strong>
    <br />
    <br />
    <a href="#getting-started"><strong>Get started »</strong></a>
    &nbsp;&nbsp;·&nbsp;&nbsp;
    <a href="#project-status">Project status</a>
    &nbsp;&nbsp;·&nbsp;&nbsp;
    <a href="#roadmap">Roadmap</a>
    &nbsp;&nbsp;·&nbsp;&nbsp;
    <a href="#documentation">Documentation</a>
  </p>

  [![License][license-shield]][license-url]
  [![Issues][issues-shield]][issues-url]
  [![Stars][stars-shield]][stars-url]
  [![.NET][dotnet-shield]][dotnet-url]

</div>

---

## CI

- [![Login Server CI][login-ci-shield]][login-ci-url]
- [![Char Server CI][char-ci-shield]][char-ci-url]
- [![Map Server CI][map-ci-shield]][map-ci-url]
- [![World Data Importer CI][world-data-importer-ci-shield]][world-data-importer-ci-url]

<details>
  <summary><strong>Table of Contents</strong></summary>
  <ol>
    <li><a href="#about">About</a></li>
    <li><a href="#why-athenanet">Why Athena.NET?</a></li>
    <li><a href="#project-status">Project status</a></li>
    <li><a href="#getting-started">Getting started</a></li>
    <li><a href="#roadmap">Roadmap</a></li>
    <li><a href="#architecture-direction">Architecture direction</a></li>
    <li><a href="#documentation">Documentation</a></li>
    <li><a href="#reference-projects">Reference projects</a></li>
    <li><a href="#project-scope">Project scope</a></li>
  </ol>
</details>

---

## About

**Athena.NET** is a new Ragnarok Online server implementation built from the ground up in modern C#/.NET.

The project focuses exclusively on compatibility with the official **International Ragnarok Online (iRO)** client. The goal is simple: run the stock, unmodified client against a modern, maintainable and cross-platform server stack without requiring a custom Ragexe build or modified game client.

Athena.NET is **not a port of rAthena**. Mature projects such as rAthena and OpenKore are used as research and behavioral references, while Athena.NET implements its own protocol handling, gameplay systems, persistence, world model and infrastructure.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Why Athena.NET?

Ragnarok Online has decades of emulator history. Athena.NET explores what a fresh implementation can look like when modern software-engineering practices are applied from the beginning.

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>🎯 Official iRO first</h3>
      <p>
        Compatibility work is driven by the current official International Ragnarok Online client rather than generic kRO compatibility.
      </p>
    </td>
    <td width="50%" valign="top">
      <h3>⚙️ Modern .NET</h3>
      <p>
        Built around current C#, .NET, async I/O, dependency injection, strongly typed domain code and automated testing.
      </p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🔬 Evidence-driven protocol support</h3>
      <p>
        Verified client captures and proven runtime behavior take precedence over assumptions or blindly copying legacy packet tables.
      </p>
    </td>
    <td width="50%" valign="top">
      <h3>🧱 Clean architecture boundaries</h3>
      <p>
        Protocol, gameplay, persistence, world data and infrastructure are intentionally separated so they can evolve independently.
      </p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🧪 Testable by design</h3>
      <p>
        Packet framing, gameplay rules, persistence and world-data conversion are covered by a growing automated test suite.
      </p>
    </td>
    <td width="50%" valign="top">
      <h3>🌍 Cross-platform</h3>
      <p>
        Designed for modern development and hosting workflows across Windows, macOS and Linux.
      </p>
    </td>
  </tr>
</table>

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Project status

Athena.NET has progressed beyond login/bootstrap experiments and now has a **playable early-game vertical slice using the official stock iRO client**.

### ✅ Working today

| Area | Status |
| --- | --- |
| **LoginServer** | Stock iRO login flow |
| **CharServer** | Character create, load, persist and select |
| **Map handoff** | CharServer → MapServer |
| **World entry** | Stock iRO MapServer authentication and entry |
| **NPCs & scripts** | Initial interactive NPC/script flow |
| **Quests** | Quest state and progression in the current slice |
| **Monsters** | Spawn, visibility, death and respawn foundations |
| **Combat** | Initial Renewal basic combat |
| **Drops & inventory** | Item pickup and inventory flow |
| **Equipment** | Equip/unequip and persistence foundations |
| **Item use** | Initial usable-item flows |
| **Progression** | EXP and Job EXP foundations |
| **Warps** | Initial same-server map/world transitions |

> Athena.NET is already able to behave like a real server to the stock client, but the implemented gameplay currently represents a focused development vertical slice rather than the complete Ragnarok Online world.

> **Athena.NET is under active development and is not yet ready for production use or public-server hosting.**

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Getting started

### Recommended entry points

- [Installation](docs/installation.md)
- [Configuration](docs/configuration.md)
- [Run locally](docs/run-locally.md)
- [Run with .NET Aspire](docs/aspire.md)
- [Run with Docker Compose](docs/docker-compose.md)

### Database

- [Database migrations](docs/migrations.md)
- [SQL Edge](docs/sql-edge.md)

Local development can be run through **.NET Aspire**, which starts Athena.NET services and supporting infrastructure together.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Roadmap

- [x] iRO LoginServer connectivity
- [x] iRO CharServer connectivity
- [x] Character loading and creation
- [x] Character persistence and selection
- [x] Handoff from CharServer to MapServer
- [x] Stock-iRO MapServer authentication and world entry
- [x] Player spawn, status and inventory foundations
- [x] NPC and script interaction vertical slice
- [x] Items, equipment and item-use foundations
- [x] Mobs, basic Renewal combat and drops
- [x] Quest state, EXP and Job EXP progression
- [x] Initial same-server warps and map transitions
- [ ] Reliable movement, collision and pathfinding
- [ ] Basic in-game chat
- [ ] Broader map, world and gameplay-content coverage
- [ ] Broader skills, parties, guilds and storage
- [ ] Stable network timing, reconnect and persistence behavior
- [ ] Administration and operational tooling
- [ ] Production hardening and deployment documentation

Athena.NET deliberately completes the stock-iRO baseline before introducing the later proxy, QUIC, Identity and Orleans architecture.

**[Read the full architecture roadmap »](docs/architecture-roadmap.md)**

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Architecture direction

Athena.NET intentionally starts simple and grows from measured evidence.

### Today

```text
Official iRO client
        ↓
   LoginServer
        ↓
    CharServer
        ↓
    MapServer
        ↓
    SQL Server
```

The current direction is to begin with **one MapServer**, keep supported maps normally loaded and collect real per-map usage and runtime telemetry before introducing lifecycle optimizations.

### Later, based on evidence

Telemetry can guide decisions such as:

- keeping important maps permanently warm;
- lazy-loading genuinely cold maps;
- independently versioning and deploying game content;
- assigning proven high-cost maps to dedicated MapServers.

A normal logical world map is intended to have **one authoritative runtime**. Future scaling should move complete maps to other MapServers rather than silently duplicating the same normal map into separate channels where players cannot see each other.

Static game content is also intended to become independently deployable without placing a remote Content Service in the gameplay hot path.

**[Read the game content and map lifecycle architecture »](docs/game-content-map-lifecycle-architecture.md)**

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Documentation

<table>
  <tr>
    <td width="33%" valign="top">
      <h3>🚀 Getting started</h3>
      <ul>
        <li><a href="docs/installation.md">Installation</a></li>
        <li><a href="docs/configuration.md">Configuration</a></li>
        <li><a href="docs/run-locally.md">Run locally</a></li>
        <li><a href="docs/aspire.md">.NET Aspire</a></li>
        <li><a href="docs/docker-compose.md">Docker Compose</a></li>
        <li><a href="docs/migrations.md">Migrations</a></li>
      </ul>
    </td>
    <td width="33%" valign="top">
      <h3>🏗 Architecture</h3>
      <ul>
        <li><a href="docs/architecture-roadmap.md">Architecture roadmap</a></li>
        <li><a href="docs/game-content-map-lifecycle-architecture.md">Game content &amp; map lifecycle</a></li>
        <li><a href="docs/client-gateway-architecture.md">Client / Gateway architecture</a></li>
        <li><a href="docs/orleans-game-engine-architecture.md">Orleans game engine</a></li>
      </ul>
    </td>
    <td width="33%" valign="top">
      <h3>🛠 Development</h3>
      <ul>
        <li><a href="tools/WorldDataImporter/README.md">World-data importer</a></li>
        <li><a href="docs/checklists.md">Development checklists</a></li>
        <li><a href="docs/scripts.md">Helper scripts</a></li>
      </ul>
    </td>
  </tr>
</table>

Developer-facing protocol evidence and implementation notes live under `ai/`. Those files are intentionally more detailed and evidence-oriented than this public project overview.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Reference projects

Athena.NET keeps two mature Ragnarok Online projects under `legacy/` as development references:

- **`legacy/rathena/`** — reference for gameplay mechanics, world data, server behavior, scripts, items, monsters and tooling.
- **`legacy/openkore/`** — reference for client/server behavior and protocol research.

These are **reference projects only**.

Athena.NET does not aim to be a generic kRO/rAthena-compatible server. The supported client target is the official International Ragnarok Online client.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

## Project scope

Athena.NET is an unofficial server implementation and is not affiliated with, endorsed by, or sponsored by **Gravity Co., Ltd.** or **WarpPortal**.

Ragnarok Online and related names and assets are the property of their respective owners.

Athena.NET is distributed under the terms described in [LICENSE](LICENSE).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

---

<div align="center">

**Athena.NET**

Modern .NET architecture. Official iRO compatibility. No modified client required.

</div>

<!-- Badge links -->

[license-shield]: https://img.shields.io/github/license/MarcoHuib/Athena.NET?style=for-the-badge
[license-url]: LICENSE

[issues-shield]: https://img.shields.io/github/issues/MarcoHuib/Athena.NET?style=for-the-badge
[issues-url]: https://github.com/MarcoHuib/Athena.NET/issues

[stars-shield]: https://img.shields.io/github/stars/MarcoHuib/Athena.NET?style=for-the-badge
[stars-url]: https://github.com/MarcoHuib/Athena.NET/stargazers

[dotnet-shield]: https://img.shields.io/badge/.NET-modern-512BD4?style=for-the-badge&logo=dotnet&logoColor=white
[dotnet-url]: https://dotnet.microsoft.com/

[login-ci-shield]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/login-server-ci.yml/badge.svg
[login-ci-url]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/login-server-ci.yml

[char-ci-shield]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/char-server-ci.yml/badge.svg
[char-ci-url]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/char-server-ci.yml

[map-ci-shield]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/map-server-ci.yml/badge.svg
[map-ci-url]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/map-server-ci.yml

[world-data-importer-ci-shield]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/world-data-importer-ci.yml/badge.svg
[world-data-importer-ci-url]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/world-data-importer-ci.yml
