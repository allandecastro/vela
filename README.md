<p align="center">
  <img src="branding/symbol/vela-chevron.svg" alt="Vela" width="120" height="120" />
</p>

<h1 align="center">Vela</h1>

<p align="center">
  <strong>Build Microsoft Dataverse with confidence: fluent plugins and Custom APIs, a preview-first <code>plan</code> → <code>apply</code> CLI, and a no-connection test harness.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/license-MIT-green?style=for-the-badge" alt="License: MIT" />
  <a href="https://github.com/allandecastro/vela/actions/workflows/ci.yml?query=branch%3Amain"><img src="https://github.com/allandecastro/vela/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI" /></a>
  <a href="https://github.com/allandecastro/vela/actions/workflows/mutation.yml"><img src="https://github.com/allandecastro/vela/actions/workflows/mutation.yml/badge.svg" alt="Mutation Testing" /></a>
  <a href="https://github.com/allandecastro/vela/actions/workflows/release.yml"><img src="https://github.com/allandecastro/vela/actions/workflows/release.yml/badge.svg" alt="Release" /></a>
  <a href="https://www.nuget.org/packages/Vela.Plugins"><img src="https://img.shields.io/nuget/v/Vela.Plugins?logo=nuget&label=Vela.Plugins" alt="Vela.Plugins on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Vela.Testing"><img src="https://img.shields.io/nuget/v/Vela.Testing?logo=nuget&label=Vela.Testing" alt="Vela.Testing on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Vela.Cli"><img src="https://img.shields.io/nuget/v/Vela.Cli?logo=nuget&label=Vela.Cli" alt="Vela.Cli on NuGet" /></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=csharp&logoColor=white" alt="C#" />
  <img src="https://img.shields.io/badge/.NET_Framework_4.6.2-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET Framework 4.6.2" />
  <img src="https://img.shields.io/badge/.NET_10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Microsoft_Dataverse-107C10?style=for-the-badge&logo=microsoft&logoColor=white" alt="Microsoft Dataverse" />
  <img src="https://img.shields.io/badge/Power_Platform-742774?style=for-the-badge&logo=microsoftpowerplatform&logoColor=white" alt="Power Platform" />
</p>

Vela is a framework (`Vela.Plugins`), a CLI (`Vela.Cli`, command `vela`) and a no-connection test harness
(`Vela.Testing`) for building Microsoft Dataverse plugins and Custom APIs, with a safe, preview-first
`plan` → `apply` workflow.

## 📖 Documentation

Full guides, reference and FAQ live at **[allandecastro.github.io/vela](https://allandecastro.github.io/vela/)**:

- [Getting started](https://allandecastro.github.io/vela/getting-started/install.html) - install, quickstart, initialize a solution
- [Guides](https://allandecastro.github.io/vela/guides/) - plugins, Custom APIs, DI, caching, testing, plan/apply, models, signing
- [Reference](https://allandecastro.github.io/vela/reference/limitations.html) - limitations and FAQ

## Features

- **Fluent base types** - declarative step registration and strongly-typed handlers for plugins and Custom APIs.
- **Typed context helpers** - strongly-typed target/images, framework tracing, lightweight DI, and a TTL cache.
- **`vela` CLI** - preview-first `plan` → `apply` of registrations, scaffolding (`vela init` / `vela add`), early-bound generation (`vela models`), and package signing.
- **Idempotent apply** - safe upsert/delete of steps, Custom APIs, and images against a live environment.
- **No-connection testing** - run plugins against an in-memory Dataverse with no live connection and no mocking-library dependency, with an **opt-in security model** (roles, privileges, business units, teams, sharing and relationship cascades) when you need it.
- **Client-side (preview, work in progress)** - TypeScript FormModel generation (`vela models --forms --lang ts`), the opt-in web resources project scaffold (`vela init --web-resources`) and web resource deploy (`vela plan`/`apply --webresources`) for the [vela-client](https://github.com/allandecastro/vela) runtime. These are new and may change.

## Packages

| Package | Target | What it gives you |
| --- | --- | --- |
| `Vela.Plugins` | .NET Framework 4.6.2 | Runtime: fluent plugin/Custom API base types, typed context helpers, lightweight DI and caching. |
| `Vela.Cli` (command `vela`) | .NET 10.0 | Plan/apply, scaffolding and package signing for plugins and Custom APIs. |
| `Vela.Testing` | .NET Framework 4.6.2 | Test harness: fake execution context + in-memory `IOrganizationService`, fluent strongly-typed builder. |

> **Status** - Pre-1.0 and actively developed, published on NuGet.org. Reference `Vela.Plugins` (and `Vela.Testing` in your test project) with `dotnet add package`, and install the CLI with `dotnet tool install --global Vela.Cli`. Releases run over two lines: a `framework-vX.Y.Z` tag publishes `Vela.Plugins` + `Vela.Testing`, and `cli-vX.Y.Z` publishes `Vela.Cli`. See [`CHANGELOG.md`](CHANGELOG.md) for what's shipped and the versioning policy.

## Quickstart

From an empty folder to registered, running steps in five commands:

```powershell
# 1. Install the CLI (.NET global tool - exposes the `vela` command)
dotnet tool install --global Vela.Cli

# 2. Scaffold a complete, build-ready solution
vela init --name ZavaSales --prefix Zava
cd ZavaSales

# 3. Build the plugin package into a .nupkg
dotnet pack src/Zava.Plugins -c Release -o artifacts

# 4. Preview what would change in your environment (read-only)
vela plan  --packages-dir artifacts --interactive

# 5. Apply the changes (idempotent create/update/delete)
vela apply --packages-dir artifacts --interactive --execute
```

Then head to the **[documentation](https://allandecastro.github.io/vela/)** for guides on writing plugins,
Custom APIs, dependency injection, caching, testing with `Vela.Testing`, and the `plan` / `apply` workflow.

## Limitations

Vela makes deliberate trade-offs (plugin-sandbox safety, a preview-first `plan`/`apply` scope, and a
faithful-but-partial test double). What is intentionally out of scope is collected in the
[Reference → Limitations](https://allandecastro.github.io/vela/reference/limitations.html).

## Contributing

Want to build or contribute to the Vela framework itself (not just use it)? See
[`CONTRIBUTING.md`](CONTRIBUTING.md) for the repository layout, architecture rules, and how to build and
test from source.

## Acknowledgments

Built on the Microsoft Dataverse SDK (`Microsoft.Xrm.Sdk`, `Microsoft.PowerPlatform.Dataverse.Client`).

---

<p align="center">
  Crafted with ❤️ and AI by Allan De Castro
</p>

<p align="center">
  License: <a href="LICENSE">MIT</a>
</p>
