# Install

`Vela.Cli` packs as a .NET global tool that exposes the **`vela`** command.

## Prerequisites

- **.NET 10 SDK.** The CLI is a `net10.0` tool, and the SDK also builds the `net48` plugin projects on any OS
  (it brings the .NET Framework reference assemblies).
- **Mono, to run plugin tests outside Windows.** Plugins and their `Vela.Testing` tests target `net48`, and
  `dotnet test` runs a .NET Framework test project on Windows only. On macOS and Linux, install Mono
  (`brew install mono`, or your distribution's `mono-complete`) and run the built test assembly with the xUnit console runner:
  `mono ~/.nuget/packages/xunit.runner.console/<version>/tools/net462/xunit.console.exe <tests>.dll`
  (add the `xunit.runner.console` package to the test project to get it). Building needs no Mono.
- **A Dataverse solution and its publisher.** `vela apply --solution <uniqueName>` deploys into an existing
  solution and takes the package's required publisher prefix from it; create the solution first (in
  the maker portal or with `pac solution`). `vela solution show <uniqueName>` prints its prefix.

## From nuget.org

```bash
dotnet tool install --global Vela.Cli
```

Behind a corporate proxy, see [Behind a proxy](../guides/cli-overview.md#behind-a-proxy): `vela` reads
`HTTPS_PROXY` / `NO_PROXY` (or the system settings), not a `.config` file.

## From a locally built package

```bash
dotnet pack ./src/Vela.Cli -c Release -o ./artifacts/cli
dotnet tool install --global --add-source ./artifacts/cli Vela.Cli
```

Verify the install (update later with `dotnet tool update --global Vela.Cli`):

```bash
vela version
```

For the command reference and secret handling, see [The vela CLI](../guides/cli-overview.md).

## Next steps

- **[Quickstart](quickstart.md)** - from an empty folder to registered, running steps in five commands.
- **[The vela CLI](../guides/cli-overview.md)** - commands, exit codes and the [CLI cheat sheet](../reference/cli-cheatsheet.md).

