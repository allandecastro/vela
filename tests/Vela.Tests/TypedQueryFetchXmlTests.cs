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
    public void WhereIn_with_a_Guid_array_expands_each_element_as_a_value()
    {
        System.Guid a = System.Guid.NewGuid();
        System.Guid b = System.Guid.NewGuid();

        string xml = Query.For<TestAccount>()
            .Select(x => x.Name)
            .WhereIn(x => x.AccountId, new[] { a, b })   // a Guid[] - the params object[] trap would nest it as one value
            .ToFetchXml();

        Assert.Contains("operator=\"in\"", xml);
        Assert.Contains($"<value>{a}</value>", xml);
        Assert.Contains($"<value>{b}</value>", xml);
    }

    [Fact]
    public void WhereNotIn_emits_not_in_with_each_element()
    {
        string xml = Query.For<TestAccount>()
            .Select(x => x.Name)
            .WhereNotIn(x => x.Name, new[] { "A", "B" })
            .ToFetchXml();

        Assert.Contains("operator=\"not-in\"", xml);
        Assert.Contains("<value>A</value>", xml);
        Assert.Contains("<value>B</value>", xml);
    }

    [Fact]
    public void In_with_a_single_value_uses_value_child_elements()   // A3
    {
        string xml = Query.For<TestAccount>()
            .Select(a => a.Name)
            .Where(a => a.Name, ConditionOperator.In, "Zava")
            .ToFetchXml();

        Assert.Contains("operator=\"in\"", xml);
        Assert.Contains("<value>Zava</value>", xml);          // list operator -> child element even for one value
        Assert.DoesNotContain("value=\"Zava\"", xml);          // never the compact attribute form
    }

    [Theory]                                                    // A15: irregular operators map to their FetchXML names
    [InlineData(ConditionOperator.EqualUserId, "eq-userid")]
    [InlineData(ConditionOperator.NotEqualUserId, "ne-userid")]
    [InlineData(ConditionOperator.EqualBusinessId, "eq-businessid")]
    public void Irregular_zero_arg_operators_map_to_fetch_names(ConditionOperator op, string expected)
    {
        string xml = Query.For<TestAccount>().Select(a => a.Name).Where(a => a.Name, op).ToFetchXml();
        Assert.Contains($"operator=\"{expected}\"", xml);
    }

    [Fact]
    public void OlderThanX_operators_map_to_olderthan_x_unit()   // A15
    {
        string xml = Query.For<TestAccount>().Select(a => a.Name)
            .Where(a => a.Name, ConditionOperator.OlderThanXMonths, 6)
            .ToFetchXml();

        Assert.Contains("operator=\"olderthan-x-months\"", xml);
        Assert.Contains("value=\"6\"", xml);
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
