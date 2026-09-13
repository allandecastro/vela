using System.Collections.Generic;
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
}
