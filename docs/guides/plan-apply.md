# Plan & Apply

This page is the reference for `vela plan`, `vela apply` and `vela inspect`; other pages link here rather than
repeating the commands. `vela help plan` (or `vela plan --help`) lists every option, and each command accepts only
its own options (an unknown or misplaced one, such as `plan --auto-approve`, exits `2`).

```bash
# Local: browser login, preview then apply into your solution (prompts for confirmation)
vela plan  --packages-dir ./artifacts --solution ZavaSales --interactive
vela apply --packages-dir ./artifacts --solution ZavaSales --interactive

# CI: app registration, secret from the environment (never on the command line)
export DATAVERSE_CLIENT_SECRET=...   # set by the pipeline's secret store
vela plan --package ./artifacts/Zava.Plugins.1.0.0.nupkg --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --strict --detailed-exitcode
vela apply --package ./artifacts/Zava.Plugins.1.0.0.nupkg --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --auto-approve
```

Always pass `--solution <uniqueName>` to `plan` and `apply`: Dataverse imports a plugin package only when its name
carries a publisher prefix (`pfx_Name`), and Vela takes that prefix from the solution (`apply` renames `Zava.Plugins`
to `zava_Zava.Plugins` in memory). A package already named with a prefix is uploaded as is: `vela init
--publisher-prefix zava` (or `--samples`, `--managed-identity`) names the packages `zava_Zava.Plugins` and
`zava_Zava.Integrations`. Without `--solution`, `plan` and `apply` both fail on a package whose name has no
prefix, before anything is written (exit 4). `inspect` is read-only and does not check this.
`--solution` also places every written component in that solution. See [Authentication](../configuration/authentication.md)
for the other ways to connect.


`plan` compares local plugin/custom-api metadata extracted from `.nupkg` against Dataverse and returns a preview of the ordered `create` / `update` / `delete` / `add-to-solution` operations plus a detailed diff. At a terminal this is a colorized human summary; piped, redirected, or with `--json`, it is a versioned JSON document (`schemaVersion`, `hasChanges`, `summary`, `operations`, `diff`, `diagnostics`, `info`, ...) documented in [Machine-readable output](../reference/machine-readable-output.md). Notices and the `--verbose` summary go to stderr, so `--verbose --json` still writes the JSON. With `--detailed-exitcode`, `plan` exits `8` when there are changes to apply and `0` when there is nothing to do.

> **`--strict` and the first deployment.** `diagnostics` holds warnings (rank collisions, a secure configuration without a value, a Custom API name without the publisher prefix) and `--strict` fails on them. A plugin assembly that is not in Dataverse yet is expected on a first deployment: it is reported under `info`, which never fails `--strict`.

> **One version per package.** `--packages-dir` may hold several builds of the same package (`Zava.Plugins.1.0.0.nupkg` and `Zava.Plugins.1.0.1.nupkg`). Only the highest version of each package id is scanned; the others are named on stderr.

> **Solution membership.** With `--solution`, a component that did not change (deployed earlier without `--solution`, or into another solution) would never be written, so it would never join the solution. `plan` lists each Vela component missing from the solution (package, assembly, steps, Custom APIs and their parameters) as an `add-to-solution` operation, and `apply` adds it with `AddSolutionComponent` (without required components).

> **Code-only rebuilds show up too.** A rebuild that changes no step or custom-API definition but recompiles the assembly leaves the registration diff empty, yet `apply` still republishes the plugin package. `plan` surfaces this as a `package` operation (`create` when the package is new, `update` when its stored version or content differs) and sets `hasChanges: true`, so a rebuild is never reported as "no changes". The numeric `summary` stays the non-package registration diff (steps and custom APIs, plus web resources on a `--webresources` run), matching the apply JSON where `packageRepublished` is a field separate from the counts; the package operation is listed under `operations`.

> **An empty package is not created.** A package that holds no plugin and no Custom API yet (a freshly scaffolded project) and is not in Dataverse gets no `package` operation: `plan` names it under `info` and `apply` skips it. Once it is in Dataverse, it is republished like any other package, so removing its last plugin still updates it.

