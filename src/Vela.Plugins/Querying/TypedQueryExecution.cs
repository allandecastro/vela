using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Steps;

namespace Vela.Querying
{
    /// <summary>
    /// Execution helpers that run a <see cref="TypedQuery{TEntity}"/> against an <see cref="IOrganizationService"/>
    /// and return typed results. <see cref="RetrieveAll{TEntity}(IOrganizationService, TypedQuery{TEntity}, int)"/>
    /// pages through every matching record so callers never hand-roll the
    /// <see cref="EntityCollection.MoreRecords"/> / <see cref="EntityCollection.PagingCookie"/> loop.
    /// </summary>
    public static class TypedQueryExecution
    {
        /// <summary>The default page size used when paging (the Dataverse per-page maximum).</summary>
        public const int DefaultPageSize = 5000;

        private const int MaxPageSize = 5000;

        /// <summary>
        /// Runs the query to completion, paging through every matching record, and returns them as a typed list. When
        /// the query sets a <see cref="TypedQuery{TEntity}.Top"/>, that cap is honored with a single retrieve instead,
        /// since Top and paging are mutually exclusive in Dataverse.
        /// </summary>
        /// <typeparam name="TEntity">The root early-bound entity type.</typeparam>
        /// <param name="service">The organization service to execute against.</param>
        /// <param name="query">The typed query to run.</param>
        /// <param name="pageSize">Records per page while paging (1..5000, default 5000).</param>
        /// <returns>Every matching record, materialized as <typeparamref name="TEntity"/>.</returns>
        public static List<TEntity> RetrieveAll<TEntity>(this IOrganizationService service, TypedQuery<TEntity> query, int pageSize = DefaultPageSize)
            where TEntity : Entity
        {
            service = service ?? throw new ArgumentNullException(nameof(service));
            query = query ?? throw new ArgumentNullException(nameof(query));
            if (pageSize < 1 || pageSize > MaxPageSize)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
            }

            QueryExpression expression = query.Expression;
            List<TEntity> results = new List<TEntity>();

            // Top and paging are mutually exclusive; when a Top is set, a single retrieve already caps the result set.
            if (expression.TopCount.HasValue)
            {
                foreach (Entity entity in service.RetrieveMultiple(expression).Entities)
                {
                    results.Add(entity.ToEntity<TEntity>());
                }

                return results;
            }

            // Page with a private PagingInfo and restore the caller's afterwards, so running the query never leaves a
            // reusable TypedQuery on its last page (a later ToList / FirstOrDefault must see the original paging).
            PagingInfo? originalPaging = expression.PageInfo;
            PagingInfo paging = new PagingInfo
            {
                PageNumber = 1,
                Count = pageSize,
                PagingCookie = null,
                ReturnTotalRecordCount = originalPaging?.ReturnTotalRecordCount ?? false,
            };
            expression.PageInfo = paging;
            try
            {
                while (true)
                {
                    EntityCollection page = service.RetrieveMultiple(expression);
                    foreach (Entity entity in page.Entities)
                    {
                        results.Add(entity.ToEntity<TEntity>());
                    }

                    if (!page.MoreRecords)
                    {
                        break;
                    }

                    paging.PageNumber++;
                    paging.PagingCookie = page.PagingCookie;
                }
            }
            finally
            {
                expression.PageInfo = originalPaging;
            }

            return results;
        }

        /// <summary>
        /// Fluent terminal for <see cref="RetrieveAll{TEntity}(IOrganizationService, TypedQuery{TEntity}, int)"/>:
        /// runs the query to completion through <paramref name="service"/> and returns every matching record.
        /// </summary>
        public static List<TEntity> ToList<TEntity>(this TypedQuery<TEntity> query, IOrganizationService service, int pageSize = DefaultPageSize)
            where TEntity : Entity
        {
            service = service ?? throw new ArgumentNullException(nameof(service));
            return service.RetrieveAll(query, pageSize);
        }

