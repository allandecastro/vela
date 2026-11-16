# Typed queries

Vela builds and runs Dataverse queries from your [early-bound models](models.md), so a table or column typo is a
build error instead of a runtime `FaultException`. This page covers the typed query builder (`Query.For<T>()`),
FetchXML output, running a query, and the typed reads of one record. The query types are in the `Vela.Querying`
namespace.

## Build a query

`Query.For<TEntity>()` returns a typed builder: the table comes from the type's `[EntityLogicalName]`, every column
from a selector, and the builder converts implicitly to `QueryExpression`:

```csharp
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Zava.Shared.Models.Generated;

QueryExpression query = Query.For<Account>()
    .Select(a => a.AccountId, a => a.Name)
    .Where(a => a.Name, ConditionOperator.NotNull)
    .OrderBy(a => a.Name);
```

To add typed parts to an existing `QueryExpression`, use the extensions on it:

```csharp
QueryExpression query = new QueryExpression(Account.EntityLogicalNameValue)
    .Select<Account>(x => x.AccountId, x => x.Name)
    .Where<Account>(x => x.Name, ConditionOperator.NotNull);
```

### Links

Give the target type once, then configure the join (columns, criteria, nested links) through a typed action:

```csharp
QueryExpression query = Query.For<Contact>()
    .Where(c => c.FullName, ConditionOperator.BeginsWith, "J")
    .Link<Account>(c => c.ParentCustomerId, a => a.AccountId, JoinOperator.Inner, "acct",
        acct => acct.Select(a => a.Name)
                    .Where(a => a.StateCode, ConditionOperator.Equal, 0));
```

### Conditions over a collection

For an `In` or `NotIn` over an existing collection, use `WhereIn(column, values)` or `WhereNotIn(column, values)`
(and `ConditionIn`/`ConditionNotIn` inside a `WhereAll`/`WhereAny` filter). They take an `IEnumerable<T>`, so a
`Guid[]` or a `List<Guid>` passes directly, and each element becomes a separate condition value:

```csharp
Guid[] ids = GetIds();
QueryExpression query = Query.For<Account>()
    .Select(a => a.Name)
    .WhereIn(a => a.AccountId, ids);   // not .Where(a => a.AccountId, ConditionOperator.In, ids)
```

### Other builder methods

| Method | Purpose |
| --- | --- |
| `SelectAll()` | Every column. |
| `OrderByDescending(column)` | Descending order. |
| `Top(n)` | At most `n` rows. |
| `Distinct()` | Distinct rows. |
| `Page(n, count)` | One page of `count` rows. |
| `WithTotalRecordCount()` | Return the total record count. |
| `WhereColumns(a, op, b)` | Compare two columns. |
| `WhereAll(...)` / `WhereAny(...)` | A nested AND / OR filter. |
| `WhereLink<TLinked>(alias, ...)` | A condition on a linked table (for example, records not in a set). |
| `WhereRelated<TTo>(..., JoinOperator.Any/All/NotAny/NotAll, ...)` | A condition on related rows; several calls combine with AND. |
| `GroupBy(column, alias)`, `Count`, `Sum`, `Average`, `Min`, `Max(column, alias)` | Aggregates. |

## Emit FetchXML

Some messages and tools need a FetchXML string (rollups, saved queries, FetchXML Builder). Build the query typed
and emit it with `ToFetchXml()`:

```csharp
string fetchXml = Query.For<Contact>()
    .Select(c => c.FullName)
    .Where(c => c.ParentCustomerId, ConditionOperator.Equal, accountId)
    .OrderBy(c => c.FullName)
    .ToFetchXml();
```

`ToFetchXml()` is also an extension on a plain `QueryExpression`. The builder has FetchXML-only options that a
`QueryExpression` cannot hold, so they appear only in the emitted FetchXML: `.LateMaterialize()`,
`.AggregateLimit(n)`, `.CountColumnDistinct(column, alias)`, `.OrderByAggregate(alias)`, `.UtcDateGrouping()`,
`.RowAggregate(...)`, `.RawChoiceOrder()`, `.FromRetainedData()` and `.SqlOptions("...")`.

Values are written as Dataverse expects them:

