# Install

`Vela.Cli` packs as a .NET global tool that exposes the **`vela`** command.

> [!NOTE]
> Vela is pre-1.0 and **not yet on nuget.org**. For now, install from a private feed or from a
> locally built package (see below). The `dotnet tool install` command below is what you will use
> once it is published.

## From your NuGet feed

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

