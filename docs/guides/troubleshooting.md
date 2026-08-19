# Troubleshooting

1. **`RegisterCustomApi(...) can only be called inside RegisterCustomApi()`**
   - Call it only from the override `RegisterCustomApi()` method.
2. **Step handler not found / wrong signature**
   - Ensure instance method signature is `void Handler(ILocalPluginContext, TEntity)`.
3. **Custom API handler not found / wrong signature**
   - Ensure signature is `void Handler(ILocalPluginContext)` for lightweight mode.
4. **No changes in `plan`**
   - Confirm the `.nupkg` path, version, and environment connection are correct.
5. **Unexpected legacy warnings**
   - Migrate to direct patterns (`RegisterPluginSteps()` + `RegisterSteps().On...()`, then in custom APIs override `RegisterCustomApi()` and call `RegisterCustomApi(...)`).
6. **`The plug-in type could not be found in the plug-in assembly` at runtime**
   - Dataverse caches plugin assemblies **by version**, so re-deploying changed code under the **same** assembly version can keep serving the old assembly. **Bump the assembly version** and rebuild. Projects scaffolded by `vela init` auto-increment `AssemblyVersion` (`1.0.*`) to avoid this; if you add a new plugin type without bumping the version, `vela apply` **fails fast** with this guidance rather than letting it break at runtime.
7. **`apply` does nothing after I changed plugin code**
   - The `.nupkg` may be **stale**: an incremental build does not always re-run `GeneratePackageOnBuild`, so the package can still hold the previous assembly and `apply` sees no diff. Do a clean build (`dotnet build -c Release` after removing `bin`/`obj`, or `dotnet clean`) so the package is regenerated, then re-run `plan`/`apply`.
   - Build the plugin package with the **build** (`GeneratePackageOnBuild`), not a bare `dotnet pack`: on a plugin project `dotnet pack` can produce a package with no registrable assembly, which `apply` rejects with `No assemblies found in package` (exit 4).