- `OptionSetValue`, `Money` and `EntityReference` values emit their underlying value: the choice integer, the money
  decimal, the lookup GUID. An early-bound choice enum passed to a typed `Where`, `WhereIn` or `Condition` becomes
  its integer in the `QueryExpression` too.
- A UTC `DateTime` is written with milliseconds and a `Z` (a local one is converted to UTC first). An `Unspecified`
  one carries no zone, so Dataverse reads it in the user's time zone.
- A query's `NoLock` is carried through as `no-lock='true'`.

In tests, the in-memory harness runs the emitted FetchXML through the same engine as the `QueryExpression`, so both
return the same rows. The FetchXML it supports and rejects is listed in [Testing](testing.md#queries).

## Run a query

Run the builder directly against an `IOrganizationService`, without `.Build()` or hand-written paging:

```csharp
// Every match, paged automatically (follows MoreRecords to the end).
List<Account> all = Query.For<Account>().Select(a => a.Name).ToList(service);

// The first match, or null: one retrieve, with Top set to 1.
Account? one = Query.For<Account>()
    .Where(a => a.Name, ConditionOperator.Equal, "Contoso")
    .FirstOrDefault(service);

// Service-first form, equivalent to ToList.
List<Account> same = service.RetrieveAll(Query.For<Account>().SelectAll());
```

All three return the early-bound `TEntity` (through `ToEntity<T>()`). None of them changes the query:
`ToList`/`RetrieveAll` page with their own paging and restore the query's `PageInfo`, and `FirstOrDefault` restores
its `TopCount`, so the same `TypedQuery` can run again. A query with `Top(n)` is read in one retrieve, without
paging.

### Scalar aggregates

A scalar aggregate runs and reads back in one typed call, with no alias string and no `AliasedValue` to unwrap:

```csharp
int     count = Query.For<Account>().Where(a => a.StateCode, ConditionOperator.Equal, 0)
                     .CountScalar(service, a => a.AccountId);                    // matching rows
decimal? total = Query.For<SalesOrder>().SumScalar(service, o => o.TotalAmount);    // null when none match
decimal? avg   = Query.For<SalesOrder>().AverageScalar(service, o => o.TotalAmount);

DateTime? earliest = Query.For<Task>().MinScalar(service, t => t.ScheduledStart);   // DateTime? column: DateTime?
int?      largest  = Query.For<Account>().MaxScalar(service, a => a.NumberOfEmployees);
decimal?  topDeal  = Query.For<SalesOrder>().MaxScalar(service, o => o.TotalAmount); // Money column: decimal?
```

- `CountScalar` returns `int`.
- `SumScalar` and `AverageScalar` return `decimal?`: `null` tells "no rows" apart from a real `0`.
- `MinScalar` and `MaxScalar` return the **column's own type**, inferred from the selector: `DateTime?` for a
  `DateTime?` column, `int?` for an `int?` column, `decimal?` (the amount) for a money column.
- All return `null` when nothing matches.

For **grouped** aggregates (`GroupBy(...)` with several aggregates), use the alias methods (`.Sum(column, "alias")`)
and read the rows.

## Read one record

When you know the record's id, `service.Retrieve<TEntity>(id, columns...)` (namespace `Vela.Context`) reads it
early-bound, with the table taken from `TEntity` and the columns from typed selectors:

```csharp
using Vela.Context;

Account account = service.Retrieve<Account>(id, a => a.Name, a => a.Telephone1);
```

It sends a real `Retrieve`, not a query: the steps registered on `Retrieve` run, and a missing record faults as it
does with the SDK `Retrieve`. At least one column is required; there is no all-columns form.

Choose the read by what you have:

| You have | Use |
| --- | --- |
| The id | `service.Retrieve<Account>(id, a => a.Name)` |
| An alternate key | `service.RetrieveByKey<Account>(Account.ByAccountNumberKey("A-001"), a => a.Name)` (the key factory is generated, see [Models](models.md); with no column, every column is returned) |
| Criteria | `Query.For<Account>().Where(...).Select(...)` with `FirstOrDefault` or `ToList` (above) |

## See also

- [Write a plugin](plugins.md): where these queries run.
- [Testing with Vela.Testing](testing.md): the in-memory service runs the same queries.
