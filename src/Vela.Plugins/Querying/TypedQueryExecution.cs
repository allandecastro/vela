using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

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

            expression.PageInfo = new PagingInfo { PageNumber = 1, Count = pageSize, PagingCookie = null };
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

                expression.PageInfo.PageNumber++;
                expression.PageInfo.PagingCookie = page.PagingCookie;
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
    }
}