> **How steps are matched.** A step is identified by its plugin type and step key (`WithStepKey(...)`, or the key Vela derives from its trigger, filters and images); its rank is not part of it, so a rank change is an `update`. Several steps may share a trigger (message, stage, mode, entity). A step whose key changed still pairs with its live step by trigger, and that `update` carries `liveStepKey` (the live step's key) in its `after` definition, so `apply` rewrites exactly that step; an added step never takes over another step on its trigger. `asyncAutoDelete` (`Async(deleteJobOnSuccess: true)`, async steps only) is compared too.
>
> **Relationship scope.** A step scoped with `ForRelationship(...)` shows its scope in the plan and inspect output (`[filter=none; pre=0; post=0; relationships=account_contacts]`) and in the JSON as `relationships` on its local definition. Vela applies the scope at run time and does not deploy it (Associate and Disassociate steps register with no relationship filter), so the live read has no value to compare and a scope change is not a diff. The new scope deploys with the plugin package. A keyless step that is not the first keyless step on its trigger is the exception: its derived key includes the scope, so a scope change gives it a new key, and `plan` shows an `update` carrying `liveStepKey` (the rename described above). The first keyless step on a trigger keeps its key.

> Upgrading: a step deployed at a rank other than 1 without an explicit step key gets a new automatic key once (the rank used to be part of it); the first `plan` shows it as an `update` (a rename), not a delete and create.

> **Publisher-prefix check with `--solution`.** Dataverse requires a Custom API unique name to carry the target solution's publisher prefix, and `apply` rejects one that does not. Pass `--solution <uniqueName>` to `plan` and it resolves that prefix and emits a warning for every Custom API being created or updated whose unique name lacks it (for example `DoThing` under publisher prefix `acme` warns to rename it `acme_DoThing`), so the mismatch is caught in the preview instead of at `apply`. The warnings appear in the `warnings` block (human) and the `diagnostics` array (JSON); under `--strict` they fail the command, which makes `plan --solution --strict` a useful pre-apply gate in CI.

`apply` applies Dataverse create/update/delete operations to converge with local package metadata, then prints the post-apply state diff (human summary at a terminal, JSON when piped or with `--json`). It writes by default: on a terminal it shows the plan and asks you to type `yes`; in a non-interactive session (no stdin TTY) it refuses unless you pass `--auto-approve` (alias `--yes`), so a script never writes unattended by accident. Use `vela plan` to preview without writing.

> **Reading the apply JSON (scripts/agents).** `summary` on a successful apply is the **post-apply remaining** diff, so it is `{added:0,changed:0,removed:0}` even though work was done. Read the separate **`executed`** block for what actually happened: `{ "created": N, "updated": M, "deleted": K, "packageRepublished": bool, "addedToSolution": S, "byKind": [ { "kind": "step", "created": .., "updated": .., "deleted": .. }, ... ] }`, taken from the pre-apply diff (`byKind` breaks the totals down per component: `step` / `custom-api` / `web-resource`). `executed` is present only on `apply` (mode `apply-execute`, or `apply-not-converged` when the environment still differs after the writes: `summary` is then non-zero and the exit code is `5`), not on `plan`/`inspect`. Do not parse the free-text `notes`. See [Machine-readable output](../reference/machine-readable-output.md).

`plan`, `apply` and `inspect` are safe to run under redirected or piped stdin (CI, agents): they never block waiting on a prompt. The optional "package assembly name" prompt only appears on a real terminal, and interactive OAuth prompts are terminal-only too, so a scripted run that supplies its inputs on the command line runs to completion (or fails with a clear message) without hanging.

### Batched writes

`apply` sends its writes in **batches** rather than one request per record: the create/update/delete operations are grouped into dependency tiers (the step and custom API rows first, then their images, parameters, secure config and state) and each tier is flushed in chunked `ExecuteMultiple` requests. Each `ExecuteMultiple` carries up to 100 requests; tune it with `--batch-size <1-1000>`. This collapses hundreds of round-trips into a handful, so `apply` is much faster on a large environment and far less likely to hit the connection timeout. Deletes run in the reverse order (children before parents). The output is unchanged: results are still reported **per plugin and per custom API**, and the `--verbose` progress lines (`[step] create ...`) are printed once the batch has run, a failed one ending with `(not applied)`. If a whole batch cannot run on a given environment (for example `ExecuteMultiple` is unavailable, or a request times out), that chunk automatically falls back to one-by-one execution, so batching never makes `apply` less reliable. When the batch had in fact run before the call failed (a timeout after the server executed it), a replayed create whose id already exists is counted as applied, not as a failure. See [connection timeout](../configuration/authentication.md#connection-timeout) to raise the per-request budget on a very large org.

**Unmanaged registrations.** An assembly can mix Vela plugins with classic `IPlugin` / `CodeActivity` classes
registered by other tools. Those classes are not Vela types, so `plan` and `apply` leave their steps and Custom APIs
alone and list them under `unmanaged` (JSON) / "unmanaged (left untouched)" (terminal). This is information, not a
warning: `--strict` does not fail on it.

**Custom APIs owned by another assembly.** A Custom API unique name is environment-wide. If a local Custom API's
name already exists in the environment, implemented by a plugin type in an assembly that is not being applied,
`apply` stops with an error naming that type and assembly instead of re-pointing it to your plugin. Rename the
Custom API, or remove the existing one first.

**Secure configuration.** A step's secure configuration value is supplied at plan/apply time with
`--secure-config NAME=VALUE` / `NAME=@file` (or `VELA_SECURE_CONFIG_<NAME>`), never compiled into the package;
without a value the environment keeps its own. A value read from a file loses one trailing line ending (`\n` or
`\r\n`, as `echo secret > key.txt` adds); everything else in the file is kept. Deleting a step also deletes its secure-configuration row. See
the step configuration section of [Write a plugin](plugins.md).

### Package code runs in a separate process (not a sandbox)

Reading a package's steps and Custom APIs instantiates its plugin types and calls their definition methods, so the scan runs code from the package. `plan`, `apply`, `inspect` and `vela models --package` run the scan in a child `vela` process. That keeps package code away from the CLI's own memory: the open Dataverse connection, the access token, the resolved client secret and secure-config values. The child also starts with an allowlisted environment (system paths, locale, .NET runtime variables), so `DATAVERSE_*`, `VELA_SECURE_CONFIG_*`, `AZURE_*`, managed-identity and CI token variables are not handed to it by accident, and its output goes to stderr.

> [!WARNING]
> This is **not** a sandbox. The child runs with your OS user's rights, so hostile package code can still read the parent process's command line and environment (`ps`, and `/proc/<pid>/cmdline` and `/proc/<pid>/environ` on Linux), your files (including the interactive token cache under `~/.vela`) and the network. Scan only packages you build or trust.

Keep secrets off the command line, where every process on the machine can see them: prefer `DATAVERSE_CLIENT_SECRET` or `--client-secret-stdin` to `--client-secret`, `DATAVERSE_CONNECTION_STRING` to `--connection-string`, `VELA_SECURE_CONFIG_<NAME>` or `--secure-config NAME=@file` to an inline value, or no secret at all with `--interactive` (cached token) or the ambient Azure credential (`--url` alone: `az login`, managed identity). Set `VELA_SCAN_IN_PROCESS=1` to scan in-process (debugging).

By default the plugin assembly is inferred from the package: the assembly named like the package id, or like the id without its publisher prefix (`zava_Zava.Plugins`, as `vela init --publisher-prefix zava` names it, ships `Zava.Plugins.dll`). If a package bundles more than one candidate assembly, or the assembly name differs from the package id, name it explicitly with `--package-assembly <name.dll>`.

### Assembly name collisions across projects

The registered plugin assembly is keyed by its **.NET assembly name** (the csproj `<AssemblyName>`, e.g. `Zava.Plugins`), which is **unique per org**. `vela init --prefix <prefix>` scaffolds the plugins project as `<prefix>.Plugins`, so two solutions initialized with the same prefix build the same assembly name and **collide**: applying the second rewrites the first's assembly content in that org. When `apply` is run with `--solution` and the target assembly already exists but is **not part of that solution**, it warns before overwriting:

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

`apply` has no cross-operation transaction, so a failure partway through (a rejected registration, a transient Dataverse error) leaves the operations that already succeeded in place. Batching does not change this: each chunk runs with continue-on-error, so a single failed item does not abort the rest, and a tier-2 child (image, parameter) whose tier-1 row failed is skipped and reported rather than orphaned. The output is a per-owner report listing, for each plugin and custom API, what was applied and what failed and why, followed by the resume report: every planned operation under exactly one of `completed`, `failed` and `not applied` (a step is named with its step key). Since a batch continues past a failure, a step planned after the failed one can still be listed as completed. Every operation is an idempotent upsert or an already-safe delete, so once you fix the cause you re-run `apply` (add `--auto-approve` in CI): the operations that already succeeded become no-ops and the failed ones are reconciled, converging to a clean `plan`.

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
