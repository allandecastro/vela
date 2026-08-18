# Typed queries

Use typed extensions on `QueryExpression` to avoid string literals:

```csharp
using Vela.Querying;
using Zava.Shared.Models;
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

Also available on the builder: `Distinct()`, `Page(n, count)`, `WithTotalRecordCount()`, `WhereColumns(a, op, b)`
(compare two columns), `WhereLink<TLinked>(alias, …)` (conditions on a join, e.g. records-not-in-a-set),
`WhereRelated<TTo>(…, JoinOperator.Any/All/NotAny/NotAll, …)`, and aggregates
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
runtime `FaultException`.

## See also

- [Write a plugin](plugins.md) - where these queries run.
- [Testing with Vela.Testing](testing.md) - the in-memory service executes the same queries.
