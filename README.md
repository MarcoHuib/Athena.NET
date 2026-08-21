<a id="readme-top"></a>

[![License][license-shield]][license-url]
[![Issues][issues-shield]][issues-url]
[![Stars][stars-shield]][stars-url]

<div align="center">
  <img src="docs/assets/logo.png" alt="Athena.NET Logo" style="display: block; margin: 0 auto 6px;">
  <h1 align="center" style="margin-top: 0;">Athena.NET</h1>
  <p align="center">
    A modern C# private server implementation focused exclusively on the stock International Ragnarok Online (iRO) client.
    <br />
    Strongly inspired by rAthena's architecture, gameplay systems, data model, and tooling — but with client-facing protocol behavior driven by verified iRO traffic.
    <br />
    <a href="docs/"><strong>Explore the docs »</strong></a>
    <br />
    <br />
    <a href="docs/installation.md">Installation</a>
    &middot;
    <a href="docs/configuration.md">Configuration</a>
    &middot;
    <a href="docs/aspire.md">.NET Aspire</a>
  </p>
</div>

## CI
- [![Login Server CI][login-ci-shield]][login-ci-url]
- [![Char Server CI][char-ci-shield]][char-ci-url]
- [![Map Server CI][map-ci-shield]][map-ci-url]

<details>
  <summary>Table of Contents</summary>
  <ol>
    <li><a href="#about">About</a></li>
    <li><a href="#protocol-strategy">Protocol strategy</a></li>
    <li><a href="#status">Status</a></li>
    <li><a href="#quick-start">Quick Start</a></li>
    <li><a href="#docs">Documentation</a></li>
    <li><a href="#roadmap">Roadmap</a></li>
    <li><a href="#project-scope">Project scope</a></li>
  </ol>
</details>

## About
Athena.NET is a clean, cross-platform C# implementation of a Ragnarok Online server stack with one compatibility target: the current supported **stock iRO client**.

The project borrows heavily from rAthena where that is valuable: Login/Char/Map server separation, database concepts, gameplay rules, data formats, NPC/scripts, tools, and years of server-engineering experience. Athena.NET does **not** treat generic rAthena/kRO packet parity as a goal. Where verified iRO traffic differs, iRO behavior wins.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Protocol strategy
Client-facing protocol work is capture-driven. The evidence priority is:

1. Successful official iRO Wireshark captures for the targeted client generation.
2. Repeatable behavior of the unmodified stock iRO client against Athena.NET.
3. Regression tests derived from verified traffic.
4. rAthena/OpenKore as implementation and structural references.

This prevents regional kRO assumptions from silently becoming iRO behavior.

The target architecture does not require patching `Ragexe.exe`, replacing the official executable, or introducing a custom client protocol. Development network redirection may be used to point official endpoints at local Athena.NET services while leaving the client itself unmodified.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Status
- **LoginServer:** stock-iRO login flow works, including the verified `0x0064` request and `0x0A4D` world-list response.
- **CharServer:** stock-iRO character list, keepalive, create, persistence, select, and `0x0071` MapServer handoff are working in the current test flow.
- **MapServer:** process/inter-server foundation exists; current milestone is decoding and authenticating the stock-iRO `0x0C1F` 1001-byte MapServer entry packet.

See `ai/iro-2026-wire.md` for the current verified wire facts and explicitly disproven assumptions.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Quick Start
- [Install prerequisites and secrets](docs/installation.md)
- [Configure runtime settings](docs/configuration.md)
- [Run locally](docs/run-locally.md)
- [Run with .NET Aspire](docs/aspire.md)
- [Run with Docker Compose](docs/docker-compose.md)
- [Migrations](docs/migrations.md)
- [SQL Edge](docs/sql-edge.md)

Note: SQL credentials live in `solutionfiles/secrets/secret.json`. The AppHost reads this file to keep Aspire and the servers in sync. Aspire uses the persistent SQL Edge volume `athena-sql` to retain local data across restarts.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Docs
- [Installation](docs/installation.md)
- [Configuration](docs/configuration.md)
- [Run locally](docs/run-locally.md)
- [.NET Aspire](docs/aspire.md)
- [Docker Compose](docs/docker-compose.md)
- [Migrations](docs/migrations.md)
- [Checklists](docs/checklists.md)
- [Helper scripts](docs/scripts.md)
- `ai/README.md` - development/agent policy
- `ai/iro-2026-wire.md` - verified stock-iRO protocol knowledge

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Roadmap
- [x] Stock-iRO LoginServer authentication and world handoff
- [x] Stock-iRO CharServer entry and character synchronization
- [x] 175-byte iRO `CHARACTER_INFO` serialization
- [x] Stock-iRO character creation and persistence
- [x] Stock-iRO character select and `0x0071` MapServer handoff
- [ ] Decode and authenticate `0x0C1F` MapServer entry
- [ ] Reach stable first-map spawn with the stock iRO client
- [ ] Initial status/inventory/equipment synchronization
- [ ] Movement and map switching
- [ ] NPC/script interaction
- [ ] Items, skills, mobs, combat, quests, party/guild/storage/chat and other iRO gameplay systems
- [ ] Production hardening, observability, migration tooling, and deployment documentation

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Project scope
Athena.NET is an unofficial server implementation and is not affiliated with, endorsed by, or sponsored by Gravity Co., Ltd. or WarpPortal. Ragnarok Online and related names/assets are trademarks/property of their respective owners.

The repository remains distributed under the license in [LICENSE](LICENSE). Do not modify the GPL license text itself.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- MARKDOWN LINKS & IMAGES -->
[license-shield]: https://img.shields.io/github/license/MarcoHuib/Athena.NET?style=for-the-badge
[license-url]: LICENSE
[issues-shield]: https://img.shields.io/github/issues/MarcoHuib/Athena.NET?style=for-the-badge
[issues-url]: https://github.com/MarcoHuib/Athena.NET/issues
[stars-shield]: https://img.shields.io/github/stars/MarcoHuib/Athena.NET?style=for-the-badge
[stars-url]: https://github.com/MarcoHuib/Athena.NET/stargazers
[login-ci-shield]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/login-server-ci.yml/badge.svg?style=for-the-badge
[login-ci-url]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/login-server-ci.yml
[char-ci-shield]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/char-server-ci.yml/badge.svg?style=for-the-badge
[char-ci-url]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/char-server-ci.yml
[map-ci-shield]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/map-server-ci.yml/badge.svg?style=for-the-badge
[map-ci-url]: https://github.com/MarcoHuib/Athena.NET/actions/workflows/map-server-ci.yml
