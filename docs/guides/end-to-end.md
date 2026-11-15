# End-to-end example

From plugin code to a registered, running step, in a solution scaffolded by [`vela init`](scaffold.md).

**1. Write a plugin** in the plugin-package project (`Zava.Plugins`, which references `Vela.Plugins`):

```csharp
namespace Zava.Plugins.Account.Plugins
{
    using Vela.Plugin;
    using Zava.Shared.Models.Generated;

    public sealed partial class AccountPostUpdate(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)          // fire only when Name is sent
                .WithPreImage(a => a.Name)
                .Handle(OnAccountUpdated);

        private void OnAccountUpdated(OnAccountUpdatedContext ctx) =>
            ctx.Trace("Account renamed from '{0}' to '{1}'", ctx.PreImage?.Name, ctx.Request.Target.Name);
    }
}
```

See [Write a plugin](plugins.md) for the step chain and the generated `OnAccountUpdatedContext`.

**2. Build the plugin package.** Use `dotnet build`, not `dotnet pack`: the build runs `GeneratePackageOnBuild` and
drops the `.nupkg` into `artifacts/`, while a cold `dotnet pack` emits a package with no assembly.

```bash
dotnet build src/Zava.Plugins -c Release
```

**3. Preview** the changes (read-only). `--solution` names an existing unmanaged solution: Dataverse imports a plugin
package only when its name carries the publisher prefix (`vela init --publisher-prefix zava` names it
`zava_Zava.Plugins`; an unprefixed package gets the prefix of that solution's publisher), and every component is
added to it.

```bash
vela plan --packages-dir artifacts --solution ZavaSales --interactive
```

At a terminal `plan` prints a colorized preview (`summary: +1 -0 ~0` and the ordered operations); piped, or with
`--json`, it prints JSON.

**4. Apply** the same plan:

```bash
vela apply --packages-dir artifacts --solution ZavaSales --interactive
```

At a terminal `apply` shows the plan and asks you to type `yes` (`--auto-approve` in CI). It publishes the package,
waits for the plugin type, then registers the step and its image. A second run with no change does nothing. If a
run fails partway, the resume report on stderr lists what completed; fix the cause and re-run. See
[Plan and apply](plan-apply.md) for authentication and options.

**5. Verify** in Dataverse: rename an account, then read the trace line with
[`vela logs`](cli-overview.md#reading-execution-logs-vela-logs) (`vela logs --since 1h -i`) or in the plug-in trace
log. Trace logging must be [enabled on the environment](https://learn.microsoft.com/power-apps/developer/data-platform/logging-tracing#enable-trace-logging).
