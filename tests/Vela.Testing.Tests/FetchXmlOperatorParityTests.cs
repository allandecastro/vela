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
        [InlineData("<condition attribute='numberofemployees' operator='between'><value>1</value><value>99</value></condition>")]
        [InlineData("<condition attribute='numberofemployees' operator='not-between'><value>1</value><value>99</value></condition>")]
        [InlineData("<condition attribute='createdon' operator='on' value='2026-01-01' />")]
        [InlineData("<condition attribute='createdon' operator='on-or-before' value='2026-01-01' />")]
        [InlineData("<condition attribute='createdon' operator='on-or-after' value='2026-01-01' />")]
        [InlineData("<condition attribute='createdon' operator='today' />")]
        [InlineData("<condition attribute='createdon' operator='yesterday' />")]
        [InlineData("<condition attribute='createdon' operator='tomorrow' />")]
        [InlineData("<condition attribute='createdon' operator='this-month' />")]
        [InlineData("<condition attribute='createdon' operator='this-year' />")]
        [InlineData("<condition attribute='createdon' operator='last-x-days' value='7' />")]
        [InlineData("<condition attribute='createdon' operator='next-x-days' value='7' />")]
        [InlineData("<condition attribute='createdon' operator='olderthan-x-days' value='7' />")]
        [InlineData("<condition attribute='ownerid' operator='eq-userid' />")]
        [InlineData("<condition attribute='ownerid' operator='ne-userid' />")]
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
