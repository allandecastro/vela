<img src="https://raw.githubusercontent.com/allandecastro/vela/main/branding/png/vela-chevron.svg.png" width="72" align="left" alt="Vela" />

# Vela

**Build Dataverse with confidence** — modern Microsoft Dataverse development.

<br clear="left" />

Vela is a framework (`Vela.Plugins`) + CLI (`Vela.Cli`, command `vela`) for building Microsoft
Dataverse plugins and Custom APIs, with a safe, preview-first `plan` → `apply` workflow.

## Install

```powershell
# Framework (reference from your plugin project)
dotnet add package Vela.Plugins

# CLI (.NET global tool — exposes the `vela` command)
dotnet tool install --global Vela.Cli
```

## Quick example

```csharp
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;

public class AccountPostUpdate : EntityPluginBase<Account>
{
    public AccountPostUpdate(string unsecure, string secure) : base(unsecure, secure) { }

    protected override void RegisterPluginSteps()
        => RegisterSteps().OnUpdate()
               .Stage(PluginStages.PostOperation)
               .Mode(ExecutionMode.Synchronous)
               .FilteringAttributes(a => a.Name);

    protected override void Execute(ILocalPluginContext ctx, Account target)
        => ctx.Trace($"Account renamed to '{target.Name}'");
}
```

Then preview and apply against your environment:

```powershell
vela plan  --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive
vela apply --package .\artifacts\Zava.Plugins.1.0.0.nupkg --interactive --execute
```

## Documentation

Full docs, scaffolding (`vela init`), Custom APIs, DI, caching and tracing:
**https://github.com/allandecastro/vela**
