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
}
