# Troubleshooting

1. **`Custom API '...' can only call RegisterCustomApi(uniqueName, handler) inside RegisterCustomApi()`**
   - Call `RegisterCustomApi("name", handler)` only from your `RegisterCustomApi()` override.
2. **A step handler does not compile or does not bind**
   - Bind it by method group (`.Handle(OnUpdated)`) and give it the step's context: the generated
     `OnUpdatedContext`, or the message context such as `IUpdatePostContext<TEntity>` (`ILocalPluginContext` for an
     `OnMessage<TMarker>()` step). [VELA015](../reference/analyzers.md#vela015) names the expected type. A generated
     context also needs the plugin class to be `partial` ([VELA014](../reference/analyzers.md#vela014)).
3. **A Create/Update step does not build**
   - It needs `FilterOn(...)` ([VELA020](../reference/analyzers.md#vela020)).
4. **`plan` or `apply` fails with "vela plan requires --solution <uniquename>"** (exit 2)
   - Pass `--solution <uniqueName>`, the existing solution the packages deploy into (`--interactive` prompts for
     it). The CLI never reads `PROJECT.md`, which records it. Dataverse imports a package only when its name carries
     the solution publisher's prefix, and Vela takes the prefix from that solution. See [Plan and apply](plan-apply.md).
5. **No changes in `plan`**
   - Check the `.nupkg` path or `--packages-dir`, its version, and the environment you connect to.
6. **`apply` does nothing after a code change**
   - The `.nupkg` may be stale: an incremental build does not always re-run `GeneratePackageOnBuild`. Rebuild
     cleanly (`dotnet clean`, then `dotnet build -c Release`) and re-run `plan`.
   - Build the package with `dotnet build -c Release`, not a bare `dotnet pack`: a cold pack produces a package with
     no assembly (`NU5128`), which `plan`/`apply` reject with `No assemblies found in package` (exit 4).
7. **`apply` fails with `Plugin type not found in Dataverse after the plugin package publish`**
   - Dataverse did not expose the plugin type from the published package. Dataverse caches plugin assemblies by
     version, so changed code under the same assembly version can keep serving the old assembly: bump the
     assembly version and rebuild. Projects scaffolded by `vela init` use `AssemblyVersion` `1.0.*` for this reason.
   - If `dotnet build` of the solution fails with `The process cannot access the file '...<Plugin>.1.0.0.nupkg' because it is being used by another process`, one likely cause is a test project's `ProjectReference` building the plugin project a second time (in Debug) alongside the solution's Release build, with both packing into the same file. A binary log (`dotnet build -bl`) shows whether the plugin project was built twice. If so, add `<ShouldUnsetParentConfigurationAndPlatform>false</ShouldUnsetParentConfigurationAndPlatform>` to the test projects' `<PropertyGroup>` (projects scaffolded by `vela init` already set it). Otherwise, look for concurrent builds or another process (an IDE, an antivirus scan) holding the file.
