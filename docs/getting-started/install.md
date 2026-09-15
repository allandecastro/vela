# Install

`Vela.Cli` packs as a .NET global tool that exposes the **`vela`** command.

## From nuget.org

```powershell
dotnet tool install --global Vela.Cli
```

## From a locally built package

```powershell
dotnet pack .\src\Vela.Cli -c Release -o .\artifacts\cli
dotnet tool install --global --add-source .\artifacts\cli Vela.Cli
```

Verify the install (update later with `dotnet tool update --global Vela.Cli`):

```powershell
vela version
```

For the command reference and secret handling, see [The vela CLI](../guides/cli-overview.md).

## Next steps

- **[Quickstart](quickstart.md)** - from an empty folder to registered, running steps in five commands.
- **[The vela CLI](../guides/cli-overview.md)** - commands, exit codes and the [CLI cheat sheet](../reference/cli-cheatsheet.md).

