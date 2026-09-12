# FAQ

Common questions about what Vela does, doesn't do, and how to get unstuck. See [limitations](limitations.md)
for the full "by design" list.

### Why does `plan` report no changes?

Confirm the `.nupkg` path, its version, and the environment connection are correct - `plan` compares the
metadata extracted from your package against the live environment, so a wrong path or stale version shows
nothing to reconcile. See [plan/apply](../guides/plan-apply.md).

### At runtime I get "The plug-in type could not be found in the plug-in assembly" - why?

Dataverse caches plugin assemblies **by version**, so re-deploying changed code under the **same** assembly
version can keep serving the old assembly. Bump the assembly version and rebuild. Solutions scaffolded by
`vela init` auto-increment `AssemblyVersion` (`1.0.*`); if you add a new plugin type without bumping the
version, `vela apply` fails fast with this guidance rather than letting it break at runtime.

### I get "RegisterCustomApi(...) can only be called inside RegisterCustomApi()" - what does it mean?

Call `RegisterCustomApi(...)` only from inside the `RegisterCustomApi()` override method.

### My step handler isn't found (or has the wrong signature) - what should it be?

A plugin step handler is an instance method taking the **stage-typed** message context, bound by method group -
`.Handler(OnUpdated)` where `void OnUpdated(IUpdatePostContext<TEntity> ctx)` for a PostOperation step (or
`IUpdatePreContext<TEntity>` for a PreValidation/PreOperation step, which has `ctx.Request` but no `ctx.Response`;
or `void OnX(ILocalPluginContext ctx)` for an `OnMessage("...")` step). The context type is checked against the
message and stage at compile time, so binding an `OnUpdate()` step to an `ICreatePostContext` handler - or reading
`ctx.Response` from a pre-stage handler - does not build. A single-step plugin binds the same typed
`.Handler(...)` (its context is `I{Message}PostContext<T>` / `I{Message}PreContext<T>`) and reads the record
with `ctx.Request.Target`; overriding `Execute(ILocalPluginContext ctx)` and using `ctx.GetTarget<T>()` stays
supported as an untyped fallback.
For a lightweight Custom API handler the signature is `void Handler(ILocalPluginContext)`.

### I'm seeing unexpected legacy warnings - how do I clear them?

Migrate to the direct patterns: `RegisterPluginSteps()` + `RegisterSteps().On...()` for plugins, and for
Custom APIs override `RegisterCustomApi()` and call `RegisterCustomApi(...)`.

### Can `plan`/`apply` authenticate with a certificate or a managed-identity connection?

No. Supported authentication is connection string, client secret (env var / stdin / arg), and interactive MSAL
(with sovereign-cloud overrides). There is no certificate auth and no managed-identity connection - managed
identity is only relevant to `vela sign` for MI plugins. See [limitations](limitations.md).

### Does `apply` manage solutions, security roles, or environment variables?

No. `plan`/`apply` manage plugin packages/assemblies, steps, step images, and Custom APIs. Solutions must
already exist (`--solution` only adds components), and SDK message filters, step enabled/disabled state, step
configuration, security roles / field security, web resources, and environment variables are not managed.
Assemblies are upserted, never deleted. See [limitations](limitations.md).

### Can I register a plugin from a bare `.dll`?

No. A plugin-package `.nupkg` (built via `Microsoft.PowerApps.MSBuild.Plugin`) is required; a bare `.dll` is
not supported. See [limitations](limitations.md).

### Does the in-memory test harness support every SDK message?

No - it covers a common set (`Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple`, the `*Multiple` bulk
messages, `Assign`/`Associate`/`Disassociate`, metadata reads, and `ExecuteMultiple`/`ExecuteTransaction`).
For any other message, inject your own `IOrganizationService` via `WithOrganizationService(...)`.
See [testing](../guides/testing.md).

### Is the harness security model on by default?

No - it is opt-in and off by default. With no `WithSecurity(...)` call the in-memory service grants full
access, exactly as before. Opt in only when you need privilege/access checks.
See [security model](../guides/security-model.md).

### Can `vela models` generate relationship navigation properties or LINQ / OrganizationServiceContext?

No - those are out of scope (lazy loading and change-tracking don't fit the plugin sandbox; use `Query.For<T>()`
instead). Localized multi-language labels and custom class/property casing are also out of scope; use
`pac modelbuilder` if you need those. See [limitations](limitations.md).

### Does `vela sign` support Key Vault or HSM signing?

No. `vela sign` accepts a certificate by fingerprint (`CurrentUser\My`), a `.pfx` (with password/credential),
or a base64 `.pfx`, and delegates to `dotnet nuget sign`. Key Vault / HSM signing is out of scope, and
`--pfx-credential` is Windows-only. See [signing](../guides/signing.md).

### Is Vela available on nuget.org?

Yes. `Vela.Plugins` and `Vela.Testing` (the framework line) and `Vela.Cli` (the CLI) are published on
nuget.org. Reference the framework packages with `dotnet add package Vela.Plugins` / `Vela.Testing`, and
install the CLI with `dotnet tool install --global Vela.Cli`. It is still **pre-1.0**, so APIs may change
between minor versions - see [limitations](limitations.md).

### Can the plugin cache do async I/O?

No - `IPluginCacheService` is synchronous only. The plugin pipeline runs synchronously and the cache locks
with `Monitor`, so do your I/O inside the factory and cache the result. See [caching](../guides/caching.md).
