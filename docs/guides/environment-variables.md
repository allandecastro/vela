# Environment variables

Read Dataverse [environment variables](https://learn.microsoft.com/en-us/power-apps/maker/data-platform/environmentvariables)
from a plugin handler, typed, with sensible handling for a missing value and optional caching. The effective value
is the environment variable **value** when one is set, otherwise the definition's **default value** - read through
the elevated system service, so it works regardless of the calling user's privilege.

```csharp
using Vela.Context;

string url     = ctx.GetEnvironmentVariable("adc_ApiBaseUrl");           // effective string value
int    retries = ctx.GetEnvironmentVariableAs<int>("adc_MaxRetries");    // int/long/decimal/double/bool/Guid/DateTime
MyCfg  config  = ctx.GetEnvironmentVariableAs<MyCfg>("adc_Config");      // a JSON-typed variable -> your type
```

## Handling a missing value

"Missing" means the definition does not exist, or it has no value record and no default. Choose per call site:

```csharp
// 1) Required: throws InvalidPluginExecutionException when there is no effective value.
string url = ctx.GetEnvironmentVariable("adc_ApiBaseUrl");

// 2) Fallback: return your own default instead of throwing.
int retries = ctx.GetEnvironmentVariableAs<int>("adc_MaxRetries", fallback: 3);

// 3) Try: branch on presence. Also returns false when the value cannot be converted to T.
if (ctx.TryGetEnvironmentVariable("adc_FeatureFlag", out string? flag))
{
    // flag is set
}
```

## Types

The definition's type drives conversion for `GetEnvironmentVariableAs<T>`:

| Dataverse type | `GetEnvironmentVariableAs<T>` |
| --- | --- |
| String | any `T` (raw string, or parsed/deserialized as below) |
| Number | `int`, `long`, `decimal`, `double`, ... (invariant parse) |
| Boolean | `bool` (accepts `true`/`false`, `yes`/`no`, `1`/`0`) |
| JSON | a complex `T` (deserialized with JSON) |
| Data Source | raw string (a connection-reference JSON) |
| Secret | not supported - reading one throws (secrets resolve through Azure Key Vault) |

`Guid` and `DateTime` (invariant / round-trip) are also supported, as are enums (by name).

## Caching

By default a value is read **fresh once per execution**: repeated reads of the same variable in one plugin run hit
Dataverse once, and the next execution reads it again (so an admin's change is picked up). The call itself does the
lookup - there is no separate "check the cache" step.

For a hot path, pass `cacheFor` to reuse the value **across executions** for that duration, via the built-in
[`IPluginCacheService`](caching.md):

```csharp
// Cache across executions for 5 minutes (best-effort: lives while the sandbox worker is alive).
string url = ctx.GetEnvironmentVariable("adc_ApiBaseUrl", cacheFor: TimeSpan.FromMinutes(5));
```

With `cacheFor`, the read is: look in the cache -> return on a non-expired hit, otherwise query Dataverse, store
with the TTL, and return. Cross-execution caching is best-effort - it lasts only as long as the Dataverse sandbox
worker process, so treat it as a performance optimization, not a guarantee.

## Testing

The reader resolves rows through the context's system service, so seed the definition (and optional value) into the
in-memory org and read them back:

```csharp
Guid def = Guid.NewGuid();
var crm = new InMemoryOrganizationService().Seed(
    new Entity("environmentvariabledefinition", def)
    {
        ["schemaname"] = "adc_ApiBaseUrl",
        ["type"] = new OptionSetValue(100000000), // String
        ["defaultvalue"] = "https://default",
    },
    new Entity("environmentvariablevalue", Guid.NewGuid())
    {
        ["environmentvariabledefinitionid"] = new EntityReference("environmentvariabledefinition", def),
        ["value"] = "https://override",
        ["statecode"] = new OptionSetValue(0),
    });

ILocalPluginContext ctx = new PluginContextBuilder().WithSystemService(crm).Build();

Assert.Equal("https://override", ctx.GetEnvironmentVariable("adc_ApiBaseUrl"));
```

## See also

- [Caching](caching.md) - the `IPluginCacheService` behind `cacheFor`.
- [Write a plugin](plugins.md) - the context helpers available in a handler.
- [Dependency injection](dependency-injection.md) - how built-in services are resolved from the context.
