using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Parity guard: every FetchXML operator that FetchXmlParser.MapOperator maps must be handled by
    // InMemoryQueryEngine.EvaluateCondition. If one is mapped but not evaluated (or vice versa), execution
    // throws "not supported" — this test catches that drift the moment either side changes.
    public sealed class FetchXmlOperatorParityTests
    {
        [Theory]
        [InlineData("<condition attribute='name' operator='eq' value='Acme' />")]
        [InlineData("<condition attribute='name' operator='ne' value='Acme' />")]
        [InlineData("<condition attribute='name' operator='neq' value='Acme' />")]
        [InlineData("<condition attribute='name' operator='null' />")]
        [InlineData("<condition attribute='name' operator='not-null' />")]
        [InlineData("<condition attribute='numberofemployees' operator='gt' value='1' />")]
        [InlineData("<condition attribute='numberofemployees' operator='ge' value='1' />")]
        [InlineData("<condition attribute='numberofemployees' operator='lt' value='99' />")]
        [InlineData("<condition attribute='numberofemployees' operator='le' value='99' />")]
        [InlineData("<condition attribute='name' operator='in'><value>Acme</value></condition>")]
        [InlineData("<condition attribute='name' operator='not-in'><value>Other</value></condition>")]
        [InlineData("<condition attribute='name' operator='like' value='%cm%' />")]
        [InlineData("<condition attribute='name' operator='not-like' value='%zz%' />")]
        [InlineData("<condition attribute='name' operator='begins-with' value='Ac' />")]
        [InlineData("<condition attribute='name' operator='not-begin-with' value='Zz' />")]
        [InlineData("<condition attribute='name' operator='ends-with' value='me' />")]
        [InlineData("<condition attribute='name' operator='not-end-with' value='zz' />")]
        public void Every_parser_supported_operator_executes_without_a_not_supported_error(string condition)
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Acme", ["numberofemployees"] = 10 });

            string fetch = $"<fetch><entity name='account'><attribute name='name' /><filter>{condition}</filter></entity></fetch>";

            // A parser operator that the engine doesn't evaluate throws NotSupportedException here.
            EntityCollection result = crm.RetrieveMultiple(new FetchExpression(fetch));
            Assert.NotNull(result);
        }
    }
}
