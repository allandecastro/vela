using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Querying;

/// <summary>
/// A strongly-typed builder around <see cref="QueryExpression"/>. The entity type is carried by the builder,
/// so the chained methods (<c>Where</c>, <c>OrderBy</c>, <c>Select</c>, <c>Link</c>…) don't repeat the type
/// argument. Converts implicitly to <see cref="QueryExpression"/>, so it can be passed to
/// <c>RetrieveMultiple</c> directly. Start one with <see cref="Query.For{TEntity}"/>.
/// </summary>
/// <typeparam name="TEntity">The root early-bound entity type.</typeparam>
public sealed class TypedQuery<TEntity>
    where TEntity : Entity
{
    internal TypedQuery()
    {
        Expression = new QueryExpression(TypedQueryNames.GetEntityLogicalName<TEntity>());
    }

    /// <summary>The underlying <see cref="QueryExpression"/>.</summary>
    public QueryExpression Expression { get; }

    /// <summary>
    /// Selects the given typed columns, replacing any earlier selection. Group-by and aggregate columns added by
    /// <see cref="GroupBy"/>, <see cref="Count"/>, <see cref="Sum"/> and the like are kept, whatever the call order.
    /// </summary>
    public TypedQuery<TEntity> Select(params Expression<Func<TEntity, object?>>[] columns)
    {
        Expression.ColumnSet = KeepAggregates(TypedQueryNames.BuildColumnSet(columns));
        return this;
    }

    /// <summary>Selects all columns. Group-by and aggregate columns added earlier are kept.</summary>
    public TypedQuery<TEntity> SelectAll()
    {
        Expression.ColumnSet = KeepAggregates(new ColumnSet(true));
        return this;
    }

    private ColumnSet KeepAggregates(ColumnSet columns)
    {
        columns.AttributeExpressions.AddRange(Expression.ColumnSet.AttributeExpressions);
        return columns;
    }

    /// <summary>Adds a condition to the root criteria.</summary>
    public TypedQuery<TEntity> Where(Expression<Func<TEntity, object?>> column, ConditionOperator conditionOperator, params object[] values)
    {
        Expression.Criteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), conditionOperator, TypedQueryValues.Normalize(values)));
        return this;
    }

    /// <summary>
    /// Adds an <c>In</c> condition whose values come from a sequence (for example a <c>Guid[]</c> or
    /// <c>List&lt;Guid&gt;</c>), so each element is a distinct value. Avoids the <c>params object[]</c> trap where
    /// a typed array binds as a single nested value.
    /// </summary>
    public TypedQuery<TEntity> WhereIn<TValue>(Expression<Func<TEntity, object?>> column, IEnumerable<TValue> values)
    {
        Expression.Criteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), ConditionOperator.In, TypedQueryValues.Materialize(values)));
        return this;
    }

    /// <summary>Adds a <c>NotIn</c> condition whose values come from a sequence (see <see cref="WhereIn{TValue}"/>).</summary>
    public TypedQuery<TEntity> WhereNotIn<TValue>(Expression<Func<TEntity, object?>> column, IEnumerable<TValue> values)
    {
        Expression.Criteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), ConditionOperator.NotIn, TypedQueryValues.Materialize(values)));
        return this;
    }

    /// <summary>Adds an AND sub-filter.</summary>
    public TypedQuery<TEntity> WhereAll(Action<TypedFilterBuilder<TEntity>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression filter = new FilterExpression(LogicalOperator.And);
        configure(new TypedFilterBuilder<TEntity>(filter));
        Expression.Criteria.AddFilter(filter);
        return this;
    }

    /// <summary>Adds an OR sub-filter.</summary>
    public TypedQuery<TEntity> WhereAny(Action<TypedFilterBuilder<TEntity>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression filter = new FilterExpression(LogicalOperator.Or);
        configure(new TypedFilterBuilder<TEntity>(filter));
        Expression.Criteria.AddFilter(filter);
        return this;
    }

    /// <summary>Orders ascending by a typed column.</summary>
    public TypedQuery<TEntity> OrderBy(Expression<Func<TEntity, object?>> column)
    {
        Expression.AddOrder(TypedQueryNames.GetAttributeLogicalName(column), OrderType.Ascending);
        return this;
    }

    /// <summary>Orders descending by a typed column.</summary>
    public TypedQuery<TEntity> OrderByDescending(Expression<Func<TEntity, object?>> column)
    {
        Expression.AddOrder(TypedQueryNames.GetAttributeLogicalName(column), OrderType.Descending);
        return this;
    }

    /// <summary>Limits the result count.</summary>
    public TypedQuery<TEntity> Top(int count)
    {
        Expression.TopCount = count;
        return this;
    }

    /// <summary>
    /// Joins a linked entity. The target type <typeparamref name="TTo"/> is given once; configure the link
    /// (columns, criteria, nested links) through the typed <paramref name="configure"/> action.
    /// </summary>
    /// <typeparam name="TTo">The linked entity type.</typeparam>
    public TypedQuery<TEntity> Link<TTo>(
        Expression<Func<TEntity, object?>> fromAttribute,
        Expression<Func<TTo, object?>> toAttribute,
        JoinOperator joinOperator = JoinOperator.Inner,
        string? alias = null,
        Action<TypedLink<TTo>>? configure = null)
        where TTo : Entity
    {
        LinkEntity link = Expression.AddLink(
            TypedQueryNames.GetEntityLogicalName<TTo>(),
            TypedQueryNames.GetAttributeLogicalName(fromAttribute),
            TypedQueryNames.GetAttributeLogicalName(toAttribute),
            joinOperator);

        if (!string.IsNullOrWhiteSpace(alias))
        {
            link.EntityAlias = alias;
        }

        configure?.Invoke(new TypedLink<TTo>(link));
        return this;
    }

    /// <summary>Returns distinct rows.</summary>
    public TypedQuery<TEntity> Distinct()
    {
        Expression.Distinct = true;
        return this;
    }

    /// <summary>Requests a page of results (1-based page number).</summary>
    public TypedQuery<TEntity> Page(int pageNumber, int count)
    {
        Expression.PageInfo = new PagingInfo { PageNumber = pageNumber, Count = count };
        return this;
    }

    /// <summary>Asks the result to include the total record count (up to 5,000).</summary>
    public TypedQuery<TEntity> WithTotalRecordCount()
    {
        Expression.PageInfo ??= new PagingInfo();
        Expression.PageInfo.ReturnTotalRecordCount = true;
        return this;
    }

    /// <summary>Adds a condition that compares two columns on the same row.</summary>
    public TypedQuery<TEntity> WhereColumns(Expression<Func<TEntity, object?>> left, ConditionOperator conditionOperator, Expression<Func<TEntity, object?>> right)
    {
        Expression.Criteria.AddCondition(new ConditionExpression(
            TypedQueryNames.GetAttributeLogicalName(left),
            conditionOperator,
            compareColumns: true,
            value: TypedQueryNames.GetAttributeLogicalName(right)));
        return this;
    }

    /// <summary>Adds a condition that targets a joined entity by its alias (e.g. the "records not in a set" pattern).</summary>
    public TypedQuery<TEntity> WhereLink<TLinked>(string alias, Expression<Func<TLinked, object?>> column, ConditionOperator conditionOperator, params object[] values)
        where TLinked : Entity
    {
        Expression.Criteria.AddCondition(alias, TypedQueryNames.GetAttributeLogicalName(column), conditionOperator, TypedQueryValues.Normalize(values));
        return this;
    }

    /// <summary>
    /// Filters on the existence of related records (Any/All/NotAny/NotAll) without returning them. Several calls
    /// combine with AND.
    /// </summary>
    public TypedQuery<TEntity> WhereRelated<TTo>(
        Expression<Func<TEntity, object?>> fromAttribute,
        Expression<Func<TTo, object?>> toAttribute,
        JoinOperator anyAllOperator,
        Action<TypedLink<TTo>>? configure = null)
        where TTo : Entity
    {
        LinkEntity link = new LinkEntity(
            Expression.EntityName,
            TypedQueryNames.GetEntityLogicalName<TTo>(),
            TypedQueryNames.GetAttributeLogicalName(fromAttribute),
            TypedQueryNames.GetAttributeLogicalName(toAttribute),
            anyAllOperator);
        configure?.Invoke(new TypedLink<TTo>(link));

        // A FilterExpression holds a single AnyAllFilterLinkEntity, so each related-record filter gets its own child
        // filter: a second WhereRelated adds to the first instead of replacing it.
        Expression.Criteria.AddFilter(new FilterExpression(LogicalOperator.And) { AnyAllFilterLinkEntity = link });
        return this;
    }

    /// <summary>Groups by a typed column (aggregate query).</summary>
    public TypedQuery<TEntity> GroupBy(Expression<Func<TEntity, object?>> column, string alias)
    {
        Expression.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression
        {
            AttributeName = TypedQueryNames.GetAttributeLogicalName(column),
            Alias = alias,
            HasGroupBy = true
        });
        return this;
    }

    /// <summary>Adds a Count aggregate (counts rows in the group).</summary>
    public TypedQuery<TEntity> Count(Expression<Func<TEntity, object?>> column, string alias) => AddAggregate(column, alias, XrmAggregateType.Count);

    /// <summary>Adds a Sum aggregate.</summary>
    public TypedQuery<TEntity> Sum(Expression<Func<TEntity, object?>> column, string alias) => AddAggregate(column, alias, XrmAggregateType.Sum);

    /// <summary>Adds an Average aggregate.</summary>
    public TypedQuery<TEntity> Average(Expression<Func<TEntity, object?>> column, string alias) => AddAggregate(column, alias, XrmAggregateType.Avg);

    /// <summary>Adds a Min aggregate.</summary>
    public TypedQuery<TEntity> Min(Expression<Func<TEntity, object?>> column, string alias) => AddAggregate(column, alias, XrmAggregateType.Min);

    /// <summary>Adds a Max aggregate.</summary>
    public TypedQuery<TEntity> Max(Expression<Func<TEntity, object?>> column, string alias) => AddAggregate(column, alias, XrmAggregateType.Max);

    private TypedQuery<TEntity> AddAggregate(Expression<Func<TEntity, object?>> column, string alias, XrmAggregateType type)
    {
        Expression.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression
        {
            AttributeName = TypedQueryNames.GetAttributeLogicalName(column),
            Alias = alias,
            AggregateType = type
        });
        return this;
    }

    /// <summary>
    /// Emit <c>latematerialize='true'</c> in <see cref="ToFetchXml"/> (a FetchXML-only performance hint). Has no
    /// effect when the query runs as a <see cref="QueryExpression"/>.
    /// </summary>
    public TypedQuery<TEntity> LateMaterialize()
    {
        FetchOptions.LateMaterialize = true;
        return this;
    }

    /// <summary>
    /// Emit a custom <c>aggregatelimit</c> in <see cref="ToFetchXml"/> (FetchXML-only). Has no effect when the query
    /// runs as a <see cref="QueryExpression"/>.
    /// </summary>
    public TypedQuery<TEntity> AggregateLimit(int limit)
    {
        FetchOptions.AggregateLimit = limit;
        return this;
    }

    /// <summary>Adds a <b>distinct</b> <c>countcolumn</c> aggregate — the count of unique values (FetchXML-only).</summary>
    public TypedQuery<TEntity> CountColumnDistinct(Expression<Func<TEntity, object?>> column, string alias)
    {
        AddAggregate(column, alias, XrmAggregateType.CountColumn);
        FetchOptions.DistinctAggregateAliases.Add(alias);
        return this;
    }

    /// <summary>Adds a hierarchical <b>row aggregate</b> on a lookup column (FetchXML-only; e.g. count of children).</summary>
    public TypedQuery<TEntity> RowAggregate(Expression<Func<TEntity, object?>> column, string alias, string function)
    {
        FetchOptions.RowAggregates.Add(new RowAggregate
        {
            Attribute = TypedQueryNames.GetAttributeLogicalName(column),
            Alias = alias,
            Function = function,
        });
        return this;
    }

    /// <summary>Orders an aggregate/group-by query by a result <paramref name="alias"/> (<c>&lt;order alias='...'/&gt;</c>).</summary>
    public TypedQuery<TEntity> OrderByAggregate(string alias, bool descending = false)
    {
        Expression.Orders.Add(new OrderExpression { Alias = alias, OrderType = descending ? OrderType.Descending : OrderType.Ascending });
        return this;
    }

    /// <summary>Group date columns by UTC instead of the user's time zone (emits <c>usertimezone='false'</c>; FetchXML-only).</summary>
    public TypedQuery<TEntity> UtcDateGrouping()
    {
        FetchOptions.UtcDateGrouping = true;
        return this;
    }

    /// <summary>Sort choice columns by integer value, not label (emits <c>useraworderby='true'</c>; FetchXML-only).</summary>
    public TypedQuery<TEntity> RawChoiceOrder()
    {
        FetchOptions.RawChoiceOrder = true;
        return this;
    }

    /// <summary>Query long-term-retained rows only (emits <c>datasource='retained'</c>; FetchXML-only).</summary>
    public TypedQuery<TEntity> FromRetainedData()
    {
        FetchOptions.RetainedDataSource = true;
        return this;
    }

    /// <summary>Apply SQL Server query hints (emits <c>options='...'</c>; FetchXML-only, use only when MS support advises).</summary>
    public TypedQuery<TEntity> SqlOptions(string options)
    {
        FetchOptions.SqlOptions = options;
        return this;
    }

    /// <summary>
    /// Serializes this query to a FetchXML string — the same query authored with compile-time type safety, emitted
    /// for the messages/tools that require FetchXML. FetchXML-only settings (<see cref="LateMaterialize"/>,
    /// <see cref="AggregateLimit"/>) are included; everything else comes from the underlying query.
    /// </summary>
    public string ToFetchXml() => FetchXmlWriter.Write(Expression, _fetchOptions);

    private FetchXmlOptions FetchOptions => _fetchOptions ??= new FetchXmlOptions();

    private FetchXmlOptions? _fetchOptions;

    /// <summary>Returns the underlying <see cref="QueryExpression"/>.</summary>
    public QueryExpression Build()
    {
        return Expression;
    }

    public static implicit operator QueryExpression(TypedQuery<TEntity> query)
    {
        return query.Expression;
    }
}

