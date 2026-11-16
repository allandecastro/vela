# Troubleshooting

This page lists the errors you are most likely to meet with Vela, in the order you meet them (writing code,
generating code, building, deploying, running), with their cause and fix. Each issue has its own heading, so you can
link to it. Exit codes are those of the [vela CLI](cli-overview.md#exit-codes). For questions that are not errors,
see the [FAQ](../reference/faq.md).

## A step handler does not compile or does not bind

Bind it by method group (`.Handle(OnUpdated)`) and give it the step's context: the generated `OnUpdatedContext`, or
the message context such as `IUpdatePostContext<TEntity>` (`ILocalPluginContext` for an `OnMessage<TMarker>()`
step). [VELA015](../reference/analyzers.md#vela015) names the expected type. A generated context also needs the
plugin class to be `partial` ([VELA014](../reference/analyzers.md#vela014)).

## A Create or Update step does not build

It needs `FilterOn(...)` ([VELA020](../reference/analyzers.md#vela020)).

## `Custom API '...' can only call RegisterCustomApi(uniqueName, handler) inside RegisterCustomApi()`

Call `RegisterCustomApi("name", handler)` only from your `RegisterCustomApi()` override.

## `--entity 'account': no early-bound model was found ...` (exit 2)

`--entity` takes the model class (`Account`) or the table logical name (`account`). A logical name is mapped to the
class through the generated model, so it needs one: run [`vela models`](models.md) first, or pass the class name.

## `... requires --prefix <publisherprefix>` (exit 2) on `vela add customapi`

Pass the publisher prefix of the target solution. `PROJECT.md` records it, and
`vela solution show <solution> --json` prints it. See [Add a component (`vela add`)](add.md).

## `dotnet build` fails because the `.nupkg` is used by another process

The message is `The process cannot access the file '...<Plugin>.1.0.0.nupkg' because it is being used by another
process`. One likely cause is a test project's `ProjectReference` building the plugin project a second time (in Debug)
alongside the solution's Release build, with both packing into the same file. A binary log (`dotnet build -bl`) shows
whether the plugin project was built twice. If so, add
`<ShouldUnsetParentConfigurationAndPlatform>false</ShouldUnsetParentConfigurationAndPlatform>` to the test project's
`<PropertyGroup>` (projects scaffolded by `vela init` already set it). Otherwise, look for concurrent builds or
another process (an IDE, an antivirus scan) holding the file.

## `plan` or `apply` fails with `No assemblies found in package` (exit 4)

Build the package with `dotnet build -c Release`, not `dotnet pack` alone: a pack without a prior build produces a
package with no assembly (`NU5128`).

## `vela plan requires --solution <uniquename>` (exit 2)

Pass `--solution <uniqueName>`, the existing solution the packages deploy into (`--interactive` prompts for it). The
CLI never reads `PROJECT.md`, which records it. Dataverse imports a plugin package only when its name carries the
solution publisher's prefix, and Vela takes the prefix from that solution. See [Plan and apply](plan-apply.md).

## `Solution '...' is managed in ...` or `Solution not found` (exit 4)

The connection points at an environment where the `--solution` solution is managed (a test or production
environment that imported it) or does not exist. Connect to the development environment that holds the unmanaged
solution. Nothing was written. See [Plan and apply](plan-apply.md).

## `plan` shows no changes

Check the `.nupkg` path or `--packages-dir`, the package version, and the environment you connect to.
`--packages-dir` takes the newest version of each package id in the folder.

## `apply` does nothing after a code change

The `.nupkg` may be stale: an incremental build does not always re-run `GeneratePackageOnBuild`. Rebuild cleanly
(`dotnet clean`, then `dotnet build -c Release`) and run `plan` again.

## `apply` fails with `Plugin type not found in Dataverse after the plugin package publish`

Dataverse did not expose the plugin type from the published package. Dataverse caches plugin assemblies by version,
so changed code under the same assembly version can keep serving the old assembly: bump the assembly version and
rebuild. Projects scaffolded by `vela init` use `AssemblyVersion` `1.0.*` for this reason.

## `apply` fails partway (exit 7)

The resume report on stderr lists what completed. Every operation is idempotent: fix the cause and run `apply`
again.

## The step does not fire, or you need its trace

Read the trace log with [`vela logs`](cli-overview.md#reading-execution-logs-vela-logs) (`vela logs --errors --since 1h -i`),
or the failed system jobs of an asynchronous step with `vela logs --jobs`. Trace logging must be
[enabled on the environment](https://learn.microsoft.com/power-apps/developer/data-platform/logging-tracing#enable-trace-logging).
