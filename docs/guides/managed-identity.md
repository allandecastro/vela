# Managed identity

Acquire a token for an external API from inside a plugin using the environment's **managed identity**, so you call
downstream services without storing a secret. The token is obtained through the built-in `IManagedIdentityService`,
surfaced on the context as `ctx.AcquireManagedIdentityToken(...)`.

```csharp
// Token for the default application user, for the requested scopes.
string token = ctx.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" });

// Or for a specific application user (federated identity credential).
string userToken = ctx.AcquireManagedIdentityToken(applicationUserId, new[] { "https://graph.microsoft.com/.default" });
```

Use the token as a bearer credential when calling the external API (for example on an `HttpClient` request).

`AcquireManagedIdentityToken` throws when the environment has no managed identity available. Scaffold a solution
with `vela init --managed-identity` (it adds the Integrations project) to wire the managed-identity signing/registration path, and configure the
managed identity + federated credential on the environment (see
[Use managed identity for server-to-server authentication](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/managed-identity-overview)).

## Link the plugin package at deployment

The platform side is set up outside Vela, once per environment: the app registration or user-assigned managed
identity, its federated credential, the signed package and the `managedidentity` record in Dataverse. Follow
[Set up managed identity for Dataverse plug-ins](https://learn.microsoft.com/power-platform/admin/set-up-managed-identity)
for these steps. `vela sign` signs the package.

The last step, binding the plugin package to the `managedidentity` record, is part of `vela plan` and `vela apply`:
pass the record's id (`managedidentityid`) with `--managed-identity`. Each environment has its own record, so the id
changes per environment while the package stays the same:

```bash
vela plan  --package ./artifacts/zava_Zava.Integrations.1.0.0.nupkg --solution ZavaSales \
  --url https://zava-test.crm.dynamics.com --managed-identity <test-managedidentityid> --detailed-exitcode
vela apply --package ./artifacts/zava_Zava.Integrations.1.0.0.nupkg --solution ZavaSales \
  --url https://zava-test.crm.dynamics.com --managed-identity <test-managedidentityid> --auto-approve
```

- `plan` fails (exit 4) when no `managedidentity` record has that id in the environment, before anything is written.
- `plan` lists each published package as already linked, to link, or to change from the managed identity it is
  linked to now. A link to write is a pending change (`hasChanges`, exit 8 with `--detailed-exitcode`).
- `apply` sets `pluginpackage.managedidentityid` right after the package publish, so a package created by the same run
  is linked too. With `--solution`, the update carries the solution like the other writes.
- Without `--managed-identity`, the link is neither read nor changed: a package linked by hand keeps its link.

Only the plugin packages of the run are linked; Vela deploys packages, not standalone plugin assemblies. The JSON
fields are in [Machine-readable output](../reference/machine-readable-output.md); the option is described with the other
plan/apply options in [Plan & Apply](plan-apply.md).

## Testing

Provide a fake managed-identity service on the context so a handler under test can acquire a token without a real
environment:

```csharp
ILocalPluginContext ctx = PluginContext.For<Account>()
    .WithManagedIdentityService(fake)
    .OnCreate()
    .Build();
```

In a pipeline test, `PluginPipeline.WithManagedIdentityService(fake)` hands the fake to every plugin and Custom API
the pipeline runs, nested executions included:

```csharp
PluginPipeline pipeline = PluginPipeline.Empty()
    .Register<AccountSyncPlugin>()
    .WithManagedIdentityService(fake);
```

Without it, neither harness serves a managed-identity service, so `AcquireManagedIdentityToken` throws as it does on
an environment without one. `WithService<IManagedIdentityService>(...)` is rejected: use `WithManagedIdentityService`.

## See also

- [Caching](caching.md) and [Environment variables](environment-variables.md) - the other built-in context services.
- [Authentication](../configuration/authentication.md) - managed identity for the CLI's own Dataverse connection (a separate concern).
- [Write a plugin](plugins.md) - the context helpers available in a handler.