        /// <summary>
        /// Runs the query and returns the first matching record, or <c>null</c> when there are none. Caps the
        /// retrieve at one row (sets <see cref="QueryExpression.TopCount"/> to 1 when no Top was already set), so it
        /// is a single retrieve and never pages. The fluent terminal that lets <c>.Build()</c> stay out of
        /// caller code for the single-record case.
        /// </summary>
        /// <typeparam name="TEntity">The root early-bound entity type.</typeparam>
        /// <param name="query">The typed query to run.</param>
        /// <param name="service">The organization service to execute against.</param>
        /// <returns>The first matching record as <typeparamref name="TEntity"/>, or <c>null</c> if none match.</returns>
        public static TEntity? FirstOrDefault<TEntity>(this TypedQuery<TEntity> query, IOrganizationService service)
            where TEntity : Entity
        {
            query = query ?? throw new ArgumentNullException(nameof(query));
            service = service ?? throw new ArgumentNullException(nameof(service));

            QueryExpression expression = query.Expression;

            // Cap at one row only when the caller set neither a Top nor a Page (Top and paging are mutually
            // exclusive), and restore the original afterwards so FirstOrDefault never permanently shrinks a
            // reusable query (q.FirstOrDefault(svc) then q.ToList(svc) must still return every record).
            bool capAtOne = !expression.TopCount.HasValue
                && (expression.PageInfo is null || expression.PageInfo.Count == 0);
            int? originalTop = expression.TopCount;
            if (capAtOne)
            {
                expression.TopCount = 1;
            }

            try
            {
                DataCollection<Entity> matches = service.RetrieveMultiple(expression).Entities;
                return matches.Count > 0 ? matches[0].ToEntity<TEntity>() : null;
            }
            finally
            {
                if (capAtOne)
                {
                    expression.TopCount = originalTop;
                }
            }
        }

        // ---- Scalar aggregates --------------------------------------------------------------------------
        // One-call typed scalar aggregates: no alias string to repeat (an aliased-value desync would read a
        // silent default), no explicit result generic. The column selector only identifies the column; the
        // result type is intrinsic: count -> int, sum/average -> decimal, and min/max -> the column's own type,
        // taken from a strongly-typed selector so there is no cast at the call site. Grouped aggregates stay on
        // the string-alias authoring API (TypedQuery.GroupBy/Sum(..., alias) + ToFetchXml).

        /// <summary>Counts the matching rows. <see cref="XrmAggregateType.Count"/> counts every matching row, so the
        /// column only names the attribute the count is keyed on (its value is irrelevant) - pass the primary id.</summary>
        public static int CountScalar<TEntity>(this TypedQuery<TEntity> query, IOrganizationService service, Expression<Func<TEntity, object?>> column)
            where TEntity : Entity
            => Convert.ToInt32(RunScalarAggregate(query, service, column, XrmAggregateType.Count) ?? 0);

        /// <summary>Sums <paramref name="column"/> over the matching rows, or <c>null</c> when none match.</summary>
        public static decimal? SumScalar<TEntity>(this TypedQuery<TEntity> query, IOrganizationService service, Expression<Func<TEntity, object?>> column)
            where TEntity : Entity
            => ToDecimal(RunScalarAggregate(query, service, column, XrmAggregateType.Sum));

        /// <summary>Averages <paramref name="column"/> over the matching rows, or <c>null</c> when none match.</summary>
        public static decimal? AverageScalar<TEntity>(this TypedQuery<TEntity> query, IOrganizationService service, Expression<Func<TEntity, object?>> column)
            where TEntity : Entity
            => ToDecimal(RunScalarAggregate(query, service, column, XrmAggregateType.Avg));

        // Min/Max return the column's own type, so the selector is strongly typed (x => x.scheduledstart typed
        // DateTime?, x => x.quantity typed int?) and TResult is inferred from it - the caller writes no type
        // argument and gets back the same type, with no AliasedValue and no cast. Money columns surface as the
        // reference type Microsoft.Xrm.Sdk.Money, which the struct constraint excludes, so they get a dedicated
        // overload that returns the decimal amount (matching SumScalar/AverageScalar over money).

        /// <summary>The smallest <paramref name="column"/> value over the matching rows, or <c>null</c> when none
        /// match. The result type matches the selected column (for example <see cref="DateTime"/> or
        /// <see cref="decimal"/>).</summary>
        public static TResult? MinScalar<TEntity, TResult>(this TypedQuery<TEntity> query, IOrganizationService service, Expression<Func<TEntity, TResult?>> column)
            where TEntity : Entity
            where TResult : struct
            => ConvertScalar<TResult>(RunScalarAggregate(query, service, column, XrmAggregateType.Min));

        /// <summary>The largest <paramref name="column"/> value over the matching rows, or <c>null</c> when none
        /// match. The result type matches the selected column (for example <see cref="DateTime"/> or
        /// <see cref="decimal"/>).</summary>
        public static TResult? MaxScalar<TEntity, TResult>(this TypedQuery<TEntity> query, IOrganizationService service, Expression<Func<TEntity, TResult?>> column)
            where TEntity : Entity
            where TResult : struct
            => ConvertScalar<TResult>(RunScalarAggregate(query, service, column, XrmAggregateType.Max));

