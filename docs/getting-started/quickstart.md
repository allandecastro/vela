# Quickstart

From an empty folder to registered, running steps in five commands:

```powershell
# 1. Install the CLI (.NET global tool - exposes the `vela` command)
dotnet tool install --global Vela.Cli

# 2. Scaffold a complete, build-ready solution
vela init --name ZavaSales --prefix Zava
cd ZavaSales

# 3. Build the plugin package into a .nupkg (the build packages it into artifacts/; not dotnet pack)
dotnet build ZavaSales.sln -c Release

# 4. Preview what would change in your environment (read-only)
vela plan  --packages-dir artifacts --interactive

# 5. Apply the changes (idempotent create/update/delete) into an existing Dataverse solution.
#    apply writes by default: on a terminal it shows the plan and asks you to confirm (type "yes").
#    Add --auto-approve to skip the prompt in CI / unattended runs.
vela apply --packages-dir artifacts --solution ZavaSales --interactive
```

What you get from `vela init`: a solution that references `Vela.Plugins` as a NuGet package, with seeded
plugins, Custom APIs, services and a no-connection test project. See [Initialize a solution](initialize-a-solution.md)
for the layout and the [guides](../guides/index.md) to write and test your own code.

> [!NOTE]
> Prefer to add Vela to an **existing** plugin project? Reference the package directly instead of
> scaffolding: `dotnet add src/Zava.Plugins package Vela.Plugins`. The plugin package project
> (a `pac plugin init` project) references your domain/model projects, which in turn use the framework
> abstractions through the NuGet dependency.

## Next steps

- **[What `vela init` scaffolds](initialize-a-solution.md)** - the projects you just generated and where to edit.
- **Author** - [write a plugin](../guides/plugins.md) or a [Custom API](../guides/custom-apis.md).
- **Test** - [run it against an in-memory Dataverse](../guides/testing.md), no connection.
- **Deploy** - [plan then apply](../guides/plan-apply.md) into your environment.

