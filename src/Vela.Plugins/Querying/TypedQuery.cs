using System;
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

    /// <summary>Selects the given typed columns.</summary>
    public TypedQuery<TEntity> Select(params Expression<Func<TEntity, object?>>[] columns)
    {
        Expression.ColumnSet = TypedQueryNames.BuildColumnSet(columns);
        return this;
    }

    /// <summary>Selects all columns.</summary>
    public TypedQuery<TEntity> SelectAll()
    {
        Expression.ColumnSet = new ColumnSet(true);
        return this;
    }

    /// <summary>Adds a condition to the root criteria.</summary>
    public TypedQuery<TEntity> Where(Expression<Func<TEntity, object?>> column, ConditionOperator conditionOperator, params object[] values)
    {
        Expression.Criteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), conditionOperator, values ?? Array.Empty<object>()));
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
        Expression.Criteria.AddCondition(alias, TypedQueryNames.GetAttributeLogicalName(column), conditionOperator, values ?? Array.Empty<object>());
        return this;
    }

    /// <summary>Filters on the existence of related records (Any/All/NotAny/NotAll) without returning them.</summary>
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
        Expression.Criteria.AnyAllFilterLinkEntity = link;
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

    /// <summary>Adds a condition to the link criteria (filters the linked rows).</summary>
    public TypedLink<TTo> Where(Expression<Func<TTo, object?>> column, ConditionOperator conditionOperator, params object[] values)
    {
        Definition.LinkCriteria.AddCondition(new ConditionExpression(TypedQueryNames.GetAttributeLogicalName(column), conditionOperator, values ?? Array.Empty<object>()));
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
