---
_description: "Preview then apply registrations: --solution (an unmanaged solution), secure config, batched writes, resuming a failed apply, CI usage"
---

# Plan and apply

This page is the reference for `vela plan`, `vela apply` and `vela inspect`, the commands that deploy plugin
packages to Dataverse. Other pages link here instead of repeating it.

- `plan` previews the changes. It never writes.
- `apply` makes them, after a confirmation.
- `inspect` shows the registrations in your packages next to the live ones. It never writes.

Before you start, build the solution (the build packs the plugin package into `artifacts/`, see
[Quickstart](../getting-started/quickstart.md)) and pick a way to connect (see [Authentication](../configuration/authentication.md)).
For the full option list, run `vela plan --help` or see the [CLI cheat sheet](../reference/cli-cheatsheet.md). Each
command accepts only its own options: an unknown or misplaced one, such as `plan --auto-approve`, exits `2`.

## Quick start

```bash
# Local: browser sign-in, preview, then apply (apply asks you to type "yes")
vela plan  --packages-dir ./artifacts --solution ZavaSales --interactive
vela apply --packages-dir ./artifacts --solution ZavaSales --interactive

# CI: app registration, with the secret in the environment (never on the command line)
export DATAVERSE_CLIENT_SECRET=...   # set by the pipeline's secret store
vela plan --package ./artifacts/zava_Zava.Plugins.1.0.0.nupkg --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --strict --detailed-exitcode
vela apply --package ./artifacts/zava_Zava.Plugins.1.0.0.nupkg --solution ZavaSales \
  --url https://zava.crm.dynamics.com --tenant-id <tenant-id> --client-id <app-id> \
  --auto-approve
```

## Choose the packages

Pass the packages with `--package <file.nupkg>` (`-p`, repeatable) or `--packages-dir <folder>`.

- **One version per package.** A folder can hold several builds of the same package (`Zava.Plugins.1.0.0.nupkg` and
  `Zava.Plugins.1.0.1.nupkg`). `--packages-dir` scans only the highest version of each package id and names the
  others on stderr.
- **Plugin assembly.** Vela infers the plugin assembly from the package: the assembly named like the package id, or
  like the id without its publisher prefix (`zava_Zava.Plugins` ships `Zava.Plugins.dll`). When a package holds more
  than one candidate, or the assembly name differs from the package id, name it with `--package-assembly <name.dll>`.
- **No bare assemblies.** Vela deploys plugin packages only. A standalone `.dll` is not an input.

## Choose the solution

`plan` and `apply` require `--solution <uniquename>` (alias `--solution-unique-name`). Without it they exit `2` before
connecting; with `--interactive` they prompt for it. The solution drives the plan:

- every component the run writes is placed in it;
- a Vela component that exists but is missing from it is an `add-to-solution` operation;
- Custom API names are checked against its publisher prefix;
- an unprefixed package name takes its prefix from it.

Dataverse imports a plugin package only when its name carries a publisher prefix (`pfx_Name`), so `apply` renames
`Zava.Plugins` to `zava_Zava.Plugins` in memory. A package that already has a prefix is uploaded as is:
`vela init --publisher-prefix zava` (or `--samples`, `--managed-identity`) names the packages `zava_Zava.Plugins` and
`zava_Zava.Integrations`.

`inspect` is read-only: `--solution` is optional there, and required only with `--webresources`.

### The solution must be unmanaged

`plan` and `apply` deploy into an unmanaged solution, which lives in a development environment. Right after
connecting, before reading or writing anything, they look up the `--solution` solution in the target environment and
exit `4` when it does not exist (`Solution not found: ZavaSales`) or when it is managed:

```text
Solution 'ZavaSales' is managed in https://zava-test.crm.dynamics.com. Vela deploys to the unmanaged solution in a
development environment: check that the connection points at the development environment, not a test or production
one where the solution was imported managed. Nothing was written.
```

