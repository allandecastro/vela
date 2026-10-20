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

Without it, neither harness serves a managed-identity service, so `AcquireManagedIdentityToken` throws an
`InvalidPluginExecutionException` as it does on an environment without one; the message names
`WithManagedIdentityService(...)` for a test and links the Learn page on linking a managed identity for Dataverse. `WithService<IManagedIdentityService>(...)` is rejected: use `WithManagedIdentityService`.

## See also

- [Caching](caching.md) and [Environment variables](environment-variables.md) - the other built-in context services.
- [Authentication](../configuration/authentication.md) - managed identity for the CLI's own Dataverse connection (a separate concern).
- [Write a plugin](plugins.md) - the context helpers available in a handler.
