using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Drives every FetchXML operator through the parser (MapOperator) and the query engine, so both the
    // operator mapping and the engine's condition evaluation are exercised (the thinly-covered hotspots).
    public sealed class FetchXmlOperatorCoverageTests
    {
        private static InMemoryOrganizationService Seeded()
        {
            // Alpha (100), Bravo (200), Charlie (no employee count -> null).
            return new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Alpha", ["numberofemployees"] = 100 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Bravo", ["numberofemployees"] = 200 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Charlie" });
        }

        [Theory]
        // expectedNames pins WHICH rows match (ordinal-sorted), not just how many — so an operator that returns
        // the wrong rows of the right count is caught.
        [InlineData("<condition attribute='name' operator='eq' value='Alpha' />", "Alpha")]
        [InlineData("<condition attribute='name' operator='ne' value='Alpha' />", "Bravo,Charlie")]
        [InlineData("<condition attribute='name' operator='neq' value='Alpha' />", "Bravo,Charlie")]
        [InlineData("<condition attribute='numberofemployees' operator='null' />", "Charlie")]
        [InlineData("<condition attribute='numberofemployees' operator='not-null' />", "Alpha,Bravo")]
        [InlineData("<condition attribute='name' operator='gt' value='Alpha' />", "Bravo,Charlie")]
        [InlineData("<condition attribute='name' operator='ge' value='Bravo' />", "Bravo,Charlie")]
        [InlineData("<condition attribute='name' operator='lt' value='Bravo' />", "Alpha")]
        [InlineData("<condition attribute='name' operator='le' value='Bravo' />", "Alpha,Bravo")]
        [InlineData("<condition attribute='name' operator='in'><value>Alpha</value><value>Bravo</value></condition>", "Alpha,Bravo")]
        [InlineData("<condition attribute='name' operator='not-in'><value>Alpha</value></condition>", "Bravo,Charlie")]
        [InlineData("<condition attribute='name' operator='like' value='%lph%' />", "Alpha")]
        [InlineData("<condition attribute='name' operator='not-like' value='%lph%' />", "Bravo,Charlie")]
        [InlineData("<condition attribute='name' operator='begins-with' value='A' />", "Alpha")]
        [InlineData("<condition attribute='name' operator='not-begin-with' value='A' />", "Bravo,Charlie")]
        [InlineData("<condition attribute='name' operator='ends-with' value='a' />", "Alpha")]
        [InlineData("<condition attribute='name' operator='not-end-with' value='a' />", "Bravo,Charlie")]
        public void FetchXml_operator_maps_and_filters(string condition, string expectedNames)
        {
            string fetch = $"<fetch><entity name='account'><attribute name='name' /><filter>{condition}</filter></entity></fetch>";

            EntityCollection result = Seeded().RetrieveMultiple(new FetchExpression(fetch));

            string[] actual = result.Entities
                .Select(e => e.GetAttributeValue<string>("name"))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedNames.Split(','), actual);
        }

        [Fact]
        public void FetchXml_unknown_operator_is_rejected()
        {
            string fetch = "<fetch><entity name='account'><filter><condition attribute='name' operator='wat' value='x' /></filter></entity></fetch>";

            Assert.Throws<NotSupportedException>(() => Seeded().RetrieveMultiple(new FetchExpression(fetch)));
        }
    }
}