        /// <summary>The smallest money <paramref name="column"/> amount over the matching rows, or <c>null</c>
        /// when none match.</summary>
        public static decimal? MinScalar<TEntity>(this TypedQuery<TEntity> query, IOrganizationService service, Expression<Func<TEntity, Money?>> column)
            where TEntity : Entity
            => ToDecimal(RunScalarAggregate(query, service, column, XrmAggregateType.Min));

        /// <summary>The largest money <paramref name="column"/> amount over the matching rows, or <c>null</c>
        /// when none match.</summary>
        public static decimal? MaxScalar<TEntity>(this TypedQuery<TEntity> query, IOrganizationService service, Expression<Func<TEntity, Money?>> column)
            where TEntity : Entity
            => ToDecimal(RunScalarAggregate(query, service, column, XrmAggregateType.Max));

        // Runs a single-aggregate retrieve against a stripped clone of the caller's query, never touching the
        // original. The clone keeps only what an aggregate needs - entity, filter criteria and filtering links -
        // and drops the projection shape that conflicts with aggregation: root/link column projections, orders
        // (aggregate ordering must use a group/aggregate alias), paging and Top (which would page past the single
        // aggregate row and read a silent 0/null). Criteria/LinkCriteria are shared by reference (read-only here).
        private static object? RunScalarAggregate<TEntity>(TypedQuery<TEntity> query, IOrganizationService service, LambdaExpression column, XrmAggregateType type)
            where TEntity : Entity
        {
            query = query ?? throw new ArgumentNullException(nameof(query));
            service = service ?? throw new ArgumentNullException(nameof(service));
            column = column ?? throw new ArgumentNullException(nameof(column));

            const string alias = "velaScalar";
            QueryExpression source = query.Expression;

            ColumnSet aggregateColumns = new ColumnSet();
            aggregateColumns.AttributeExpressions.Add(new XrmAttributeExpression
            {
                AttributeName = ExpressionHelper.GetAttributeLogicalName(column),
                Alias = alias,
                AggregateType = type,
            });

            QueryExpression aggregateQuery = new QueryExpression(source.EntityName)
            {
                ColumnSet = aggregateColumns,
                Criteria = source.Criteria,
                Distinct = source.Distinct,
                NoLock = source.NoLock,
                // No Orders / PageInfo / TopCount: they are meaningless for a scalar aggregate and conflict with it.
            };
            foreach (LinkEntity link in source.LinkEntities)
            {
                aggregateQuery.LinkEntities.Add(StripProjection(link));
            }

            DataCollection<Entity> rows = service.RetrieveMultiple(aggregateQuery).Entities;
            if (rows.Count == 0)
            {
                return null;
            }

            object? value = rows[0].Contains(alias) ? rows[0][alias] : null;
            return value is AliasedValue aliased ? aliased.Value : value;
        }

        // Clones a link keeping its join and filter (LinkCriteria, shared read-only) but dropping its projection
        // (Columns) and Orders, so a filtering link does not inject ungrouped columns into the aggregate query.
        private static LinkEntity StripProjection(LinkEntity source)
        {
            LinkEntity copy = new LinkEntity(source.LinkFromEntityName, source.LinkToEntityName, source.LinkFromAttributeName, source.LinkToAttributeName, source.JoinOperator)
            {
                Columns = new ColumnSet(),
                LinkCriteria = source.LinkCriteria,
                EntityAlias = source.EntityAlias,
            };
            foreach (LinkEntity nested in source.LinkEntities)
            {
                copy.LinkEntities.Add(StripProjection(nested));
            }

            return copy;
        }

        private static decimal? ToDecimal(object? value) => value switch
        {
            null => null,
            Money money => money.Value,
            decimal dec => dec,
            _ => Convert.ToDecimal(value),
        };

        // Coerces a min/max aggregate value (already unwrapped from AliasedValue by RunScalarAggregate) to the
        // caller's struct result type. The value is normally that type already; Convert.ChangeType covers the
        // odd provider that widens (for example an int column surfaced as long) and runs in the invariant culture
        // so a decimal is parsed the same everywhere.
        private static TResult? ConvertScalar<TResult>(object? value)
            where TResult : struct
        {
            if (value is null)
            {
                return null;
            }

            if (value is TResult typed)
            {
                return typed;
            }

            return (TResult)Convert.ChangeType(value, typeof(TResult), CultureInfo.InvariantCulture);
        }
    }
}
