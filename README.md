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
  <img src="https://img.shields.io/badge/.NET_Framework_4.6.2--4.8-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET Framework 4.6.2-4.8" />
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
- **Analyzers, code fixes and source generators** - bundled Roslyn analyzers (VELA001-VELA027) catch invalid step/column usage and plugin pitfalls at build time, several with an IDE code fix, and a source generator emits typed image projections and typed step contexts (`ctx.PreImage` / `ctx.PostImage`). Shipped inside `Vela.Plugins` - no extra reference or setup.
- **`vela` CLI** - preview-first `plan` → `apply` of registrations, scaffolding (`vela init` / `vela add`), early-bound generation (`vela models`), and package signing.
- **Idempotent apply** - safe upsert/delete of steps, Custom APIs, and images against a live environment.
- **No-connection testing** - run plugins against an in-memory Dataverse with no live connection and no mocking-library dependency, with an **opt-in security model** (roles, privileges, business units, teams, sharing and relationship cascades) when you need it.
- **Client-side (preview, work in progress)** - TypeScript FormModel generation (`vela models --forms --lang ts`), the opt-in web resources project scaffold (`vela init --web-resources`) and web resource deploy (`vela plan`/`apply --webresources`) for the `vela-client` runtime (a separate library). These are new and may change.

## Packages

| Package | Target | What it gives you |
| --- | --- | --- |
| `Vela.Plugins` | .NET Framework 4.6.2 | Runtime: fluent plugin/Custom API base types, typed context helpers, lightweight DI and caching. Bundles the Roslyn analyzers (VELA001-VELA027), their code fixes and source generators (typed image projections and step contexts). |
| `Vela.Messages` | .NET Framework 4.6.2, .NET 10.0 | Typed request/response base classes (`TypedRequest`, `CustomApiRequest`); a dependency of `Vela.Plugins`, usable alone from client code. |
| `Vela.Cli` (command `vela`) | .NET 10.0 | Plan/apply, scaffolding and package signing for plugins and Custom APIs. |
| `Vela.Testing` | .NET Framework 4.6.2 | Test harness: fake execution context + in-memory `IOrganizationService`, fluent strongly-typed builder. |

The libraries target .NET Framework 4.6.2 so any supported plug-in project can reference them. Microsoft supports
plug-ins on 4.6.2 through 4.8 and recommends 4.8 (4.8.1 is not supported); `vela init` scaffolds plugin projects on
`net48`. See [Target framework](docs/guides/migrating.md#before-you-start) for the plugin-package caveat.

> **Status** - Pre-1.0 and actively developed, published on NuGet.org. Reference `Vela.Plugins` (and `Vela.Testing` in your test project) with `dotnet add package`, and install the CLI with `dotnet tool install --global Vela.Cli`. Releases run over two lines: a `framework-vX.Y.Z` tag publishes `Vela.Messages` + `Vela.Plugins` + `Vela.Testing`, and `cli-vX.Y.Z` publishes `Vela.Cli`. See [`CHANGELOG.md`](CHANGELOG.md) for what's shipped and the versioning policy.

## Quickstart

```bash
dotnet tool install --global Vela.Cli
vela init --name ZavaSales --prefix Zava --publisher-prefix zava --solution ZavaSales && cd ZavaSales   # minimal; --samples adds examples
vela models --entities account --out src/Zava.Shared/Models/Generated --namespace Zava.Shared.Models.Generated --interactive
vela add plugin AccountValidator --entity Account --filtering-attributes Name --project src/Zava.Plugins
dotnet build ZavaSales.sln -c Release                                   # packs the plugin package into artifacts/
vela plan  --packages-dir artifacts --solution ZavaSales --interactive  # read-only preview
vela apply --packages-dir artifacts --solution ZavaSales --interactive  # asks before writing
```

`--solution` (required by `plan` and `apply`) names an existing Dataverse solution the components are added to.
`--publisher-prefix` names the package `zava_Zava.Plugins`, as Dataverse requires; without it, `--solution` gives the
package that prefix. `vela init` records both in the project's `PROJECT.md`, which the CLI never reads. The
[quickstart](https://allandecastro.github.io/vela/getting-started/quickstart.html) explains each step, and the
**[documentation](https://allandecastro.github.io/vela/)** covers plugins, Custom APIs, testing with
`Vela.Testing` and the `plan` / `apply` workflow. AI coding agents: start with [`AGENTS.md`](AGENTS.md) in this
repository, or [`docs/llms.txt`](docs/llms.txt).

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
