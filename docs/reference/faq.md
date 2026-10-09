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
`vela init` auto-increment `AssemblyVersion` (`1.0.*`). When a plugin type is still missing after the package
publish, `vela apply` stops with `Plugin type not found in Dataverse after the plugin package publish` instead of
registering its steps.

### I get "can only call RegisterCustomApi(uniqueName, handler) inside RegisterCustomApi()" - what does it mean?

Call `RegisterCustomApi("name", ...)` only from inside your `protected override void RegisterCustomApi()`.

### My step handler isn't found (or has the wrong signature) - what should it be?

A step handler is an instance method returning `void` with one parameter, bound by method group
(`.Handle(OnUpdated)`). The parameter is one of:

- the stage-typed message context: `IUpdatePostContext<TEntity>` at PostOperation, `IUpdatePreContext<TEntity>` at
  PreValidation/PreOperation (it has `ctx.Request` but no `ctx.Response`);
- the generated `{Handler}Context` (here `OnUpdatedContext`), which adds the typed `ctx.PreImage` / `ctx.PostImage`
  (the plugin must be `partial`, [VELA014](analyzers.md#vela014));
- `ILocalPluginContext`, for `OnMessage("...")` steps or when you need nothing typed.

A context for another message or stage is a build error ([VELA015](analyzers.md#vela015)), and every step chain
must end with `.Handle(...)` ([VELA002](analyzers.md#vela002)). A lightweight Custom API handler is
`void Handler(ILocalPluginContext ctx)`.

### Can `plan`/`apply` authenticate with a managed identity or a certificate?

Managed identity: yes. A bare `--url` uses the ambient Azure credential (`DefaultAzureCredential`: `az login`,
environment variables, workload identity, and managed identity) - the recommended default for CI and agents.
Certificate auth: no. The other supported methods are connection string, client secret (env var / stdin / arg),
and interactive MSAL (with sovereign-cloud overrides). See [authentication](../configuration/authentication.md)
and [limitations](limitations.md).

### Does `apply` manage solutions, security roles, or environment variables?

No. `plan`/`apply` manage plugin packages/assemblies, steps, step images, Custom APIs, and web resources
(with `--webresources --solution`), including each step's enabled/disabled state and unsecure configuration (and
its secure configuration when you supply the value with `--secure-config`). Solutions must already exist
(`--solution` only adds components), and SDK message filters, security roles / field security, and environment
variables are not managed. Assemblies are upserted, never deleted. See [limitations](limitations.md).

### Can I register a plugin from a bare `.dll`?

No. A plugin-package `.nupkg` (built via `Microsoft.PowerApps.MSBuild.Plugin`) is required; a bare `.dll` is
not supported. See [limitations](limitations.md).

### Does the in-memory test harness support every SDK message?

No - it covers a common set (`Create`/`Update`/`Delete`/`Retrieve`/`RetrieveMultiple`, the `*Multiple` bulk
messages, `Upsert`, `Assign`/`SetState`/`Merge`/`Associate`/`Disassociate`, file blocks, metadata reads, and
`ExecuteMultiple`/`ExecuteTransaction`). For any other message, a single-plugin test can inject its own
`IOrganizationService` via `WithOrganizationService(...)`; see [limitations](limitations.md#velatesting).
See [testing](../guides/testing.md).

### Can I run `plan`/`apply`/`inspect` in CI or from a coding agent without them hanging?

Yes. They never block on a prompt under redirected or piped stdin: the optional "package assembly name" prompt
and the interactive OAuth prompts only appear on a real terminal. Supply your inputs on the command line (and
`--auto-approve` for `apply`) and the command runs to completion or fails with a clear message.
See [plan → apply](../guides/plan-apply.md).

### `apply` rejected my signed plugin package - why?

Dataverse only imports a plugin package whose name carries a solution publisher prefix. When the name lacks it,
`apply --solution` adds the prefix by rewriting the `.nupkg`, which would invalidate a signature applied by
`vela sign` (Dataverse returns `0x80040265`), so `apply` refuses a signed package that needs the rename. Give the
package its prefixed name (for example `zava_Zava.Plugins`) before building and signing it.
See [signing](../guides/signing.md).

### Why was my workflow-enabled Custom API rejected?

A Custom API with `WorkflowSdkStepEnabled` set must be an action (not a function) and cannot use
`Entity`/`EntityCollection`/`string[]` parameters, nor an `EntityReference` unless it is bound to a table.
`Build()` rejects such a definition with a message naming the API and the parameter, so `plan` fails before
any write. See [Write a Custom API](../guides/custom-apis.md).

### Is the harness security model on by default?

No - it is opt-in and off by default. With no `WithSecurity(...)` call the in-memory service grants full
access, exactly as before. Opt in only when you need privilege/access checks.
See [security model](../guides/security-model.md).

### Can `vela models` generate relationship navigation properties or LINQ / OrganizationServiceContext?

Relationship navigation properties: yes. `vela models` generates typed N:1 / 1:N / N:N navs, read with
`RetrieveWithRelated` and written with `CreateWithRelated` / `AssociateRelated` (see
[Relationship navigation properties](../guides/models.md#relationship-navigation-properties)).
LINQ / `OrganizationServiceContext` is out of scope (lazy loading and change-tracking don't fit the plugin sandbox;
use `Query.For<T>()` instead), as are localized multi-language labels and custom class/property casing; use
`pac modelbuilder` if you need those. See [limitations](limitations.md).

### Does `vela sign` support Key Vault or HSM signing?

No. `vela sign` accepts a certificate by fingerprint (`CurrentUser\My`), a `.pfx` (with password/credential),
or a base64 `.pfx`, and delegates to `dotnet nuget sign`. Key Vault / HSM signing is out of scope, and
`--pfx-credential` is Windows-only. See [signing](../guides/signing.md).

### Is Vela available on nuget.org?

Yes. `Vela.Plugins`, `Vela.Testing` and `Vela.Messages` (the framework line) and `Vela.Cli` (the CLI) are
published on nuget.org. Reference the framework packages with `dotnet add package Vela.Plugins` / `Vela.Testing`, and
install the CLI with `dotnet tool install --global Vela.Cli`. It is still **pre-1.0**, so APIs may change
between minor versions - see [limitations](limitations.md).

### Can the plugin cache do async I/O?

No - `IPluginCacheService` is synchronous only. The plugin pipeline runs synchronously and the cache locks
with `Monitor`, so do your I/O inside the factory and cache the result. See [caching](../guides/caching.md).
