using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// Query.For<T>() authors a query with compile-time IntelliSense; ToFetchXml() emits the FetchXML string for the
// messages/tools that require it. The lambdas make table/column typos a compile error.
public sealed class TypedQueryFetchXmlTests
{
    [Fact]
    public void Typed_query_emits_fetchxml()
    {
        string xml = Query.For<TestAccount>()
            .Select(a => a.Name)
            .Where(a => a.Name, ConditionOperator.Equal, "Zava")
            .OrderBy(a => a.Name)
            .Top(5)
            .ToFetchXml();

        Assert.Contains("top=\"5\"", xml);
        Assert.Contains("<entity name=\"account\">", xml);
        Assert.Contains("<attribute name=\"name\"", xml);
        Assert.Contains("<condition attribute=\"name\" operator=\"eq\" value=\"Zava\"", xml);
        Assert.Contains("<order attribute=\"name\"", xml);
    }

    [Fact]
    public void Fetchxml_only_knob_is_emitted()
    {
        string xml = Query.For<TestAccount>().Select(a => a.Name).LateMaterialize().ToFetchXml();
        Assert.Contains("latematerialize=\"true\"", xml);
    }

    [Fact]
    public void Fetchxml_only_long_tail_knobs_emit()
    {
        string xml = Query.For<TestAccount>()
            .CountColumnDistinct(a => a.AccountId, "u")
            .OrderByAggregate("u", descending: true)
            .SqlOptions("HashJoin")
            .FromRetainedData()
            .RawChoiceOrder()
            .UtcDateGrouping()
            .RowAggregate(a => a.AccountId, "kids", "CountChildren")
            .ToFetchXml();

        Assert.Contains("distinct=\"true\"", xml);
        Assert.Contains("options=\"HashJoin\"", xml);
        Assert.Contains("datasource=\"retained\"", xml);
        Assert.Contains("useraworderby=\"true\"", xml);
        Assert.Contains("rowaggregate=\"CountChildren\"", xml);
        Assert.Contains("<order alias=\"u\" descending=\"true\"", xml);
    }
}
