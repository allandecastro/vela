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

# 3) Preview the apply operations without writing (dry-run) - use plan
vela plan `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret>

# 3b) Apply (idempotent upsert/delete) into an existing Dataverse solution.
#     On a terminal, apply shows the plan and asks for confirmation; add --auto-approve for CI / unattended runs.
vela apply `
  --package .\artifacts\Zava.Plugins.1.0.0.nupkg `
  --url https://zava.crm.dynamics.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --client-secret <client-secret> `
  --solution ZavaSales `
  --auto-approve

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

`plan` compares local plugin/custom-api metadata extracted from `.nupkg` against Dataverse and returns a preview of the ordered `create` / `update` / `delete` operations plus a detailed diff. At a terminal this is a colorized human summary; piped, redirected, or with `--json`, it is JSON with `hasChanges`, `summary`, `operations`, and `diff` fields.

`apply` applies Dataverse create/update/delete operations to converge with local package metadata, then prints the post-apply state diff (human summary at a terminal, JSON when piped or with `--json`). It writes by default: on a terminal it shows the plan and asks you to type `yes`; in a non-interactive session (no stdin TTY) it refuses unless you pass `--auto-approve` (alias `--yes`), so a script never writes unattended by accident. Use `vela plan` to preview without writing.

`plan`, `apply` and `inspect` are safe to run under redirected or piped stdin (CI, agents): they never block waiting on a prompt. The optional "package assembly name" prompt only appears on a real terminal, and interactive OAuth prompts are terminal-only too, so a scripted run that supplies its inputs on the command line runs to completion (or fails with a clear message) without hanging.

By default the plugin assembly is inferred from the package. If a package bundles more than one candidate assembly, or the assembly name differs from the package id, name it explicitly with `--package-assembly <name.dll>`.

## Deploy web resources (`--webresources`)

> [!NOTE]
> Preview / work in progress. This is part of the new client-side initiative and may change.

`plan` and `apply` can also deploy **web resources** (form scripts, CSS, images, HTML, ...) from a folder mirror,
alongside or instead of plugins:

```powershell
# preview: create/update/delete web resources so the solution matches ./dist
vela plan --webresources ./dist --solution ZavaSales --interactive

# apply them (and publish the changes so they go live)
vela apply --webresources ./dist --solution ZavaSales --interactive
```

The folder is mirrored **verbatim**: each file's unique name is its path relative to the root, with forward
slashes, so the top-level folder (for example `adc_`) is a real folder, not a prepended prefix. The type is
inferred from the extension (`.js`, `.css`, `.html`, `.png`, ...); a content hash drives the diff, so unchanged
files are skipped. Changed and created resources are published in one `PublishXmlRequest`.

Web resources reconcile **independently and additively**: a plugin-only `apply` never touches web resources, and
a `--webresources` run never touches plugins. `--webresources` **requires `--solution`** (web resources are
reconciled within a solution, component type 61). Deletion is **scoped to the top-level folders you actually
deploy**: a live web resource is removed only when it shares a top-level folder (e.g. `adc_`) with the local set
but is absent from it, so mirroring a subset folder never prunes other folders, and an empty folder deletes
nothing. A `--webresources` run needs no `--package`, so you can deploy web resources on their own. Each name must
start with the solution's publisher prefix (its top-level folder); Vela validates this up front and fails with a
clear message otherwise. Generate the TypeScript form models the scripts build on with
[`vela models --forms`](models.md), and scaffold the client project with
[`vela init --web-resources`](scaffold.md).

## Resuming a failed apply

`apply` has no cross-operation transaction, so a failure partway through (a rejected registration, a transient Dataverse error) leaves the operations that already succeeded in place. The output is an ordered resume report listing what was applied and what was not, then the failing operation and its reason. Every operation is an idempotent upsert or an already-safe delete, so once you fix the cause you re-run `apply` (add `--auto-approve` in CI): the operations that already succeeded become no-ops and the run continues from where it stopped, converging to a clean `plan`.

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
