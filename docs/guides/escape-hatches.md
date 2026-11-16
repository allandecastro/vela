# Escape hatches

Vela is a thin, opt-in layer over the Dataverse SDK. Every abstraction exposes the underlying SDK object, so when
the framework does not cover something you drop one level for that one call and keep Vela's typed models, context
and services for the rest.

This page lists those escape hatches, then shows the raw SDK patterns behind Vela's typed relationship helpers
(`RetrieveWithRelated`, `CreateWithRelated`, `AssociateRelated`; see
[Relationship navigation properties](models.md#relationship-navigation-properties)). The raw patterns are useful for
relationships you did not generate a model for, or when you need full control over the request. It assumes you
know the plugin context described in [Write a plugin](plugins.md).

## The raw organization service

`ILocalPluginContext` hands you the real `IOrganizationService`, so you can execute any `OrganizationRequest`:

```csharp
IOrganizationService service = ctx.Service;   // also: ctx.SystemService, ctx.AsUser(id)
service.Execute(new WhoAmIRequest());
```

Use `ctx.Service` to act as the current user (the step's impersonated user when it has one, otherwise the caller;
their privileges apply), `ctx.SystemService` to run with elevated access, or `ctx.AsUser(id)` to act as a given
user (`ctx.AsUser(ctx.InitiatingUserId)` for the user who started the operation).
`ctx.GetRequiredService<IOrganizationService>()` resolves the same current-user service through
[dependency injection](dependency-injection.md); SYSTEM is never injected implicitly.

These are the SDK services, wrapped only to trace each call, so the SDK's own methods (`Retrieve`, `Create`,
`RetrieveMultiple`, ...) work as usual next to Vela's typed reads (`Retrieve<T>`, `RetrieveByKey`; see
[Read and write records](plugins.md#read-and-write-records)).

## The raw query

A typed `Query.For<TEntity>()` converts implicitly to a `QueryExpression`, and also exposes it explicitly, so you
can hand it to anything the SDK accepts or adjust parts the builder does not cover:

```csharp
QueryExpression query = Query.For<Account>().Select(a => a.Name).Build();   // or the implicit conversion
string fetchXml = Query.For<Account>().Select(a => a.Name).ToFetchXml();     // FetchXML when you need it
```

See [Typed queries](queries.md) for the builder itself.

## The raw execution context

The full `IPluginExecutionContext` is available for anything the typed message context does not surface (shared
variables, correlation id, raw `InputParameters` and `OutputParameters`, and so on):

```csharp
object raw = ctx.PluginExecutionContext.InputParameters["Target"];
```

## Read a record with its related records

Dataverse returns a record together with its related records in a single call through a
`RelationshipQueryCollection`. For generated relationship navigation properties, prefer the typed
`ctx.Service.RetrieveWithRelated<T>(target, q => q.Include(o => o.Nav))`
(see [Relationship navigation properties](models.md#relationship-navigation-properties)). The raw pattern below
stays available for relationships without a generated property, or for full control:

```csharp
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;

IOrganizationService service = ctx.Service;
var childRelationship = new Relationship("contact_customer_accounts");   // account 1:N contacts

var response = (RetrieveResponse)service.Execute(new RetrieveRequest
{
    Target = new EntityReference(Account.EntityLogicalName, accountId),
    ColumnSet = new ColumnSet("name"),
    RelatedEntitiesQuery = new RelationshipQueryCollection
    {
        { childRelationship, Query.For<Contact>().Select(c => c.FirstName, c => c.LastName).Build() },
    },
});

Account account = response.Entity.ToEntity<Account>();
foreach (Entity row in account.RelatedEntities[childRelationship].Entities)
{
    Contact contact = row.ToEntity<Contact>();   // back to the typed model
    ctx.Trace("Contact: {0} {1}", contact.FirstName, contact.LastName);
}
```

This works for a single record. `RetrieveMultiple` does not nest related records, so for a set either flatten with
a link (`Query.For<T>().Link<...>()`, which returns aliased columns) or run a second bulk query on the children
and match them to their parent by its lookup.

## Create a record with its related records

Dataverse creates a parent and its children in one transaction (a deep insert) when you populate
`Entity.RelatedEntities` before `Create`. Because a Vela early-bound class is a normal `Entity`, this is a typed,
one-call insert:

```csharp
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

var account = new Account { Name = "Contoso" };
account.RelatedEntities[new Relationship("contact_customer_accounts")] = new EntityCollection(new List<Entity>
{
    new Contact { FirstName = "Ada", LastName = "Lovelace" },
    new Contact { FirstName = "Grace", LastName = "Hopper" },
});

Guid accountId = ctx.Service.Create(account);   // account + two contacts, in one transaction
```

The same shape associates existing records over an N:N relationship (populate the collection with references to
records that already exist). For the platform's rules, see
[Create related entities in one operation](https://learn.microsoft.com/power-apps/developer/data-platform/org-service/entity-operations-create#create-related-entities-in-one-operation).

### The typed form

When you generate the related tables together, `vela models` also emits typed relationship navigation properties
(a single parent for N:1, a collection for 1:N and N:N), so the deep insert above becomes:

```csharp
using Vela.Context;

var account = new Account { Name = "Contoso" };
account.Contacts.Add(new Contact { FirstName = "Ada", LastName = "Lovelace" });
account.Contacts.Add(new Contact { FirstName = "Grace", LastName = "Hopper" });

Guid accountId = ctx.Service.CreateWithRelated(account);   // account + two contacts, one transaction
```

- `CreateWithRelated` projects the populated 1:N collections into `RelatedEntities` for you.
- `RetrieveWithRelated<T>(target, q => q.Include(...))` reads them back typed (single record; N:1, 1:N and N:N).
- `AssociateRelated(entity)` associates the existing records in the N:N collections.

A navigation property is not a column, so it cannot be used in `FilterOn`, `Select`, `Where` or `WithPreImage`
([VELA010](../reference/analyzers.md#vela010) catches that); select the lookup column (`PrimaryContactId`) instead.

## See also

- [Typed queries](queries.md): the `Query.For<TEntity>()` builder and typed columns.
- [Write a plugin](plugins.md): the context, services and images.
- [Early-bound models](models.md): generating the typed classes used above.
