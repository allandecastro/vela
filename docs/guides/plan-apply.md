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

> **Code-only rebuilds show up too.** A rebuild that changes no step or custom-API definition but recompiles the assembly leaves the registration diff empty, yet `apply` still republishes the plugin package. `plan` surfaces this as a `package` operation (`create` when the package is new, `update` when its stored version or content differs) and sets `hasChanges: true`, so a rebuild is never reported as "no changes". The numeric `summary` stays the non-package registration diff (steps and custom APIs, plus web resources on a `--webresources` run), matching the apply JSON where `packageRepublished` is a field separate from the counts; the package operation is listed under `operations`.

> **Publisher-prefix check with `--solution`.** Dataverse requires a Custom API unique name to carry the target solution's publisher prefix, and `apply` rejects one that does not. Pass `--solution <uniqueName>` to `plan` and it resolves that prefix and emits a warning for every Custom API being created or updated whose unique name lacks it (for example `DoThing` under publisher prefix `acme` warns to rename it `acme_DoThing`), so the mismatch is caught in the preview instead of at `apply`. The warnings appear in the `warnings` block (human) and the `diagnostics` array (JSON); under `--strict` they fail the command, which makes `plan --solution --strict` a useful pre-apply gate in CI.

`apply` applies Dataverse create/update/delete operations to converge with local package metadata, then prints the post-apply state diff (human summary at a terminal, JSON when piped or with `--json`). It writes by default: on a terminal it shows the plan and asks you to type `yes`; in a non-interactive session (no stdin TTY) it refuses unless you pass `--auto-approve` (alias `--yes`), so a script never writes unattended by accident. Use `vela plan` to preview without writing.

> **Reading the apply JSON (scripts/agents).** `summary` on a successful apply is the **post-apply remaining** diff, so it is `{added:0,changed:0,removed:0}` even though work was done. Read the separate **`executed`** block for what actually happened: `{ "created": N, "updated": M, "deleted": K, "packageRepublished": bool, "byKind": [ { "kind": "step", "created": .., "updated": .., "deleted": .. }, ... ] }`, taken from the pre-apply diff (`byKind` breaks the totals down per component: `step` / `custom-api` / `web-resource`). `executed` is present only on `apply` (mode `apply-execute`), not on `plan`/`inspect`. Do not parse the free-text `notes`.

`plan`, `apply` and `inspect` are safe to run under redirected or piped stdin (CI, agents): they never block waiting on a prompt. The optional "package assembly name" prompt only appears on a real terminal, and interactive OAuth prompts are terminal-only too, so a scripted run that supplies its inputs on the command line runs to completion (or fails with a clear message) without hanging.

### Batched writes

`apply` sends its writes in **batches** rather than one request per record: the create/update/delete operations are grouped into dependency tiers (the step and custom API rows first, then their images, parameters, secure config and state) and each tier is flushed in chunked `ExecuteMultiple` requests. This collapses hundreds of round-trips into a handful, so `apply` is much faster on a large environment and far less likely to hit the connection timeout. Deletes run in the reverse order (children before parents). The output is unchanged: results are still reported **per plugin and per custom API**. If a whole batch cannot run on a given environment (for example `ExecuteMultiple` is unavailable, or a request times out), that chunk automatically falls back to one-by-one execution, so batching never makes `apply` less reliable. See [connection timeout](../configuration/authentication.md#connection-timeout) to raise the per-request budget on a very large org.

**Unmanaged registrations.** An assembly can mix Vela plugins with classic `IPlugin` / `CodeActivity` classes
registered by other tools. Those classes are not Vela types, so `plan` and `apply` leave their steps and Custom APIs
alone and list them under `unmanaged` (JSON) / "unmanaged (left untouched)" (terminal). This is information, not a
warning: `--strict` does not fail on it.

By default the plugin assembly is inferred from the package. If a package bundles more than one candidate assembly, or the assembly name differs from the package id, name it explicitly with `--package-assembly <name.dll>`.

### Assembly name collisions across projects

The registered plugin assembly is keyed by its **.NET assembly name** (the csproj `<AssemblyName>`, e.g. `Zava.Plugins`), which is **unique per org**. `vela init <prefix>` scaffolds the plugins project as `<prefix>.Plugins`, so two solutions initialized with the same prefix build the same assembly name and **collide**: applying the second rewrites the first's assembly content in that org. When `apply` is run with `--solution` and the target assembly already exists but is **not part of that solution**, it warns before overwriting:

```
Warning: plugin assembly 'Zava.Plugins' already exists in this org and is not part of solution 'Sales'.
         Applying overwrites its content, modifying an assembly owned by another solution.
```

To avoid it, give each project a distinct `<AssemblyName>` (for example prefix it with the solution name). The registered name follows the csproj, so renaming the assembly is the override.

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

`apply` has no cross-operation transaction, so a failure partway through (a rejected registration, a transient Dataverse error) leaves the operations that already succeeded in place. Batching does not change this: each chunk runs with continue-on-error, so a single failed item does not abort the rest, and a tier-2 child (image, parameter) whose tier-1 row failed is skipped and reported rather than orphaned. The output is a per-owner report listing, for each plugin and custom API, what was applied and what failed and why. Every operation is an idempotent upsert or an already-safe delete, so once you fix the cause you re-run `apply` (add `--auto-approve` in CI): the operations that already succeeded become no-ops and the failed ones are reconciled, converging to a clean `plan`.

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
