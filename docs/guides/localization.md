---
_description: "Resolve messages in the user's or the organization's language"
---

# Localized messages

The built-in `IMessageService` (namespace `Vela.Localization`) lets a plugin or Custom API show messages, in
particular business errors, in the user's language. It resolves a template by key in the user's UI language, falls
back to the organization's base language and then to `DefaultLanguageCode`, formats it with `string.Format`, and
caches the labels so a run does not query per message.

This page covers registering the project's messages, the message sources Vela ships, how the language is resolved,
and testing. It builds on [Dependency injection](dependency-injection.md), where `IMessageService` is one of the
built-in services.

## Register the project's messages once

Messages belong to the project, not to one plugin, so register their source once for the whole assembly with an
[`IServiceRegistrar`](dependency-injection.md#share-services-across-the-assembly). Keep the keys and the source
in one class.

A project created with `vela init` already has both, empty, in each plugin project: `Messages.cs` holds the
`ProjectMessages` class (add the keys and their texts there) and `ProjectServices.cs` holds the registrar, with the
assembly attribute and a commented line that registers `ProjectMessages.Source`; uncomment it once the first key
exists. Filled in, they look like this:

[!code-csharp[](../samples/Vela.DocSamples/LocalizationSample.cs#messages)]

Point the assembly at the registrar, once, at assembly scope (the generated `ProjectServices.cs` already does):

[!code-csharp[](../samples/Vela.DocSamples/LocalizationSample.cs#registrar)]

The registrar runs for every plugin and Custom API of the assembly before its own `ConfigureServices`, so a handler
throws a localized business error without registering anything itself:

[!code-csharp[](../samples/Vela.DocSamples/LocalizationSample.cs#plugin)]

`ctx.BusinessError(key, args)` returns a `PluginBusinessException` carrying the localized text, ready to throw (see
[Errors](plugins.md#errors)). `ctx.GetMessage(key, args)` resolves the text without throwing it. The key constants
make a typo a compile error, and switching the project to another source (a
[Dataverse labels table](#dataverse-labels-table) or [String (RESX) web resources](#string-resx-web-resources))
changes only the registrar.

A plugin can still add a source in its own `ConfigureServices` override for keys only it uses. Sources are consulted
in the order they are added, and the registrar's comes first, so a key the project source defines keeps the
project's text.

## Message sources

Sources are consulted in the order they are added; the first with a template for the requested key and language
wins. Three sources ship with Vela: an in-code dictionary, a Dataverse labels table and String (RESX) web resources.
You can add your own by implementing `IMessageSource`. Each snippet below goes in the registrar's `Register`, where
`context` is the live plugin context.

### In-code dictionary

A fixed catalogue that ships with the assembly, with no Dataverse round trip:

```csharp
context.GetRequiredService<IMessageService>()
    .AddSource(new DictionaryMessageSource()
        .Add(1033, "GREETING", "Hello {0}")
        .Add(1036, "GREETING", "Bonjour {0}"));
```

### Dataverse labels table

Templates stored in a table, so translations can change without a redeploy. The schema is configurable (the table's
logical name and the key, language and value column names), so it fits any existing labels table:

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

The whole table is read once through the system service (so it works whatever the calling user's privileges) and
kept as an in-memory snapshot, so a plugin run does not query per message. Deactivated rows (`statecode` other than
0) are ignored. The cross-execution snapshot is keyed by the table and its three column names, so two sources that
map the same table differently never share a snapshot.

### String (RESX) web resources

Reuse the **String (RESX) web resources** a model-driven app already ships for client-side strings
(`Xrm.Utility.getResourceString`), so the same translations serve the client and your plugins. Follow the platform
naming convention `<baseName>.<LCID>.resx`, one web resource per language (for example
`acme_/strings/messages.1033.resx` for English and `acme_/strings/messages.1036.resx` for French):

```csharp
context.GetRequiredService<IMessageService>()
    .AddSource(new ResxWebResourceMessageSource(
        context,
        context.GetRequiredService<IPluginCacheService>(),
        baseName: "acme_/strings/messages",   // the per-language resource is <baseName>.<LCID>.resx
        cacheFor: TimeSpan.FromMinutes(10)));
```

Pass the **base name without the language suffix**. For the resolved language, the source reads the matching web
resource once through the system service, decodes its base64 content and parses the resx XML (plain `<data>` string
entries only; typed or binary resources such as images are skipped).

- When a language's resource is absent, the source returns nothing and resolution moves on to the organization's
  base language, then `DefaultLanguageCode`. You only need the base-language `.resx` plus the languages you
  translate.
- A resource that is present but malformed (bad base64 or invalid resx XML) is written to the Plugin Trace Log and
  treated as empty. Neither case breaks resolution.

## Language resolution

`GetMessage` uses the **initiating** user's UI language, read from `usersettings.uilanguageid`: the person who
triggered the operation, so the message is in their language even when the plugin runs as another user. A user with
no setting gets the organization's base language.

When no source has a template in that language, resolution falls back to the organization's base language
(`organization.languagecode`, read only when needed and memoized for the execution), then to `DefaultLanguageCode`
(1033 by default; set it on the `IMessageService` to change the last-resort language). Use
`ctx.GetMessageIn(lcid, key, args)` to resolve an explicit language.

## Handling a missing key

```csharp
// 1) Required: throws InvalidPluginExecutionException when no template exists
//    (user, organization or default language).
string text = ctx.GetMessage("ERR_REQUIRED", "name");

// 2) Try: branch on presence. A template whose placeholders do not match the arguments still throws.
if (ctx.TryGetMessage("ERR_REQUIRED", out string? optional, "name"))
{
    // optional is set
}
```

## Caching

The dictionary source needs no caching. The Dataverse table and RESX sources read their data once and, by default,
memoize it for the execution; pass `cacheFor` to reuse it **across executions** for that duration through the
built-in [`IPluginCacheService`](caching.md). The resolved user language is also memoized for the execution, and
can be cached across executions with `ResolveUserLanguageCode(cacheFor)` (and the organization language with
`ResolveOrganizationLanguageCode(cacheFor)`). Cross-execution caching is best-effort: it lasts only as long as the
sandbox worker process, so treat it as a performance optimization, not a guarantee (see
[Scope and lifetime](caching.md#scope-and-lifetime)).

## Testing

A plugin test with `PluginPipeline` runs the assembly's registrar too, so the project's messages are there. Seed a
`usersettings` row to pick the user's language:

[!code-csharp[](../samples/Vela.DocSamples/LocalizationSample.cs#test)]

To test a Dataverse table source, seed its label rows the same way (and an `organization` row with a
`languagecode` to exercise the organization fallback): the plugins the pipeline runs read them through their system
service.

See [Testing with Vela.Testing](testing.md) for the harness.

## See also

- [Caching](caching.md): the `IPluginCacheService` behind `cacheFor`.
- [Environment variables](environment-variables.md): the same read-once and `cacheFor` pattern.
- [Dependency injection](dependency-injection.md): how built-in services are resolved from the context.
