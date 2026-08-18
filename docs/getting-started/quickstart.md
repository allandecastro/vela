# Quickstart

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

What you get from `vela init`: a solution that references `Vela.Plugins` as a NuGet package, with seeded
plugins, Custom APIs, services and a no-connection test project. From there:

- **Write your own code** - plugins, Custom APIs, DI, caching.
- **Test without a connection** - `Vela.Testing`.
- **Ship** - `dotnet pack` → `vela plan` → `vela apply --execute`.

> [!NOTE]
> Prefer to add Vela to an **existing** plugin project? Reference the package directly instead of
> scaffolding: `dotnet add src/Zava.Plugins package Vela.Plugins`. The plugin package project
> (a `pac plugin init` project) references your domain/model projects, which in turn use the framework
> abstractions through the NuGet dependency.

Next: **[Initialize a solution](initialize-a-solution.md)**.
