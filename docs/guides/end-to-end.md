# End-to-end example

A complete loop from plugin code to a registered, running step, using a solution scaffolded by
[`vela init`](scaffold.md).

**1. Write a plugin** in your plugin-package project (`Zava.Plugins`, which references `Vela.Plugins`):

```csharp
using Vela.Context;
using Vela.Constants;
using Vela.Plugin;
using Vela.StepBuilder;
using Zava.Shared.Models;

public class AccountPostUpdate : EntityPluginBase<Account>
{
    public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

    protected override void RegisterPluginSteps()
    {
        RegisterSteps().OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)   // only fire when Name changes
            .PreImage()
                .Attributes(a => a.Name);
    }

    protected override void Execute(ILocalPluginContext ctx, Account target)
    {
        var before = ctx.GetPreImage<Account>();
        ctx.Trace($"Account renamed from '{before.Name}' to '{target.Name}'");
    }
}
```

**2. Build the plugin package** into a `.nupkg` (the Release build packages it into `artifacts/` via
`GeneratePackageOnBuild` + `PackageOutputPath` - use `dotnet build`, not `dotnet pack`, which on a cold tree
emits an assembly-less package):

```powershell
dotnet build .\src\Zava.Plugins -c Release
```

**3. Preview** what would change in the target environment (read-only):

```powershell
vela plan --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive --verbose
```

`plan` prints a preview - e.g. `summary: +1 -0 ~0` plus an ordered `operations` list - and makes no changes.

**4. Apply** the changes (idempotent create/update/delete):

```powershell
vela apply --package .\artifacts\Zava.Plugins.1.0.0.nupkg --solution ZavaSales --interactive --execute --verbose
```

`--solution` is an existing unmanaged Dataverse solution: importing a plugin package requires it, both because the package name is prefixed with the solution publisher's customization prefix and because every registered component (package, assembly, steps, Custom APIs) is added to that solution. `apply --execute` uploads the plugin package into it, ensures the plugin type exists, then registers the step and its image. Re-running with no local changes is a no-op. If a run fails partway, the resume report on stderr lists which operations completed and which did not - fix the cause and re-run (upserts are idempotent).

**5. Verify** in Dataverse: update an account's name and confirm the step fires (its message appears in the plugin trace log).
