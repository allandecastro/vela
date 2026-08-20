# Plan & Apply

```powershell
# 1) Compare local package(s) with live Dataverse state (client id / client secret)
vela plan `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# 2) Multiple packages
vela plan `
  --packages-dir .\artifacts `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# 3) Preview apply operations (dry-run) against the same environment
vela apply `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# 3b) Execute apply operations (idempotent upsert/delete) into an existing Dataverse solution
vela apply `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret> `
  --solution ZavaSales `
  --execute

# 4) Interactive local mode (wizard prompts)
vela plan --interactive

# 5) CI strict mode (fail if scanner/metadata diagnostics exist)
vela plan `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret> `
  --strict
```

`plan` compares local plugin/custom-api metadata extracted from `.nupkg` against Dataverse and returns a preview: `hasChanges`, `summary`, and ordered `operations` (`create`, `update`, `delete`) plus detailed `diff`.

`apply --execute` applies Dataverse create/update/delete operations to converge with local package metadata, then prints the post-apply state diff.

By default the plugin assembly is inferred from the package. If a package bundles more than one candidate assembly, or the assembly name differs from the package id, name it explicitly with `--package-assembly <name.dll>`.

## Resuming a failed apply

`apply` has no cross-operation transaction, so a failure partway through (a rejected registration, a transient Dataverse error) leaves the operations that already succeeded in place. The output is an ordered resume report listing what was applied and what was not, then the failing operation and its reason. Every operation is an idempotent upsert or an already-safe delete, so once you fix the cause you re-run `apply --execute`: the operations that already succeeded become no-ops and the run continues from where it stopped, converging to a clean `plan`.

## Registration manifest

Use `FrameworkManifestScanner` to extract both plugin steps and Custom APIs:

```csharp
var manifest = FrameworkManifestScanner.Scan(typeof(AccountPostUpdate).Assembly);
foreach (var def in manifest.PluginSteps)
{
    Console.WriteLine($"{def.PluginTypeName}: {def.Message} / {def.Stage}");
    Console.WriteLine($"  Filtering: {string.Join(", ", def.FilteringAttributes)}");
    Console.WriteLine($"  PreImages: {string.Join(", ", def.PreImages.Select(i => i.Alias))}");
}
```

Use strict mode in CI when you want scanner diagnostics to fail the build:

```csharp
var manifest = FrameworkManifestScanner.Scan(
    typeof(AccountPostUpdate).Assembly,
    new FrameworkManifestScanOptions { Strict = true });
```

Typed custom SDK messages can use `OnMessage<TMessage>()` with `[CustomMessage("...")]` to keep intellisense without string literals.
