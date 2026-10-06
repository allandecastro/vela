# Vela

**Build Dataverse with confidence** - modern Microsoft Dataverse development.

Vela is a framework (`Vela.Plugins`) + CLI (`Vela.Cli`, command `vela`) for building Microsoft
Dataverse plugins and Custom APIs, with a safe, preview-first `plan` → `apply` workflow.

`Vela.Plugins` also bundles Roslyn analyzers (VELA001-VELA020, which catch invalid step/column usage and plugin
pitfalls at build time, several with an IDE code fix) and source generators (typed image projections) - no extra
reference or setup.

## Install

```powershell
# Framework (reference from your plugin project)
dotnet add package Vela.Plugins

# CLI (.NET global tool - exposes the `vela` command)
dotnet tool install --global Vela.Cli
```

## Quick example

```csharp
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;

public class AccountPostUpdate : EntityPluginBase<Account>
{
    public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

    protected override void RegisterPluginSteps()
        => RegisterStep().OnUpdate()
               .PostOperation()
               .Sync()
               .FilterOn(a => a.Name)
               .Handle(OnUpdated);

    private void OnUpdated(IUpdatePostContext<Account> ctx)
        => ctx.Trace($"Account renamed to '{ctx.GetTarget<Account>().Name}'");
}
```

Then preview and apply against your environment:

```powershell
vela plan  --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive
# apply writes by default (prompts on a terminal); add --auto-approve for CI / unattended runs.
vela apply --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive
```

The `vela` CLI does more than deploy:

- `vela init` scaffolds a complete, build-ready plugin solution.
- `vela models` generates early-bound classes, security roles, and typed Custom API request/response messages from your environment.
- `vela add` generates a plugin, service, Custom API, test, or step into an existing project.
- `vela sign` signs a plugin package for managed-identity registration.

## Documentation

Full docs, scaffolding (`vela init`), Custom APIs, DI, caching, and tracing:
**https://github.com/allandecastro/vela**