/// <summary>
/// Strongly-typed configuration for a <see cref="LinkEntity"/> created by <see cref="TypedQuery{TEntity}.Link{TTo}"/>.
/// </summary>
/// <typeparam name="TTo">The linked entity type.</typeparam>
public sealed class TypedLink<TTo>
    where TTo : Entity
{
    internal TypedLink(LinkEntity link)
    {
        Definition = link;
    }

    /// <summary>The underlying <see cref="LinkEntity"/>.</summary>
    public LinkEntity Definition { get; }

    /// <summary>Projects typed columns from the linked entity (accessed as aliased values).</summary>
    public TypedLink<TTo> Select(params Expression<Func<TTo, object?>>[] columns)
    {
        Definition.Columns = TypedQueryNames.BuildColumnSet(columns);
        return this;
    }

    /// <summary>Projects all columns from the linked entity.</summary>
    public TypedLink<TTo> SelectAll()
    {
        Definition.Columns = new ColumnSet(true);
        return this;
    }

    /// <summary>Adds an <c>In</c> condition on the linked entity whose values come from a sequence (see <see cref="TypedQuery{TEntity}.WhereIn{TValue}"/>).</summary>
    public TypedLink<TTo> WhereIn<TValue>(Expression<Func<TTo, object?>> column, IEnumerable<TValue> values)
    {
        Definition.LinkCriteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), ConditionOperator.In, TypedQueryValues.Materialize(values)));
        return this;
    }

    /// <summary>Adds a <c>NotIn</c> condition on the linked entity whose values come from a sequence.</summary>
    public TypedLink<TTo> WhereNotIn<TValue>(Expression<Func<TTo, object?>> column, IEnumerable<TValue> values)
    {
        Definition.LinkCriteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), ConditionOperator.NotIn, TypedQueryValues.Materialize(values)));
        return this;
    }

    /// <summary>Adds a condition to the link criteria (filters the linked rows).</summary>
    public TypedLink<TTo> Where(Expression<Func<TTo, object?>> column, ConditionOperator conditionOperator, params object[] values)
    {
        Definition.LinkCriteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), conditionOperator, TypedQueryValues.Normalize(values)));
        return this;
    }

    /// <summary>Adds an AND sub-filter to the link criteria.</summary>
    public TypedLink<TTo> WhereAll(Action<TypedFilterBuilder<TTo>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression filter = new FilterExpression(LogicalOperator.And);
        configure(new TypedFilterBuilder<TTo>(filter));
        Definition.LinkCriteria.AddFilter(filter);
        return this;
    }

    /// <summary>Adds an OR sub-filter to the link criteria.</summary>
    public TypedLink<TTo> WhereAny(Action<TypedFilterBuilder<TTo>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression filter = new FilterExpression(LogicalOperator.Or);
        configure(new TypedFilterBuilder<TTo>(filter));
        Definition.LinkCriteria.AddFilter(filter);
        return this;
    }

    /// <summary>Adds a nested link from this linked entity to a deeper one.</summary>
    /// <typeparam name="TDeep">The deeper linked entity type.</typeparam>
    public TypedLink<TTo> Link<TDeep>(
        Expression<Func<TTo, object?>> fromAttribute,
        Expression<Func<TDeep, object?>> toAttribute,
        JoinOperator joinOperator = JoinOperator.Inner,
        string? alias = null,
        Action<TypedLink<TDeep>>? configure = null)
        where TDeep : Entity
    {
        LinkEntity nested = Definition.AddLink(
            TypedQueryNames.GetEntityLogicalName<TDeep>(),
            TypedQueryNames.GetAttributeLogicalName(fromAttribute),
            TypedQueryNames.GetAttributeLogicalName(toAttribute),
            joinOperator);

        if (!string.IsNullOrWhiteSpace(alias))
        {
            nested.EntityAlias = alias;
        }

        configure?.Invoke(new TypedLink<TDeep>(nested));
        return this;
    }
}

internal static class TypedQueryValues
{
    // Expands a typed sequence into the object[] a ConditionExpression expects, so In/NotIn get one value per
    // element instead of a single nested array (the params object[] trap for a value-typed array like Guid[]).
    public static object[] Materialize<TValue>(IEnumerable<TValue> values)
        => (values ?? Enumerable.Empty<TValue>()).Select(value => NormalizeValue(value)!).ToArray();

    // Condition values as Dataverse compares them: an early-bound choice enum becomes its integer value (a
    // QueryExpression cannot carry an enum, and FetchXML would otherwise get the member name).
    public static object[] Normalize(object[]? values)
    {
        if (values == null || values.Length == 0)
        {
            return Array.Empty<object>();
        }

        object[] normalized = new object[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            normalized[i] = NormalizeValue(values[i])!;
        }

        return normalized;
    }

    public static object? NormalizeValue(object? value) =>
        value is Enum choice ? Convert.ToInt32(choice, System.Globalization.CultureInfo.InvariantCulture) : value;
}
