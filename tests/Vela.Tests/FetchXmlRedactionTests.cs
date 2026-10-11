using Vela.Context;
using Xunit;

namespace Vela.Tests;

public sealed class FetchXmlRedactionTests
{
    [Fact]
    public void Redacts_attribute_form_condition_values()
    {
        string fetch = "<fetch><entity name='contact'><filter><condition attribute='emailaddress1' operator='eq' value=\"ada@contoso.com\" /></filter></entity></fetch>";

        string redacted = TracingOrganizationService.RedactFetchXmlValues(fetch);

        Assert.DoesNotContain("ada@contoso.com", redacted);
        Assert.Contains("value=\"***\"", redacted);
        // Query shape is preserved for diagnostics.
        Assert.Contains("attribute='emailaddress1'", redacted);
        Assert.Contains("operator='eq'", redacted);
    }

    [Fact]
    public void Redacts_multi_value_element_form()
    {
        string fetch = "<fetch><entity name='account'><filter><condition attribute='name' operator='in'><value>Contoso</value><value>Fabrikam</value></condition></filter></entity></fetch>";

        string redacted = TracingOrganizationService.RedactFetchXmlValues(fetch);

        Assert.DoesNotContain("Contoso", redacted);
        Assert.DoesNotContain("Fabrikam", redacted);
        Assert.Contains("<value>***</value>", redacted);
    }

    [Fact]
    public void Redacts_a_cdata_value_even_when_it_contains_markup()
    {
        string fetch = "<fetch><entity name='contact'><filter><condition attribute='lastname' operator='in'><value><![CDATA[O'Brien <ada@contoso.com>]]></value><value>Plain</value></condition></filter></entity></fetch>";

        string redacted = TracingOrganizationService.RedactFetchXmlValues(fetch);

        Assert.DoesNotContain("O'Brien", redacted);
        Assert.DoesNotContain("ada@contoso.com", redacted);
        Assert.DoesNotContain("Plain", redacted);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(redacted, "<value>\\*\\*\\*</value>").Count);
        Assert.Contains("attribute='lastname'", redacted);
    }
}