A managed solution usually means that the connection points at a test or production environment where the solution
was imported as managed. For how Dataverse treats managed solutions, see
[Solutions in Power Apps](https://learn.microsoft.com/power-apps/maker/data-platform/solutions-overview).
`inspect` does not run this check.

### One solution per run

Packages that deploy into different solutions take one run per solution, each with its own `-p`:

```bash
vela plan -p ./artifacts/zava_Zava.Plugins.1.0.0.nupkg      --solution ZavaCore         --interactive
vela plan -p ./artifacts/zava_Zava.Integrations.1.0.0.nupkg --solution ZavaIntegrations --interactive
```

The CLI never reads `PROJECT.md`. `vela init` writes it once, with the solution of each package, for the people and
agents who type these commands (see [PROJECT.md](scaffold.md#projectmd)).

## Preview the changes (`vela plan`)

`plan` reads the steps and Custom APIs from your packages, reads the live registrations, and lists the ordered
operations that `apply` would run: `create`, `update`, `delete`, `add-to-solution` and `link`, with a detailed diff.

On a terminal it prints a colored summary. Piped, redirected, or with `--json` / `--out <file>`, it prints a versioned
JSON document (`schemaVersion`, `hasChanges`, `summary`, `operations`, `diff`, `diagnostics`, `info`, and more),
described in [Machine-readable output](../reference/machine-readable-output.md). Notices and the `--verbose` summary go
to stderr, so `--verbose --json` still writes clean JSON.

A successful `plan` exits `0`. With `--detailed-exitcode`, it exits `8` when there are changes to apply and `0` when
there is nothing to do.

### Warnings and `--strict`

`diagnostics` holds warnings: rank collisions and a declared secure configuration without a value. `--strict` turns
any warning into exit `4`, which makes `plan --strict` a useful gate in CI.

Information that is not a problem goes to `info` and never fails `--strict`. The most common case is a plugin
assembly that is not in Dataverse yet, on a first deployment.

### Errors that stop the preview

Some problems would make `apply` fail. `plan` and `apply` check for them while they build the preview, before the
`apply` confirmation prompt and before any write, with or without `--strict`. When one is found, the command exits `4`
with every problem on stderr, writes no JSON document, and writes nothing to Dataverse:

- **Publisher prefix.** Dataverse requires a Custom API unique name to carry the publisher prefix of its solution.
  Vela resolves the prefix of the `--solution` solution and reports every Custom API the run would create or update
  without it (for example `DoThing` under prefix `acme`: rename it `acme_DoThing`).
- **Step message and table.** For every step the run would create or update, Vela looks up its message, and for a
  step on a table the message filter of that table. A message that does not exist (`Dataverse message not found`) or a
  table the message has no filter for (`no message filter exists for entity ...`) is reported. A step on the message
  of a Custom API that the same run creates is not checked here, since that Custom API is not in the environment yet:
  `apply` writes the Custom APIs before the steps (see [Batched writes](#batched-writes)).
- **Assemblies of the plugin package.** When the package update would rename, drop or add a plugin assembly of a
  deployed package, Vela stops: an existing package keeps the assemblies it was created with. See
  [Rename the plugin assembly](#rename-the-plugin-assembly).
- **Plugin types the package update removes.** When the package update removes a registered plugin type (its class
  was deleted or renamed), Dataverse refuses the update while steps are still registered on that type, even disabled
  ones. `apply` deletes the
  steps Vela manages on that type before the package update. Any other step on it (for example one added with the
  Plugin Registration Tool) is reported with its plugin type: delete it or move it to another plugin type first.

### What the plan contains

**Code-only rebuilds.** A rebuild that changes no step or Custom API still recompiles the assembly, so `apply`
republishes the plugin package. `plan` shows this as a `package` operation (`create` for a new package, `update` when
its version or content differs) and sets `hasChanges: true`. The numeric `summary` counts only the step and Custom API
changes (plus web resources on a `--webresources` run), so it can be zero while `hasChanges` is `true`.

**Empty packages.** A package with no plugin and no Custom API yet (a freshly scaffolded project) that is not in
Dataverse gets no `package` operation: `plan` names it under `info` and `apply` skips it. Once the package is in
Dataverse, it is republished like any other, so removing its last plugin still updates it.

**Solution membership.** A component that did not change is never written, so it would never join the solution on its
own. `plan` lists each Vela component missing from the solution (package, assembly, steps, Custom APIs and their
parameters) as an `add-to-solution` operation, and `apply` adds it with `AddSolutionComponent`, without its required
components.

**How steps are matched.** A step is identified by its plugin type and its step key: `WithStepKey(...)`, or the key
Vela derives from its trigger, filters and images. The rank is not part of the key, so a rank change is an `update`.
Several steps can share a trigger (message, stage, mode and table).

- A step whose key changed still pairs with its live step by trigger. That `update` carries `liveStepKey` (the live
  step's key) in its `after` definition, so `apply` rewrites exactly that step.
- An added step never takes over another step on its trigger.
- `asyncAutoDelete` (`Async(deleteJobOnSuccess: true)`, asynchronous steps only) is compared too.
- When you upgrade from a version where the rank was part of the key, a step deployed at a rank other than 1 without
  an explicit key gets a new key once. The first `plan` shows it as an `update` (a rename), not a delete and a create.

**Relationship scope.** A step scoped with `ForRelationship(...)` shows its scope in the `plan` and `inspect` output
(`[filter=none; pre=0; post=0; relationships=account_contacts]`) and as `relationships` on its local JSON definition.
Vela applies the scope at run time and does not deploy it (Associate and Disassociate steps register with no
relationship filter), so there is no live value to compare and a scope change alone is not a diff. The new scope ships
with the plugin package. One exception: a keyless step that is not the first keyless step on its trigger has the
scope in its derived key, so a scope change gives it a new key, and `plan` shows an `update` with `liveStepKey`.

**Unmanaged registrations.** An assembly can mix Vela plugins with classic `IPlugin` or `CodeActivity` classes
registered by other tools. Vela leaves the steps and Custom APIs of those classes alone and lists them under
`unmanaged` (JSON) or "unmanaged (left untouched)" (terminal). This is information, not a warning: `--strict` does
not fail on it.

## Apply the changes (`vela apply`)

`apply` runs the operations of the plan, then reads the environment again and prints what still differs (a summary
on a terminal, JSON when piped or with `--json`).

It writes by default, with a guard:

- On a terminal, it shows the plan and asks you to type `yes`. Any other answer prints `Apply cancelled.` and exits
  `0` without writing.
- When stdin is not a terminal (a script, CI, a coding agent), it refuses with exit `4` unless you pass
  `--auto-approve` (alias `--yes`), so a script never writes by accident.

Use `vela plan` to preview without writing.

`--strict` is checked twice on `apply`: on the plan before any write, and on the state read back after the writes.

### Read the apply result

On success, `summary` is what remains after the apply, so it is normally all zero even though work was done. The
`executed` block reports what the run did: created, updated and deleted counts, `packageRepublished`,
`addedToSolution`, `linkedToManagedIdentity`, and a per-kind breakdown in `byKind`. When every write succeeded but the
environment still differs, the mode is `apply-not-converged` and the exit code is `5`. The fields are described in
[Machine-readable output](../reference/machine-readable-output.md). Do not parse the free-text `notes`.

### Batched writes

`apply` sends its writes in batches. It writes the Custom APIs first, then the steps, so a step can register on the
message of a Custom API created by the same run. Within each, it groups the operations into dependency tiers (first
the step or Custom API rows, then their images, parameters, secure configuration and state) and sends each tier as
`ExecuteMultiple` requests of up to 100 operations. Set the size with `--batch-size <1-1000>`. When a Custom API
fails, the run stops before the steps. Deletes run in reverse order, children before parents.

- Results are still reported per plugin and per Custom API. The `--verbose` progress lines (`[step] create ...`) are
  printed once a batch has run, and a failed one ends with `(not applied)`.
- When a whole batch cannot run (for example `ExecuteMultiple` is unavailable, or the request times out), that chunk
  falls back to one request per operation.
- When the batch had in fact run before the call failed, a replayed create whose id already exists counts as applied,
  not as a failure.

On a very large environment, raise the request timeout with
[`--timeout`](../configuration/authentication.md#connection-timeout).

### Resume a failed apply

`apply` has no transaction across operations, so a failure partway through (a rejected registration, a transient
Dataverse error) leaves the operations that already succeeded in place. The run exits `7`.

Each batch continues past a failed item, and a child row (an image or a parameter) whose parent row failed is skipped
and reported. The output lists, for each plugin and Custom API, what was applied and what failed and why, followed by
a resume report: every planned operation under exactly one of `completed`, `failed` and `not applied` (steps are named
by their step key). Because a batch continues past a failure, a step planned after the failed one can still be listed
as completed.

With `--json` or `--out`, the `apply-failed` document carries the same lists in its `failure` object (see
[Machine-readable output](../reference/machine-readable-output.md#plan-and-apply)).

A removed step is deleted by the id `apply` read from the environment, never looked up again by name or trigger.

Every operation is an idempotent upsert or a safe delete. Fix the cause and run `apply` again (with `--auto-approve`
in CI): the operations that already succeeded do nothing, the failed ones are retried, and the next `plan` is clean.

### Rename the plugin assembly

A plugin package keeps the assemblies it was created with: Dataverse refuses a package update that renames or drops
one of them, and does not register an assembly that an update adds. `plan` and `apply` compare the assemblies of the
deployed package with the local package's before any write. When they differ (the plugins project changed its
`<AssemblyName>` but kept its package id, for example), both commands exit `4` with a message that names the package
and the assemblies, and write nothing.

Vela does not delete and recreate the package for you: that deletes every step on the package's plugin types,
including steps other tools registered, and their secure configurations. Choose one of these:

- Keep the assembly name the environment has: set `<AssemblyName>` in the plugins project back to it.
- Recreate the package:
  1. Delete the steps registered on the package's plugin types (the Plugin Registration Tool lists them).
  2. Delete the plugin package. If a Custom API is implemented by one of its plugin types, clear that Custom API's
     plugin type first, or delete the Custom API.
  3. Run `vela plan`, then `vela apply`. `apply` creates the package and registers its steps and Custom APIs again,
     pointing an existing Custom API at the new plugin type. Supply the steps' secure configurations again with
     `--secure-config` or `VELA_SECURE_CONFIG_<NAME>` (see [Secure configuration](#secure-configuration)).

See [Build and package plug-in code](https://learn.microsoft.com/power-apps/developer/data-platform/build-and-package)
for the plugin package itself.

### Custom APIs owned by another assembly

A Custom API unique name is unique in the environment. If a local Custom API's name already exists, implemented by a
plugin type in an assembly that this run does not deploy, `apply` stops with an error naming that type and assembly
instead of pointing the Custom API at your plugin. Rename the Custom API, or remove the existing one first.

### Assembly name collisions across projects

The registered plugin assembly is keyed by its .NET assembly name (the `<AssemblyName>` of the project, for example
`Zava.Plugins`), which is unique in the environment. `vela init --prefix <prefix>` names the plugins project
`<prefix>.Plugins`, so two solutions created with the same prefix build the same assembly name, and applying the
second overwrites the first's assembly. When the assembly already exists but is not part of the `--solution`
solution, `apply` warns before the confirmation prompt:

```text
Warning: plugin assembly 'Zava.Plugins' already exists in this org and is not part of solution 'Sales'. Applying
overwrites its content, modifying an assembly owned by another solution. Set a unique <AssemblyName> in the plugins
project (for example prefix it with the solution name) to avoid the collision.
```

To avoid it, give each project a distinct `<AssemblyName>`, for example prefixed with the solution name.

## Secure configuration

A step's secure configuration value is supplied at `plan` or `apply` time, never compiled into the package:

- `--secure-config NAME=@file` reads the value from a file. The file loses one trailing line ending (`\n` or `\r\n`,
  as `echo secret > key.txt` adds); everything else is kept.
- `VELA_SECURE_CONFIG_<NAME>` supplies it from the environment.
- `--secure-config NAME=VALUE` works but warns, because the value is visible in shell history and process listings.

Without a value, the environment keeps its own secure configuration and `apply` does not touch it. An empty
`VELA_SECURE_CONFIG_<NAME>` variable, or a file that is empty after that trim, counts as no value and prints a warning
naming it: a pipeline that substitutes an empty string for an undefined secret leaves the live value in place. To
clear a secure configuration, pass an empty inline value, `--secure-config NAME=`. Deleting a step also deletes its
secure configuration row. See
[Description, state and configuration](plugins.md#description-state-and-configuration).

## Link a managed identity

`--managed-identity <managedidentityid>` links every plugin package the run publishes to that `managedidentity` record
of the target environment (`pluginpackage.managedidentityid`). The package is the same in every environment and each
environment has its own record, so the id is an option of the run, not a project setting.

- `plan` checks that the record exists (an unknown id fails with exit `4` before anything is written), then reports
  each package as already linked, to link, or to change from its current managed identity. A package to link or
  change is a `link` operation of kind `package`, appears in the `managedIdentity` JSON block, and counts in
  `hasChanges` and `--detailed-exitcode`.
- `apply` writes the link right after the package publish, so a package created by the same run is linked too. It
  then reads the link back to check that it converged, and counts it in `executed.linkedToManagedIdentity`.
- Without the option, the link is neither read nor changed.

See [Managed identity](managed-identity.md) and [Signing](signing.md).

## Package code runs in a separate process

To read a package's steps and Custom APIs, Vela loads its plugin types and calls their definition methods, so the
scan runs code from the package. `plan`, `apply`, `inspect` and `vela models --package` run the scan in a child
`vela` process. The packages are extracted into a new temp directory per run, readable only by your user on Linux and
macOS. This keeps package code away from the CLI's own memory: the open Dataverse connection, the access
token, the client secret and the secure configuration values. The child starts with an allowlisted environment
(system paths, locale and .NET runtime variables), so `DATAVERSE_*`, `VELA_SECURE_CONFIG_*`, `AZURE_*`,
managed-identity and CI token variables are not passed to it. Its output goes to stderr.

> [!WARNING]
> The child process is **not** a sandbox. It runs with your user's rights, so hostile package code can still read the
> parent process's command line and environment (`ps`, and `/proc/<pid>/cmdline` and `/proc/<pid>/environ` on
> Linux), your files (including the interactive token cache under `~/.vela`) and the network. Scan only packages you
> build or trust.

For the same reason, keep secrets off the command line, where other processes on the machine can read them. Prefer
`DATAVERSE_CLIENT_SECRET` or `--client-secret-stdin` to `--client-secret`, `DATAVERSE_CONNECTION_STRING` to
`--connection-string`, and `VELA_SECURE_CONFIG_<NAME>` or `--secure-config NAME=@file` to an inline value. Or use no
secret at all, with `--interactive` or the ambient Azure credential (`--url` alone).

Set `VELA_SCAN_IN_PROCESS=1` to scan inside the `vela` process, for debugging only.

## Run unattended

`plan`, `apply` and `inspect` never wait on a prompt when stdin is redirected or piped. The optional "package assembly
name" prompt and the browser sign-in prompts appear only on a real terminal. A scripted run that passes its inputs on
the command line finishes, or fails with a clear message, without hanging. For CI patterns, see
[Environments and CI](../configuration/environments-secrets.md).

## Deploy web resources (`--webresources`)

> [!NOTE]
> Preview. This is part of the client-side initiative and may change.

`plan` and `apply` can also deploy web resources (form scripts, CSS, images, HTML) from a folder, alongside plugins or
on their own:

```bash
# Preview the creates, updates and deletes that make the solution match ./dist
vela plan --webresources ./dist --solution ZavaSales --interactive

# Apply them and publish the changes
vela apply --webresources ./dist --solution ZavaSales --interactive
```

- **Names.** The folder is mirrored as is: each file's unique name is its path relative to the folder, with forward
  slashes. The top-level folder (for example `adc_`) is a real folder, not a prefix Vela adds. Each name must start
  with the solution's publisher prefix; Vela checks this up front.
- **Types and changes.** The type comes from the extension (`.js`, `.css`, `.html`, `.png`, ...). A content hash
  drives the diff, so unchanged files are skipped. Created and changed resources are published in one
  `PublishXmlRequest`.
- **Independent of plugins.** A plugin-only `apply` never touches web resources, and a `--webresources` run needs no
  `--package` and never touches plugins.
- **Solution.** `--webresources` requires `--solution`: web resources are reconciled within a solution.
- **Deletes.** A live web resource is deleted only when it shares a top-level folder with the local set but is absent
  from it. Mirroring one folder never prunes another, and an empty folder deletes nothing.

Generate the TypeScript form models the scripts build on with [`vela models --forms`](models.md), and scaffold the
client project with [`vela init --web-resources`](scaffold.md).

## Read the registration manifest in code

`FrameworkManifestScanner` (namespace `Vela.Manifest`, in `Vela.Plugins`) lists the plugin steps and Custom APIs that
an assembly declares. Call it yourself, for example in a test:

```csharp
var manifest = FrameworkManifestScanner.Scan(typeof(AccountPostUpdate).Assembly);
foreach (var def in manifest.PluginSteps)
{
    Console.WriteLine($"{def.PluginTypeName}: {def.Message} / {def.Stage}");
    Console.WriteLine($"  Filtering: {string.Join(", ", def.FilteringAttributes)}");
    Console.WriteLine($"  PreImages: {string.Join(", ", def.PreImages.Select(i => i.Alias))}");
}
```

Pass `Strict = true` to make the scan throw when it reports any diagnostic:

```csharp
var manifest = FrameworkManifestScanner.Scan(
    typeof(AccountPostUpdate).Assembly,
    new FrameworkManifestScanOptions { Strict = true });
```

A message with no typed trigger registers through `On<TRequest>()`, or through a marker type with
`OnMessage<TMarker>()` and `[CustomMessage("...")]`; see [Other messages](plugins.md#other-messages).
