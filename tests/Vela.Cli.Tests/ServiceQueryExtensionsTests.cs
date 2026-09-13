using System;
using System.Collections.Generic;
using Vela.Cli;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Xunit;

namespace Vela.Cli.Tests;

public class ServiceQueryExtensionsTests
{
    private static EntityCollection Page(bool more, params Entity[] rows)
    {
        EntityCollection page = new EntityCollection(new List<Entity>(rows)) { MoreRecords = more };
        if (more)
        {
            page.PagingCookie = "cookie";
        }

        return page;
    }

    [Fact]
    public void RetrieveAll_AggregatesEveryPage()
    {
        Mock<IOrganizationService> service = new Mock<IOrganizationService>(MockBehavior.Strict);
        service.SetupSequence(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(Page(more: true, new Entity("account", Guid.NewGuid())))
            .Returns(Page(more: true, new Entity("account", Guid.NewGuid())))
            .Returns(Page(more: false, new Entity("account", Guid.NewGuid())));

        List<Entity> all = service.Object.RetrieveAll(new QueryExpression("account"));

        Assert.Equal(3, all.Count);
        service.Verify(s => s.RetrieveMultiple(It.IsAny<QueryBase>()), Times.Exactly(3));
    }

    [Fact]
    public void RetrieveAll_PropagatesPageNumberAndPagingCookie()
    {
        // Paging is only correct if each request advances PageNumber and echoes the previous page's cookie;
        // capturing those at call time (the query object is mutated in place) guards against silent regressions.
        List<int> pages = new List<int>();
        List<string?> cookies = new List<string?>();
        int call = 0;
        Mock<IOrganizationService> capturing = new Mock<IOrganizationService>(MockBehavior.Strict);
        capturing.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                QueryExpression query = Assert.IsType<QueryExpression>(q);
                pages.Add(query.PageInfo.PageNumber);
                cookies.Add(query.PageInfo.PagingCookie);
                call++;
                return Page(more: call < 3, new Entity("account", Guid.NewGuid()));
            });

        List<Entity> all = capturing.Object.RetrieveAll(new QueryExpression("account"));

        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { 1, 2, 3 }, pages.ToArray());
        Assert.Null(cookies[0]);            // first request carries no cookie
        Assert.Equal("cookie", cookies[1]); // cookie returned by page 1 is sent with page 2
        Assert.Equal("cookie", cookies[2]); // and again with page 3
    }

    [Fact]
    public void RetrieveAll_DoesNotMutateCallersQuery()   // B12
    {
        Mock<IOrganizationService> service = new Mock<IOrganizationService>(MockBehavior.Strict);
        service.SetupSequence(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(Page(more: true, new Entity("account", Guid.NewGuid())))
            .Returns(Page(more: false, new Entity("account", Guid.NewGuid())));

        QueryExpression query = new QueryExpression("account");
        PagingInfo? original = query.PageInfo;

        service.Object.RetrieveAll(query);

        // The caller's PageInfo must be restored to exactly what it was - RetrieveAll has no lasting side effect.
        Assert.Same(original, query.PageInfo);
    }

    [Fact]
    public void RetrieveAll_HonoursTopCount_WithASinglePage()
    {
        Mock<IOrganizationService> service = new Mock<IOrganizationService>(MockBehavior.Strict);
        // MoreRecords is true, but a TopCount asks for a bounded result, so we must not page.
        service.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(Page(more: true, new Entity("account", Guid.NewGuid())));

        List<Entity> all = service.Object.RetrieveAll(new QueryExpression("account") { TopCount = 5 });

        Assert.Single(all);
        service.Verify(s => s.RetrieveMultiple(It.IsAny<QueryBase>()), Times.Once);
    }
}
