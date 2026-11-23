<p align="center">
  <img src="branding/symbol/vela-chevron.svg" alt="Vela" width="120" height="120" />
</p>

<h1 align="center">Vela</h1>

<p align="center">
  <strong>Build Microsoft Dataverse with confidence: fluent plugins and Custom APIs, a preview-first <code>plan</code> → <code>apply</code> CLI, and a no-connection test harness.</strong>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=for-the-badge" alt="License: MIT" /></a>
  <a href="https://github.com/allandecastro/vela/actions/workflows/ci.yml?query=branch%3Amain"><img src="https://github.com/allandecastro/vela/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI" /></a>
  <a href="https://github.com/allandecastro/vela/actions/workflows/mutation.yml"><img src="https://github.com/allandecastro/vela/actions/workflows/mutation.yml/badge.svg" alt="Mutation Testing" /></a>
  <a href="https://github.com/allandecastro/vela/actions/workflows/release.yml"><img src="https://github.com/allandecastro/vela/actions/workflows/release.yml/badge.svg" alt="Release" /></a>
  <a href="https://www.nuget.org/packages/Vela.Plugins"><img src="https://img.shields.io/nuget/v/Vela.Plugins?logo=nuget&label=Vela.Plugins" alt="Vela.Plugins on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Vela.Testing"><img src="https://img.shields.io/nuget/v/Vela.Testing?logo=nuget&label=Vela.Testing" alt="Vela.Testing on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Vela.Cli"><img src="https://img.shields.io/nuget/v/Vela.Cli?logo=nuget&label=Vela.Cli" alt="Vela.Cli on NuGet" /></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/.NET_Framework_4.6.2--4.8-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET Framework 4.6.2 to 4.8" />
  <img src="https://img.shields.io/badge/.NET_10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Microsoft_Dataverse-107C10?style=for-the-badge&logo=microsoft&logoColor=white" alt="Microsoft Dataverse" />
  <img src="https://img.shields.io/badge/Power_Platform-742774?style=for-the-badge&logo=microsoftpowerplatform&logoColor=white" alt="Power Platform" />
</p>

Vela is a framework (`Vela.Plugins`), a CLI (`Vela.Cli`, command `vela`) and a no-connection test harness
(`Vela.Testing`) for building Microsoft Dataverse plugins and Custom APIs, with a safe, preview-first
`plan` → `apply` workflow.

> **Status:** pre-1.0, published on NuGet.org and actively developed. Until 1.0, a minor version (`0.y`) may contain
> breaking changes; [`CHANGELOG.md`](CHANGELOG.md) calls them out with the migration. The framework packages
> (`Vela.Messages`, `Vela.Plugins`, `Vela.Testing`) are versioned together, the CLI (`Vela.Cli`) on its own.

## 📖 Documentation

