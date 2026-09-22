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
}
