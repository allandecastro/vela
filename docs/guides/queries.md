# Typed queries

Use typed extensions on `QueryExpression` to avoid string literals:

```csharp
using Vela.Querying;
using Zava.Shared.Models.Generated;
using Microsoft.Xrm.Sdk.Query;

QueryExpression query = new QueryExpression(Account.EntityLogicalName)
    .Select<Account>(x => x.AccountId, x => x.Name)
    .Where<Account>(x => x.Name, ConditionOperator.NotNull);
```

`Query.For<TEntity>()` returns a typed builder so the whole chain is typed **without repeating the entity type** (the entity name comes from `[EntityLogicalName]`), and it converts implicitly to `QueryExpression`:

```csharp
QueryExpression query = Query.For<Account>()
    .Select(a => a.AccountId, a => a.Name)
    .Where(a => a.Name, ConditionOperator.NotNull)
    .OrderBy(a => a.Name);
```

Links are typed too - give the target type once and configure the join (columns, criteria, nested links) through a typed action:

```csharp
QueryExpression query = Query.For<Contact>()
    .Where(c => c.FullName, ConditionOperator.BeginsWith, "J")
    .Link<Account>(c => c.ParentCustomerId, a => a.AccountId, JoinOperator.Inner, "acct",
        acct => acct.Select(a => a.Name)
                    .Where(a => a.StateCode, ConditionOperator.Equal, 0));
// crm.RetrieveMultiple(query) - the in-memory engine and Dataverse both accept it
```

For an `In`/`NotIn` over an existing collection, use `WhereIn(column, values)` / `WhereNotIn(column, values)`
(and `ConditionIn`/`ConditionNotIn` inside a `WhereAll`/`WhereAny` filter). They take an `IEnumerable<T>`, so a
`Guid[]` or `List<Guid>` passes directly - each element becomes a distinct condition value:

```csharp
Guid[] ids = GetIds();
QueryExpression query = Query.For<Account>()
    .Select(a => a.Name)
    .WhereIn(a => a.AccountId, ids);   // not .Where(a => a.AccountId, ConditionOperator.In, ids)
```

Also available on the builder: `Distinct()`, `Page(n, count)`, `WithTotalRecordCount()`, `WhereColumns(a, op, b)`
(compare two columns), `WhereLink<TLinked>(alias, …)` (conditions on a join, e.g. records-not-in-a-set),
`WhereRelated<TTo>(…, JoinOperator.Any/All/NotAny/NotAll, …)` (several calls combine with AND), and aggregates
`GroupBy(…)/Count/Sum/Average/Min/Max(…, alias)`.

Need **FetchXML** (for the messages/tools that require the string - Rollup, saved queries, pasting from FetchXML
Builder)? Author it typed and emit it with `ToFetchXml()` - the same query, checked at compile time, no IDE
extension needed:

```csharp
string fetchXml = Query.For<Contact>()
    .Select(c => c.FullName)
    .Where(c => c.ParentCustomerId, ConditionOperator.Equal, accountId)
    .OrderBy(c => c.FullName)
    .ToFetchXml();
// FetchXML-only extras QueryExpression can't hold (emit-only): .LateMaterialize(), .AggregateLimit(n),
// .CountColumnDistinct(col, alias), .OrderByAggregate(alias), .UtcDateGrouping(), .RowAggregate(...),
// .RawChoiceOrder(), .FromRetainedData(), .SqlOptions("…")
```

`ToFetchXml()` is also an extension on a plain `QueryExpression`. A table/column typo is a build error, not a
runtime `FaultException`. In tests, the in-memory harness runs the emitted FetchXML through the same engine path as
the `QueryExpression`, so both return the same rows; the FetchXML it supports (and rejects) is listed in
[Testing](testing.md#queries).

Condition values that are Dataverse types (`OptionSetValue`, `Money`, `EntityReference`) emit their underlying
value - the choice integer, the money decimal, the lookup GUID - and a query's `NoLock` is carried through as
`no-lock='true'`. An early-bound choice enum passed to a typed `Where`/`WhereIn`/`Condition` becomes its integer
in the `QueryExpression` too. A UTC `DateTime` is written with milliseconds and a `Z` (a local one is converted
to UTC first); an `Unspecified` one carries no zone, so Dataverse reads it in the user's time zone.

## Running a typed query

Execute the builder directly against an `IOrganizationService` - no `.Build()`, no hand-rolled paging:

```csharp
// every match, auto-paged (follows MoreRecords to the end)
List<Account> all = Query.For<Account>().Select(a => a.Name).ToList(service);

// the first match, or null - a single retrieve (caps Top at 1)
Account? one = Query.For<Account>()
    .Where(a => a.Name, ConditionOperator.Equal, "Contoso")
    .FirstOrDefault(service);

// service-first form, equivalent to ToList
List<Account> same = service.RetrieveAll(Query.For<Account>().SelectAll());
```

All three return the early-bound `TEntity` (via `ToEntity<T>()`); you never touch `.Build()` or `AliasedValue`
for a plain read. None of them leaves the query changed: `ToList`/`RetrieveAll` page with their own paging and restore
the query's `PageInfo`, and `FirstOrDefault` restores its `TopCount`, so the same `TypedQuery` can be run again.

**Scalar aggregates** run and read back in one typed call - no alias string to repeat, no `AliasedValue` to
unwrap:

```csharp
int     count = Query.For<Account>().Where(a => a.StateCode, ConditionOperator.Equal, 0)
                     .CountScalar(service, a => a.AccountId);       // matching rows
decimal? total = Query.For<SalesOrder>().SumScalar(service, o => o.TotalAmount);  // null when none match
decimal? avg   = Query.For<SalesOrder>().AverageScalar(service, o => o.TotalAmount);

DateTime? earliest = Query.For<Task>().MinScalar(service, t => t.ScheduledStart); // DateTime? column -> DateTime?
int?      largest  = Query.For<Account>().MaxScalar(service, a => a.NumberOfEmployees);
decimal?  topDeal  = Query.For<SalesOrder>().MaxScalar(service, o => o.TotalAmount); // Money column -> decimal?
```

`SumScalar`/`AverageScalar` return `decimal?` (`null` distinguishes "no rows" from a real `0`); `CountScalar`
returns `int`. `MinScalar`/`MaxScalar` return the **column's own type**, inferred from the selector, so there
is no cast at the call site: a `DateTime?` column hands back `DateTime?`, an `int?` column `int?`, and a money
column `decimal?` (the amount). All read back `null` when nothing matches. For **grouped** aggregates
(`GroupBy(...)` + multiple aggregates) author with the alias API (`.Sum(col, "alias")`) and read the rows -
see the aggregate builders above.

## See also

- [Write a plugin](plugins.md) - where these queries run.
- [Testing with Vela.Testing](testing.md) - the in-memory service executes the same queries.
