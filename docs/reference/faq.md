# FAQ

Short answers to common questions about Vela, grouped by topic. For the full list of what Vela does not do by
design, see [Limitations](limitations.md); for step-by-step fixes, see [Troubleshooting](../guides/troubleshooting.md).

## Deploying with plan and apply

### Why does `plan` report no changes?

`plan` compares the metadata in your package with the live environment. Check that `--package` or `--packages-dir`
points at the package you just built, that its version is the one you expect (`--packages-dir` keeps only the highest
version of each package id), and that the connection targets the right environment. See
[Plan and apply](../guides/plan-apply.md).

### Why does `plan` or `apply` say my solution is managed or not found?

`plan` and `apply` deploy into an unmanaged solution of a development environment. They exit `4` before reading or
writing anything when the `--solution` solution does not exist in the target environment, or is managed there. A
managed solution usually means the connection points at a test or production environment. See
[The solution must be unmanaged](../guides/plan-apply.md#the-solution-must-be-unmanaged).

### Does `apply` manage solutions, security roles or environment variables?

No. `plan` and `apply` manage plugin packages and assemblies, steps, step images and Custom APIs, including each
step's enabled state and unsecure configuration, and its secure configuration when you supply the value with
`--secure-config`. The solution must already exist and be unmanaged: `--solution` only adds components to it. SDK
message filters, security roles, field security and environment variables are not managed. Assemblies are created and
updated, never deleted. See [Limitations](limitations.md).

### Can I register a plugin from a bare `.dll`?

No. Vela deploys plugin packages (`.nupkg`) only. See [Limitations](limitations.md).

### Can I run `plan`, `apply` and `inspect` in CI or from a coding agent without them hanging?

Yes. They never wait on a prompt when stdin is redirected or piped: the optional "package assembly name" prompt and
the browser sign-in prompts appear only on a real terminal. Pass your inputs on the command line (and
`--auto-approve` for `apply`) and the command finishes or fails with a clear message. See
[Run unattended](../guides/plan-apply.md#run-unattended).

### At run time I get "The plug-in type could not be found in the plug-in assembly". Why?

That message comes from Dataverse. In the first seconds after a deployment, Dataverse can still run the previous
build of a plugin package: an execution can run the previous code, and a step just registered on a newly added
class can fail with this error. Run the operation again after a few seconds. Bumping the assembly version does not
change this: Microsoft states that "the version of the plug-in package or plug-in assembly is not a factor in any
upgrade behaviors" (see
[Update a plug-in package](https://learn.microsoft.com/power-platform/developer/howto/cli-create-package#update-a-plug-in-package)).

A pipeline that runs tests against Dataverse right after `vela apply` should add a short pause before them.

If the error persists beyond about a minute, the cause is something else. Check that the package you deployed
contains the plugin class under that full name, then run `vela plan` to compare it with the environment. See
[Troubleshooting](../guides/troubleshooting.md#at-run-time-the-plug-in-type-could-not-be-found-in-the-plug-in-assembly)
and [Build and package plug-in code](https://learn.microsoft.com/power-apps/developer/data-platform/build-and-package).

## Authentication

### Can `plan` and `apply` authenticate with a managed identity or a certificate?

Managed identity: yes. `--url` on its own uses the ambient Azure credential (`DefaultAzureCredential`), which covers
`az login`, environment variables, workload identity and managed identity. It is the recommended default for CI and
coding agents.

Certificate: Vela has no certificate options of its own. A `--connection-string` is passed unchanged to the Dataverse
`ServiceClient`, so you can use the authentication types that its connection strings support. See
[Authentication](../configuration/authentication.md#connection-string).

## Signing

### Why did `apply` reject my signed plugin package?

Dataverse imports a plugin package only when its name carries a solution publisher prefix. When the name lacks it,
`apply` adds the prefix by rewriting the `.nupkg`, which would invalidate the signature, so `apply` refuses a signed
package that needs the rename. Give the package its prefixed name (for example `zava_Zava.Plugins`) before you build
and sign it. See [Signing](../guides/signing.md#signed-packages-and-vela-apply).

### Does `vela sign` support Key Vault or HSM signing?

No. `vela sign` takes a certificate by fingerprint (`CurrentUser\My` store), a `.pfx` file, or a base64 `.pfx`, and
calls `dotnet nuget sign`. Key Vault and HSM signing are out of scope, and `--pfx-credential` is Windows only. See
[Signing](../guides/signing.md).

## Writing plugins and Custom APIs

### What signature must a step handler have?

A step handler is an instance method that returns `void` and takes one parameter. It is bound by method group
(`.Handle(OnUpdated)`). The parameter is one of:

- the stage-typed message context: `IUpdatePostContext<TEntity>` at PostOperation, `IUpdatePreContext<TEntity>` at
  PreValidation or PreOperation (it has `ctx.Request` but no `ctx.Response`);
- the generated `{Handler}Context` (here `OnUpdatedContext`), which adds the typed `ctx.PreImage` and `ctx.PostImage`
  (the plugin must be `partial`, see [VELA014](analyzers.md#vela014));
- `ILocalPluginContext`, for `OnMessage<TMarker>()` steps or when you need nothing typed.

A context for another message or stage is a build error ([VELA015](analyzers.md#vela015)), and every step chain must
end with `.Handle(...)` ([VELA002](analyzers.md#vela002)). A lightweight Custom API handler is
`void Handler(ILocalPluginContext ctx)`.

### What does "can only call RegisterCustomApi(uniqueName, handler) inside RegisterCustomApi()" mean?

Call `RegisterCustomApi("name", ...)` only from inside your `protected override void RegisterCustomApi()`. See
[Lightweight mode](../guides/custom-apis.md#lightweight-mode).

### Why was my workflow-enabled Custom API rejected?

A Custom API with `WorkflowSdkStepEnabled` set must be an action (not a function) and cannot use `Entity`,
`EntityCollection` or `string[]` parameters, nor an `EntityReference` unless it is bound to a table. `Build()` rejects
such a definition with a message that names the API and the parameter, so `plan` fails before any write. See
[Rules checked by `Build()`](../guides/custom-apis.md#rules-checked-when-the-definition-is-built).

### Can the plugin cache do async I/O?

No. `IPluginCacheService` is synchronous. See [Caching](../guides/caching.md).

## Models

### Can `vela models` generate relationship navigation properties, or LINQ and `OrganizationServiceContext`?

Navigation properties: yes. `vela models` generates typed N:1, 1:N and N:N navigation properties, read with
`RetrieveWithRelated` and written with `CreateWithRelated` or `AssociateRelated`. See
[Relationship navigation properties](../guides/models.md#relationship-navigation-properties).

LINQ and `OrganizationServiceContext` are out of scope (lazy loading and change tracking do not fit the plugin
sandbox; use `Query.For<T>()` instead), as are localized multi-language labels and custom class or property casing.
Use `pac modelbuilder` if you need those. See [Limitations](limitations.md).

## Testing

### Does the in-memory test harness support every SDK message?

No. It covers a common set: `Create`, `Update`, `Delete`, `Retrieve`, `RetrieveMultiple`, the `*Multiple` bulk
messages, `Upsert`, `Assign`, `SetState`, `Merge`, `Associate`, `Disassociate`, file blocks, metadata reads,
`ExecuteMultiple` and `ExecuteTransaction`. For any other message, a single-plugin test can inject its own
`IOrganizationService` with `WithOrganizationService(...)`. See [Testing](../guides/testing.md) and
[Limitations](limitations.md#velatesting).

### Is the harness security model on by default?

No. It is opt-in: without a `WithSecurity(...)` call, the in-memory service grants full access. Turn it on only when
you need privilege and access checks. See [Security model in tests](../guides/security-model.md).
