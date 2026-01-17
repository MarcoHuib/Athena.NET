<a id="readme-top"></a>

[![License][license-shield]][license-url]
[![Issues][issues-shield]][issues-url]
[![Stars][stars-shield]][stars-url]

<br />
<div align="center">
  <h3 align="center">Athena.NET</h3>
  <p align="center">
    A modern C# reimplementation of the rAthena server stack.
    <br />
    <a href="docs/"><strong>Explore the docs »</strong></a>
    <br />
    <br />
    <a href="docs/installation.md">Installation</a>
    &middot;
    <a href="docs/configuration.md">Configuration</a>
    &middot;
    <a href="docs/docker-compose.md">Docker compose</a>
  </p>
</div>

<details>
  <summary>Table of Contents</summary>
  <ol>
    <li><a href="#about">About</a></li>
    <li><a href="#status">Status</a></li>
    <li><a href="#quick-start">Quick Start</a></li>
    <li><a href="#docs">Documentation</a></li>
    <li><a href="#roadmap">Roadmap</a></li>
  </ol>
</details>

## About
Athena.NET is a clean, cross-platform C# rewrite of rAthena, focused on correctness, parity, and fast iteration.
The current milestone is a fully compatible LoginServer; CharServer and MapServer will follow as the migration progresses.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Status
- LoginServer: functional and actively aligned with legacy behavior.
- CharServer/MapServer: planned.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Quick Start
- [Install prerequisites and secrets](docs/installation.md)
- [Configure runtime settings](docs/configuration.md)
- [Run locally](docs/run-locally.md)
- [Run with Docker compose](docs/docker-compose.md)
- [Migrations](docs/migrations.md)

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Docs
- [Installation](docs/installation.md)
- [Configuration](docs/configuration.md)
- [Run locally](docs/run-locally.md)
- [Docker compose](docs/docker-compose.md)
- [Migrations](docs/migrations.md)
- [Checklists](docs/checklists.md)
- [Helper scripts](docs/scripts.md)

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Roadmap
- [x] LoginServer login flow parity and SQL Server support
- [x] Legacy config aliases and `import:` support
- [x] `login_msg.conf` message catalog support
- [ ] CharServer migration
- [ ] MapServer migration

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- MARKDOWN LINKS & IMAGES -->
[license-shield]: https://img.shields.io/github/license/marco/athena.net?style=for-the-badge
[license-url]: LICENSE
[issues-shield]: https://img.shields.io/github/issues/marco/athena.net?style=for-the-badge
[issues-url]: https://github.com/marco/athena.net/issues
[stars-shield]: https://img.shields.io/github/stars/marco/athena.net?style=for-the-badge
[stars-url]: https://github.com/marco/athena.net/stargazers
