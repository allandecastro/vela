---
_description: "Read Dataverse environment variables, typed, with a missing-value policy and optional caching"
---

# Environment variables

Read Dataverse [environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables)
from a plugin handler, typed, with a choice of how to handle a missing value and optional caching. This page covers
reading, converting, caching and testing them.

The effective value is the environment variable's **value** when one is set, otherwise the definition's **default
value**. Vela reads it through the system service, so it works whatever the calling user's privileges.

```csharp
using Vela.Context;

string url     = ctx.GetEnvironmentVariable("adc_ApiBaseUrl");           // effective string value
int    retries = ctx.GetEnvironmentVariableAs<int>("adc_MaxRetries");    // int/long/decimal/double/bool/Guid/DateTime
MyCfg  config  = ctx.GetEnvironmentVariableAs<MyCfg>("adc_Config");      // a JSON variable, deserialized
```

## Handling a missing value

"Missing" means the definition does not exist, or it has no value record and no default. Choose per call site:

```csharp
// 1) Required: throws InvalidPluginExecutionException when there is no effective value.
string url = ctx.GetEnvironmentVariable("adc_ApiBaseUrl");

// 2) Fallback: return your own default instead of throwing.
int retries = ctx.GetEnvironmentVariableAs<int>("adc_MaxRetries", fallback: 3);

// 3) Try: branch on presence. TryGetEnvironmentVariableAs<T> also returns false when the value does not convert.
if (ctx.TryGetEnvironmentVariableAs<bool>("adc_FeatureFlag", out bool enabled) && enabled)
{
    // the flag is set and true
}
```

The string reads have the same three forms: `GetEnvironmentVariable(name)`, `GetEnvironmentVariable(name, fallback)`
and `TryGetEnvironmentVariable(name, out string value)`.

## Types

`GetEnvironmentVariableAs<T>` converts the effective value (stored as text) to the `T` you ask for, whatever the
definition's type; a value that does not convert throws `InvalidPluginExecutionException`. The usual pairings:

| Dataverse type | `GetEnvironmentVariableAs<T>` |
| --- | --- |
| String | any `T` (raw string, or parsed/deserialized as below) |
| Number | `int`, `long`, `decimal`, `double`, ... (invariant parse) |
| Boolean | `bool` (accepts `true`/`false`, `yes`/`no`, `1`/`0`, ignoring case) |
| JSON | a complex `T` (deserialized with JSON) |
| Data Source | raw string (a connection-reference JSON) |
| Secret | not supported: reading one throws `InvalidPluginExecutionException` (secrets resolve through Azure Key Vault) |

`Guid` and `DateTime` (invariant / round-trip) are also supported, as are enums (by name, ignoring case).

## Caching

By default a value is read **fresh once per execution**: repeated reads of the same variable in one plugin run hit
Dataverse once, and the next execution reads it again, so an admin's change is picked up. The call itself does the
lookup: there is no separate "check the cache" step.

For a hot path, pass `cacheFor` to reuse the value **across executions** for that duration, via the built-in
[`IPluginCacheService`](caching.md):

```csharp
// Cache across executions for 5 minutes (best-effort: lives while the sandbox worker is alive).
string url = ctx.GetEnvironmentVariable("adc_ApiBaseUrl", cacheFor: TimeSpan.FromMinutes(5));
```

With `cacheFor`, the read looks in the cache and returns a value that has not expired; otherwise it queries
Dataverse, stores the value with the TTL, and returns it. Cross-execution caching is best-effort: it lasts only as
long as the Dataverse sandbox worker process, so treat it as a performance optimization, not a guarantee.

## Testing

The reader resolves the rows through the context's system service. Seed the definition (and its value, if any) into
the in-memory store, then read them back:

[!code-csharp[](../samples/Vela.DocSamples/TestingSample.cs#environment-variables)]

In a [`PluginPipeline`](testing.md) test, seed the same rows with `pipeline.Seed(...)`: every plugin the pipeline
runs reads them. To test `cacheFor`, move the harness clock past the TTL (see [Caching](caching.md#test-a-ttl)).

## See also

- [Caching](caching.md): the `IPluginCacheService` behind `cacheFor`.
- [Write a plugin](plugins.md): the context helpers available in a handler.
- [Dependency injection](dependency-injection.md): how built-in services are resolved from the context.