Full guides, reference and FAQ live at **[allandecastro.github.io/vela](https://allandecastro.github.io/vela/)**:

- [Getting started](https://allandecastro.github.io/vela/getting-started/install.html): install, quickstart, initialize a solution
- [Guides](https://allandecastro.github.io/vela/guides/): plugins, Custom APIs, dependency injection, built-in services, testing, models, plan and apply, signing
- [Configuration](https://allandecastro.github.io/vela/configuration/): authentication, environments and CI
- [Reference](https://allandecastro.github.io/vela/reference/): CLI cheat sheet, JSON output, analyzers, limitations, FAQ

## Features

- **Fluent base types:** declarative step registration and strongly-typed handlers for plugins and Custom APIs.
- **Typed context helpers:** strongly-typed target and images, framework tracing, lightweight DI, and a TTL cache.
- **Typed reads:** read one record by id (`service.Retrieve<Account>(id, a => a.Name)`), by alternate key, or query by
  criteria (`Query.For<Account>()`) with early-bound column selectors instead of column-name strings.
- **Analyzers, code fixes and source generators:** bundled Roslyn analyzers (VELA001 to VELA028) catch invalid step and column usage and plugin pitfalls at build time, several with an IDE code fix, and a source generator emits typed image projections and typed step contexts (`ctx.PreImage` / `ctx.PostImage`). They ship inside `Vela.Plugins`, with no extra reference or setup.
- **`vela` CLI:** preview-first `plan` → `apply` of registrations, scaffolding (`vela init` / `vela add`), early-bound generation (`vela models`), package signing (`vela sign`)
  and execution logs from the Plug-in Trace Log or failed system jobs (`vela logs`).
- **Idempotent apply:** safe upsert and delete of steps, Custom APIs, and images against a live environment.
- **No-connection testing:** run plugins against an in-memory Dataverse with no live connection and no mocking-library dependency, with an **opt-in security model** (roles, privileges, business units, teams, sharing and relationship cascades) when you need it.

## Packages

| Package | Target | What it gives you |
| --- | --- | --- |
| `Vela.Plugins` | .NET Framework 4.6.2 | Runtime: fluent plugin and Custom API base types, typed context helpers, lightweight DI and caching. Bundles the Roslyn analyzers (VELA001 to VELA028), their code fixes and source generators (typed image projections and step contexts). |
| `Vela.Messages` | .NET Framework 4.6.2, .NET 10.0 | Typed request/response base classes (`TypedRequest`, `CustomApiRequest`); a dependency of `Vela.Plugins`, usable alone from client code. |
| `Vela.Cli` (command `vela`) | .NET 10.0 | Plan and apply, scaffolding, early-bound model generation and package signing for plugins and Custom APIs. |
| `Vela.Testing` | .NET Framework 4.6.2 | Test harness: an in-memory Dataverse and plugin pipeline (`PluginPipeline`). |

The libraries target .NET Framework 4.6.2 so any supported plugin project can reference them (see
[Support for .NET Framework versions](https://learn.microsoft.com/power-apps/developer/data-platform/supported-customizations#support-for-net-framework-versions)
on Microsoft Learn); `vela init` scaffolds plugin projects on `net48`. See [Target framework](https://allandecastro.github.io/vela/guides/migrating.html#before-you-start) for the plugin-package caveat.

## Install

Requires the .NET 10 SDK.

```bash
dotnet tool install --global Vela.Cli   # the vela command
dotnet add package Vela.Plugins         # in a plugin project
dotnet add package Vela.Testing         # in its test project
```

## Quickstart

With a Dataverse environment and an existing solution (here `ZavaSales`, publisher prefix `zava`):

```bash
vela init --name ZavaSales --prefix Zava --publisher-prefix zava --solution ZavaSales && cd ZavaSales   # minimal; --samples adds examples
vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated --interactive
vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins
vela add test AccountValidator --entity Account --project tests/Zava.Plugins.Tests
dotnet build ZavaSales.sln -c Release                                   # packs the plugin package into artifacts/
vela plan  --packages-dir artifacts --solution ZavaSales --interactive  # read-only preview
vela apply --packages-dir artifacts --solution ZavaSales --interactive  # asks before writing
```

`--solution` (required by `plan` and `apply`) names the existing unmanaged Dataverse solution the components are added
to; `plan` and `apply` stop before writing anything when it is managed in the target environment.
`--publisher-prefix` names the package `zava_Zava.Plugins`, as Dataverse requires; without it, `plan` and `apply` add
the solution's publisher prefix. `vela init` records both values in the project's `PROJECT.md` for people and
agents; the CLI never reads it, so the commands take them as flags. The
[quickstart](https://allandecastro.github.io/vela/getting-started/quickstart.html) explains each step, and the
**[documentation](https://allandecastro.github.io/vela/)** covers plugins, Custom APIs, testing with
`Vela.Testing` and the `plan` / `apply` workflow. AI coding agents: a solution from `vela init` carries its own
`AGENTS.md`, and [`docs/llms.txt`](docs/llms.txt) indexes the documentation.

## Limitations

Vela makes deliberate trade-offs (plugin-sandbox safety, a preview-first `plan`/`apply` scope, and a
faithful-but-partial test double). What is intentionally out of scope is collected in the
[Reference → Limitations](https://allandecastro.github.io/vela/reference/limitations.html).

## Contributing

Want to build or contribute to the Vela framework itself (not just use it)? See
[`CONTRIBUTING.md`](CONTRIBUTING.md) for the repository layout, architecture rules, and how to build and
test from source ([`AGENTS.md`](AGENTS.md) for AI coding agents working on this repository). Report a security issue
as described in [`SECURITY.md`](SECURITY.md).

## Acknowledgments

Built on the Microsoft Dataverse SDK (`Microsoft.Xrm.Sdk`, `Microsoft.PowerPlatform.Dataverse.Client`).

---

<p align="center">
  Crafted with ❤️ and AI by Allan De Castro
</p>

<p align="center">
  License: <a href="LICENSE">MIT</a>
</p>
