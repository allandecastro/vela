# Cookbook

Short recipes for the patterns you reach for most, for developers who already have a scaffolded solution (see the
[Quickstart](../getting-started/quickstart.md)). Each recipe compiles against the current framework and links to the
guide that covers it in full.

## Single-step update plugin

A PostOperation step that fires when an Update sends `Name`. See [Write a plugin](plugins.md).

[!code-csharp[](../samples/Vela.DocSamples/CookbookSample.cs#single-step)]

`vela add plugin AccountPostUpdate --entity Account --message Update --filtering-attributes Name` generates the same
shape. See [Add a component (`vela add`)](add.md).

## Several steps in one class

[!code-csharp[](../samples/Vela.DocSamples/CookbookSample.cs#multi-step)]

Two steps on the **same** trigger need something that tells them apart (filtering columns, images, relationship
scope) or a `WithStepKey("...")`. See [Several steps in one class](plugins.md#several-steps-in-one-class).

## Read a record early-bound

`Retrieve<T>` (namespace `Vela.Context`) reads one record by id, with the table taken from the model and the columns
from typed selectors. See [Typed queries](queries.md).

[!code-csharp[](../samples/Vela.DocSamples/CookbookSample.cs#typed-read)]

## Bound Custom API

A lightweight Custom API bound to the account table, handled by a method. See [Write a Custom API](custom-apis.md)
for the typed request and response form.

[!code-csharp[](../samples/Vela.DocSamples/CookbookSample.cs#custom-api)]

## Dependency injection

Register services in `ConfigureServices`:

[!code-csharp[](../samples/Vela.DocSamples/CookbookSample.cs#services)]

Resolve them in a handler with `ctx.GetRequiredService<T>()`:

[!code-csharp[](../samples/Vela.DocSamples/CookbookSample.cs#resolve)]

See [Dependency injection](dependency-injection.md) and [Caching](caching.md).

## Test without a connection

Register the plugin in a `PluginPipeline`, write through its service, then assert on what ran and on the resulting
data. See [Testing with Vela.Testing](testing.md).

[!code-csharp[](../samples/Vela.DocSamples/CookbookSample.cs#test)]
