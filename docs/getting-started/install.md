---
_description: "Install the vela CLI and the prerequisites: the .NET 10 SDK, Mono for .NET Framework tests outside Windows, an existing unmanaged solution"
---

# Install

This page installs the `vela` CLI and lists what you need to build, test and deploy a Vela plugin. `Vela.Cli` is a
.NET global tool that exposes the **`vela`** command. The libraries (`Vela.Plugins`, `Vela.Testing`) are NuGet
packages that `vela init` references for you.

## Prerequisites

- **.NET 10 SDK.** The CLI is a `net10.0` tool. The same SDK builds the `net48` plugin projects on any OS, because
  it brings the .NET Framework reference assemblies.
- **Mono, to run plugin tests outside Windows.** Plugins and their `Vela.Testing` tests target `net48`, and
  `dotnet test` runs a .NET Framework test project on Windows only. On macOS and Linux, install Mono
  (`brew install mono`, or your distribution's `mono-complete`), add the `xunit.runner.console` package to the test
  project, and run the built test assembly with it:
  `mono ~/.nuget/packages/xunit.runner.console/<version>/tools/net462/xunit.console.exe <tests>.dll`.
  Building needs no Mono.
- **A Dataverse solution and its publisher.** `vela plan` and `vela apply --solution <uniqueName>` deploy into an
  existing unmanaged solution and take the plugin package's required publisher prefix from it. Create the solution
  first (see [Create a solution](https://learn.microsoft.com/power-apps/maker/data-platform/create-solution) on
  Microsoft Learn). `vela solution show <uniqueName>` prints its prefix.

## Install from nuget.org

```bash
dotnet tool install --global Vela.Cli
```

To add the libraries to an existing plugin project and its test project instead of scaffolding a new solution:

```bash
dotnet add package Vela.Plugins
dotnet add package Vela.Testing
```

Behind a corporate proxy, see [Behind a proxy](../guides/cli-overview.md#behind-a-proxy): `vela` reads
`HTTPS_PROXY` and `NO_PROXY` (or the system settings), not a `.config` file.

## Install from a locally built package (contributors)

```bash
dotnet pack ./src/Vela.Cli -c Release -o ./artifacts/cli
dotnet tool install --global --add-source ./artifacts/cli Vela.Cli
```

## Verify and update

```bash
vela version
```

`vela update` updates the tool to the latest version (`--version <x.y.z>` installs a given one). It runs
`dotnet tool update --global Vela.Cli`, which you can also run yourself. On Windows, run it from a new shell if the
update fails because the running executable is locked.

## Next steps

- **[Quickstart](quickstart.md)**: from an empty folder to a tested, registered plugin in five steps.
- **[The vela CLI](../guides/cli-overview.md)**: commands, exit codes and secret handling, with the
  [CLI cheat sheet](../reference/cli-cheatsheet.md).
