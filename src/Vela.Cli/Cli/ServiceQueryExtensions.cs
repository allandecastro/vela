using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Vela.Cli;

/// <summary>
/// Query helpers for reading full result sets from Dataverse. A plain <c>RetrieveMultiple</c> returns at most
/// one page (5000 rows by default) and silently drops the rest, which would make the registration diff miss
/// existing steps/Custom APIs/images and create duplicates on apply. <see cref="RetrieveAll"/> pages through
/// every record.
/// </summary>
internal static class ServiceQueryExtensions
{
    private const int DefaultPageSize = 500;

    // How many values one ConditionOperator.In carries. Each value becomes a SQL parameter, and SQL Server caps a
    // statement at 2100 of them, so a whole-environment id list (every step of a large assembly, every component of a
    // solution) must be split across several queries.
    internal const int InChunkSize = 500;

    /// <summary>
    /// Retrieves every matching record by paging until the server reports no more. An explicit
    /// <see cref="QueryExpression.TopCount"/> is honoured (single page, capped); otherwise the query is paged.
    /// </summary>
    public static List<Entity> RetrieveAll(this IOrganizationService service, QueryExpression query)
    {
        List<Entity> all = new List<Entity>();

        // A TopCount asks for a bounded result — respect it and don't page.
        if (query.TopCount.HasValue)
        {
            all.AddRange(service.RetrieveMultiple(query).Entities);
            return all;
        }

        // Paging sets query.PageInfo; restore the caller's original when done so RetrieveAll has no side effect on
        // the passed-in QueryExpression (B12).
        PagingInfo? originalPaging = query.PageInfo;
        try
        {
            query.PageInfo = new PagingInfo
            {
                Count = DefaultPageSize,
                PageNumber = 1,
                PagingCookie = null,
            };

            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(query);
                all.AddRange(page.Entities);

                if (!page.MoreRecords)
                {
                    return all;
                }

                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
        }
        finally
        {
            query.PageInfo = originalPaging;
        }
    }

    /// <summary>
    /// Retrieves every record matching a <see cref="ConditionOperator.In"/> list of any length: the distinct values are
    /// split into chunks of <paramref name="chunkSize"/>, <paramref name="buildQuery"/> builds a fresh query for each
    /// chunk, and every chunk is paged with <see cref="RetrieveAll"/>. No query is issued for an empty list. The chunks
    /// are disjoint, so a row matched through the In attribute comes back once.
    /// </summary>
    public static List<Entity> RetrieveAllInChunks<T>(this IOrganizationService service, IEnumerable<T> values, Func<object[], QueryExpression> buildQuery, int chunkSize = InChunkSize)
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "The chunk size must be positive.");
        }

        List<Entity> all = new List<Entity>();
        foreach (T[] chunk in values.Distinct().Chunk(chunkSize))
        {
            all.AddRange(service.RetrieveAll(buildQuery(chunk.Cast<object>().ToArray())));
        }

        return all;
    }
}
