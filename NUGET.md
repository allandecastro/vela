# Vela

**Build Dataverse with confidence.** Vela is a framework, a CLI and a test harness for Microsoft Dataverse plugins
and Custom APIs, with a preview-first `plan` → `apply` workflow.

| Package | What it is |
| --- | --- |
| `Vela.Plugins` | The plugin runtime (.NET Framework 4.6.2): fluent step and Custom API registration, typed handler contexts, DI, caching. Bundles the Roslyn analyzers (VELA001-VELA023, several with a code fix) and the source generator for typed images and step contexts. |
| `Vela.Messages` | Typed request/response base classes (`TypedRequest`, `CustomApiRequest`, ...), a dependency of `Vela.Plugins` that client code can use alone. |
| `Vela.Testing` | The no-connection test harness: an in-memory Dataverse and the plugin pipeline. |
| `Vela.Cli` | The `vela` .NET tool: `plan` / `apply` registrations, `init` / `add` scaffolding, `models` generation, `sign`. |

## Install

```bash
dotnet add package Vela.Plugins                 # in your plugin project
dotnet add package Vela.Testing                 # in its test project
dotnet tool install --global Vela.Cli           # the vela command
```

## Example

```csharp
using Vela.Context;
using Vela.Plugin;

public sealed partial class AccountPostUpdate(string unsecureConfig, string secureConfig)
    : EntityPluginBase<Account>(unsecureConfig, secureConfig)
{
    protected override void RegisterPluginSteps() =>
        RegisterStep().OnUpdate().PostOperation().Sync()
            .FilterOn(a => a.Name)
            .Handle(OnUpdated);

    private void OnUpdated(IUpdatePostContext<Account> ctx) =>
        ctx.Trace("Account renamed to '{0}'", ctx.Request.Target.Name);
}
```

Build the plugin package, then preview and apply it into your solution (`--solution` gives the package the
publisher prefix Dataverse requires; `apply` asks for confirmation on a terminal):

```bash
vela plan  --packages-dir artifacts --solution ZavaSales --interactive
vela apply --packages-dir artifacts --solution ZavaSales --interactive
```

## Documentation

Guides and reference: **https://allandecastro.github.io/vela/** · Source and changelog:
https://github.com/allandecastro/vela
