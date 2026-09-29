# Localized messages (i18n)

Surface messages, in particular business error messages, in the current user's language from a plugin handler.
The message service resolves a template by key in the user's UI language (`usersettings.uilanguageid`), falls back
to a default language when there is no translation, formats it with `string.Format`, and caches labels so a run
does not query per message. It is a standard `IMessageService` resolved from the context, consistent with the rest
of the framework (services + DI + caching).

```csharp
using Vela.Context;
using Vela.Localization;

// Register sources once, in the plugin's ConfigureServices override:
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context) =>
    context.GetRequiredService<IMessageService>()
        .AddSource(new DictionaryMessageSource()
            .Add(1033, "ERR_REQUIRED", "The field {0} is required.")
            .Add(1036, "ERR_REQUIRED", "Le champ {0} est obligatoire."));

// In the handler, throw a localized business error:
throw context.BusinessError("ERR_REQUIRED", "name");
// or just resolve the text:
string text = context.GetMessage("ERR_REQUIRED", "name");
```

## Message sources

Sources are consulted in the order they are added; the first with a template for the requested key and language
wins. Two sources ship in the box, and you can add your own by implementing `IMessageSource`.

### In-code dictionary

A fixed catalogue that ships with the assembly, no Dataverse round trip:

```csharp
var source = new DictionaryMessageSource()
    .Add(1033, "GREETING", "Hello {0}")
    .Add(1036, "GREETING", "Bonjour {0}");
```

### Dataverse labels table

Templates stored in a table, so translations can change without a redeploy. The schema is configurable (its logical
name and the key / language / value column names), so it fits any existing labels table:

```csharp
context.GetRequiredService<IMessageService>()
    .AddSource(new DataverseTableMessageSource(
        context,
        context.GetRequiredService<IPluginCacheService>(),
        tableLogicalName: "vela_label",
        keyColumn:        "vela_key",
        languageColumn:   "vela_lcid",   // a whole number or a choice
        valueColumn:      "vela_value",
        cacheFor:         TimeSpan.FromMinutes(10)));
```

The whole table is read once through the elevated system service (so it works regardless of the calling user's
privilege) and kept as an in-memory snapshot, so a plugin run does not query per message.

## Language resolution

`GetMessage` resolves the **initiating** user's UI language from `usersettings.uilanguageid` - the person who
triggered the operation, so the message is in their language even when the plugin runs as another user. When there
is no translation for that language, it falls back to `DefaultLanguageCode` (1033 by default; set it to change the
fallback). Use `GetMessageIn(lcid, key, args)` to resolve an explicit language.

## Handling a missing key

```csharp
// 1) Required: throws InvalidPluginExecutionException when no template exists (user language or default).
string text = context.GetMessage("ERR_REQUIRED", "name");

// 2) Try: branch on presence.
if (context.TryGetMessage("ERR_REQUIRED", out string? text, "name"))
{
    // text is set
}
```

## Caching

The dictionary source needs no caching. The Dataverse table source reads the table once and, by default, memoizes
the snapshot for the execution; pass `cacheFor` to reuse it **across executions** for that duration via the built-in
[`IPluginCacheService`](caching.md). The resolved user language is likewise memoized for the execution, and can be
cached across executions with `ResolveUserLanguageCode(cacheFor)`. Cross-execution caching is best-effort - it lasts
only as long as the Dataverse sandbox worker process, so treat it as a performance optimization, not a guarantee.

## Testing

Sources resolve through the context's system service, so seed the label rows (and a `usersettings` row for the
user's language) into the in-memory org and read them back:

```csharp
Guid user = Guid.NewGuid();
var crm = new InMemoryOrganizationService().Seed(
    new Entity("usersettings", Guid.NewGuid()) { ["systemuserid"] = user, ["uilanguageid"] = 1036 },
    new Entity("vela_label", Guid.NewGuid()) { ["vela_key"] = "GREETING", ["vela_lcid"] = 1036, ["vela_value"] = "Bonjour {0}" });

ILocalPluginContext ctx = new PluginContextBuilder()
    .WithSystemService(crm)
    .WithInitiatingUserId(user)
    .Build();

ctx.GetRequiredService<IMessageService>()
    .AddSource(new DataverseTableMessageSource(ctx, new PluginCacheService(), "vela_label", "vela_key", "vela_lcid", "vela_value"));

Assert.Equal("Bonjour Alice", ctx.GetMessage("GREETING", "Alice"));
```

## See also

- [Caching](caching.md) - the `IPluginCacheService` behind `cacheFor`.
- [Environment variables](environment-variables.md) - the same read-once / `cacheFor` pattern.
- [Dependency injection](dependency-injection.md) - how built-in services are resolved from the context.
