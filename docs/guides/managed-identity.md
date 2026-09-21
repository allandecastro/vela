# Managed identity

Acquire a token for an external API from inside a plugin using the environment's **managed identity**, so you call
downstream services without storing a secret. The token is obtained through the built-in `IManagedIdentityService`,
surfaced on the context as `ctx.AcquireManagedIdentityToken(...)`.

```csharp
// Token for the default application user, for the requested scopes.
string token = ctx.AcquireManagedIdentityToken(new[] { "https://graph.microsoft.com/.default" });

// Or for a specific application user (federated identity credential).
string token = ctx.AcquireManagedIdentityToken(applicationUserId, new[] { "https://graph.microsoft.com/.default" });
```

Use the token as a bearer credential when calling the external API (for example on an `HttpClient` request).

`AcquireManagedIdentityToken` throws when the environment has no managed identity available. Scaffold a solution
with `vela init --managed-identity` to wire the managed-identity signing/registration path, and configure the
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

## See also

- [Caching](caching.md) and [Environment variables](environment-variables.md) - the other built-in context services.
- [Authentication](../configuration/authentication.md) - managed identity for the CLI's own Dataverse connection (a separate concern).
- [Write a plugin](plugins.md) - the context helpers available in a handler.
