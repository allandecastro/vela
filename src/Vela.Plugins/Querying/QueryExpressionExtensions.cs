using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Querying;

/// <summary>
/// Typed helpers for <see cref="QueryExpression"/> and <see cref="FilterExpression"/>.
/// </summary>
public static class QueryExpressionExtensions
{
    /// <summary>
    /// Replaces selected columns using typed expressions.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="columns">The columns to select.</param>
    /// <returns>The same query instance.</returns>
    public static QueryExpression Select<TEntity>(this QueryExpression query, params Expression<Func<TEntity, object?>>[] columns)
        where TEntity : Entity
    {
        query = query ?? throw new ArgumentNullException(nameof(query));
        query.ColumnSet = TypedQueryNames.BuildColumnSet(columns);
        return query;
    }

    /// <summary>
    /// Adds a typed condition to the root criteria.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="column">The target column.</param>
    /// <param name="conditionOperator">The operator.</param>
    /// <param name="values">The condition values.</param>
    /// <returns>The same query instance.</returns>
    public static QueryExpression Where<TEntity>(
        this QueryExpression query,
        Expression<Func<TEntity, object?>> column,
        ConditionOperator conditionOperator,
        params object[] values)
        where TEntity : Entity
    {
        query = query ?? throw new ArgumentNullException(nameof(query));
        string logicalName = TypedQueryNames.GetAttributeLogicalName(column);
        query.Criteria.AddCondition(new ConditionExpression(logicalName, conditionOperator, values ?? Array.Empty<object>()));
        return query;
    }

    /// <summary>
    /// Adds an AND sub-filter with typed conditions.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="configure">Typed filter configuration.</param>
    /// <returns>The same query instance.</returns>
    public static QueryExpression WhereAll<TEntity>(this QueryExpression query, Action<TypedFilterBuilder<TEntity>> configure)
        where TEntity : Entity
    {
        query = query ?? throw new ArgumentNullException(nameof(query));
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression filter = new FilterExpression(LogicalOperator.And);
        configure(new TypedFilterBuilder<TEntity>(filter));
        query.Criteria.AddFilter(filter);
        return query;
    }

    /// <summary>
    /// Adds an OR sub-filter with typed conditions.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="configure">Typed filter configuration.</param>
    /// <returns>The same query instance.</returns>
    public static QueryExpression WhereAny<TEntity>(this QueryExpression query, Action<TypedFilterBuilder<TEntity>> configure)
        where TEntity : Entity
    {
        query = query ?? throw new ArgumentNullException(nameof(query));
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression filter = new FilterExpression(LogicalOperator.Or);
        configure(new TypedFilterBuilder<TEntity>(filter));
        query.Criteria.AddFilter(filter);
        return query;
    }

    /// <summary>
    /// Adds a typed order expression.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="column">The ordered column.</param>
    /// <param name="orderType">Sort direction.</param>
    /// <returns>The same query instance.</returns>
    public static QueryExpression OrderBy<TEntity>(this QueryExpression query, Expression<Func<TEntity, object?>> column, OrderType orderType = OrderType.Ascending)
        where TEntity : Entity
    {
        query = query ?? throw new ArgumentNullException(nameof(query));
        string logicalName = TypedQueryNames.GetAttributeLogicalName(column);
        query.AddOrder(logicalName, orderType);
        return query;
    }

    /// <summary>
    /// Adds a typed link from source to target entities.
    /// </summary>
    /// <typeparam name="TFrom">Source entity type.</typeparam>
    /// <typeparam name="TTo">Target linked entity type.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="fromAttribute">Source join attribute.</param>
    /// <param name="toAttribute">Target join attribute.</param>
    /// <param name="joinOperator">Join operator.</param>
    /// <param name="alias">Optional link alias.</param>
    /// <param name="columns">Optional columns from linked entity.</param>
    /// <returns>The configured link.</returns>
    public static LinkEntity Link<TFrom, TTo>(
        this QueryExpression query,
        Expression<Func<TFrom, object?>> fromAttribute,
        Expression<Func<TTo, object?>> toAttribute,
        JoinOperator joinOperator = JoinOperator.Inner,
        string? alias = null,
        params Expression<Func<TTo, object?>>[] columns)
        where TFrom : Entity
        where TTo : Entity
    {
        query = query ?? throw new ArgumentNullException(nameof(query));
        string fromName = TypedQueryNames.GetAttributeLogicalName(fromAttribute);
        string toName = TypedQueryNames.GetAttributeLogicalName(toAttribute);
        string toEntity = TypedQueryNames.GetEntityLogicalName<TTo>();

        LinkEntity link = query.AddLink(toEntity, fromName, toName, joinOperator);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            link.EntityAlias = alias;
        }

        if (columns != null && columns.Length > 0)
        {
            link.Columns = TypedQueryNames.BuildColumnSet(columns);
        }

        return link;
    }
}

/// <summary>
/// Typed builder for nested conditions in a filter.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class TypedFilterBuilder<TEntity>(FilterExpression filter)
    where TEntity : Entity
{
    private readonly FilterExpression _filter = filter ?? throw new ArgumentNullException(nameof(filter));

    /// <summary>
    /// Adds a typed condition.
    /// </summary>
    /// <param name="column">The target column.</param>
    /// <param name="conditionOperator">The operator.</param>
    /// <param name="values">The condition values.</param>
    /// <returns>The builder.</returns>
    public TypedFilterBuilder<TEntity> Condition(
        Expression<Func<TEntity, object?>> column,
        ConditionOperator conditionOperator,
        params object[] values)
    {
        string logicalName = TypedQueryNames.GetAttributeLogicalName(column);
        _filter.AddCondition(new ConditionExpression(logicalName, conditionOperator, values ?? Array.Empty<object>()));
        return this;
    }

    /// <summary>
    /// Adds a nested AND filter.
    /// </summary>
    /// <param name="configure">Nested configuration.</param>
    /// <returns>The builder.</returns>
    public TypedFilterBuilder<TEntity> All(Action<TypedFilterBuilder<TEntity>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression nested = new FilterExpression(LogicalOperator.And);
        configure(new TypedFilterBuilder<TEntity>(nested));
        _filter.AddFilter(nested);
        return this;
    }

    /// <summary>
    /// Adds a nested OR filter.
    /// </summary>
    /// <param name="configure">Nested configuration.</param>
    /// <returns>The builder.</returns>
    public TypedFilterBuilder<TEntity> Any(Action<TypedFilterBuilder<TEntity>> configure)
    {
        configure = configure ?? throw new ArgumentNullException(nameof(configure));
        FilterExpression nested = new FilterExpression(LogicalOperator.Or);
        configure(new TypedFilterBuilder<TEntity>(nested));
        _filter.AddFilter(nested);
        return this;
    }
}

internal static class TypedQueryNames
{
    public static ColumnSet BuildColumnSet<TEntity>(Expression<Func<TEntity, object?>>[] columns)
        where TEntity : Entity
    {
        if (columns == null || columns.Length == 0)
        {
            return new ColumnSet(false);
        }

        string[] names = new string[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            names[index] = GetAttributeLogicalName(columns[index]);
        }

        return new ColumnSet(names);
    }

    public static string GetEntityLogicalName<TEntity>() where TEntity : Entity
        => Vela.StepBuilder.ExpressionHelper.GetEntityLogicalName<TEntity>();

    public static string GetAttributeLogicalName<TEntity>(Expression<Func<TEntity, object?>> expression)
        where TEntity : Entity
        => Vela.StepBuilder.ExpressionHelper.GetAttributeLogicalName(expression);
}
