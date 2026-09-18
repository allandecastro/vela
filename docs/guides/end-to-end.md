# End-to-end example

A complete loop from plugin code to a registered, running step, using a solution scaffolded by
[`vela init`](scaffold.md).

**1. Write a plugin** in your plugin-package project (`Zava.Plugins`, which references `Vela.Plugins`):

```csharp
using Vela.Context;
using Vela.Constants;
using Vela.Plugin;
using Vela.Steps;
using Zava.Shared.Models.Generated;

public class AccountPostUpdate : EntityPluginBase<Account>
{
    public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

    protected override void RegisterPluginSteps()
    {
        RegisterStep().OnUpdate()
            .PostOperation()
            .Sync()
            .FilterOn(a => a.Name)         // only fire when Name changes
            .WithPreImage(a => a.Name)
            .Handle(OnAccountUpdated);          // stage-typed handler, bound by method group
    }

    private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
    {
        var target = ctx.Request.Target;
        var before = ctx.PreImage;   // typed Account? for the default alias
        ctx.Trace($"Account renamed from '{before?.Name}' to '{target.Name}'");
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
vela plan --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive
```

At a terminal `plan` prints a colorized preview - e.g. `summary: +1 -0 ~0` plus an ordered `operations` list -
and makes no changes. Piped or redirected (or with `--json`), it emits JSON instead.

**4. Apply** the changes (idempotent create/update/delete):

```powershell
vela apply --package .\artifacts\Zava.Plugins.1.0.0.nupkg --solution ZavaSales --interactive
```

`apply` writes by default: on a terminal it shows the plan and asks you to confirm (type `yes`); add `--auto-approve` to skip the prompt in CI. `--solution` is an existing unmanaged Dataverse solution: importing a plugin package requires it, both because the package name is prefixed with the solution publisher's customization prefix and because every registered component (package, assembly, steps, Custom APIs) is added to that solution. `apply` uploads the plugin package into it, ensures the plugin type exists, then registers the step and its image. Re-running with no local changes is a no-op. If a run fails partway, the resume report on stderr lists which operations completed and which did not - fix the cause and re-run (upserts are idempotent).

**5. Verify** in Dataverse: update an account's name and confirm the step fires (its message appears in the plugin trace log).
