using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Exercises FetchXmlParser branches the operator/aggregate suites don't reach: the validation guards,
    // fetch-level top/count/page paging, descending and all-attributes parsing, link-entity outer/alias/
    // filter/nested forms, or-filters with nested filters, and the aggregate filter/order paths. All run
    // through the public FetchExpression path since the parser is internal.
    public sealed class FetchXmlParserCoverageTests
    {
        private static InMemoryOrganizationService Org()
        {
            Guid acme = new Guid("11111111-1111-1111-1111-111111111111");
            Guid globex = new Guid("22222222-2222-2222-2222-222222222222");
            return new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = acme, ["name"] = "Acme", ["numberofemployees"] = 10 },
                new Entity("account") { Id = globex, ["name"] = "Globex", ["numberofemployees"] = 20 },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Initech", ["numberofemployees"] = 30 },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Wile", ["parentcustomerid"] = new EntityReference("account", acme) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Hank", ["parentcustomerid"] = new EntityReference("account", globex) });
        }

        private static EntityCollection Fetch(InMemoryOrganizationService org, string fetchXml)
        {
            return org.RetrieveMultiple(new FetchExpression(fetchXml));
        }

        [Fact]
        public void Fetch_without_an_entity_element_throws()
        {
            Assert.Throws<NotSupportedException>(() => Fetch(Org(), "<fetch></fetch>"));
        }

        [Fact]
        public void Top_limits_the_row_count()
        {
            EntityCollection result = Fetch(Org(), "<fetch top='2'><entity name='account'><attribute name='name' /></entity></fetch>");
            Assert.Equal(2, result.Entities.Count);
        }

        [Fact]
        public void Count_and_page_drive_paging()
        {
            // Page 2 with a page size of 2: the third account (ordered by name) is returned.
            string fetch = "<fetch count='2' page='2'><entity name='account'><attribute name='name' /><order attribute='name' /></entity></fetch>";
            EntityCollection result = Fetch(Org(), fetch);
            Entity only = Assert.Single(result.Entities);
            Assert.Equal("Initech", only.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void Count_without_page_defaults_to_the_first_page()
        {
            string fetch = "<fetch count='2'><entity name='account'><attribute name='name' /><order attribute='name' /></entity></fetch>";
            EntityCollection result = Fetch(Org(), fetch);
            Assert.Equal(new[] { "Acme", "Globex" }, result.Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray());
            Assert.True(result.MoreRecords);
        }

        [Fact]
        public void Descending_order_is_parsed()
        {
            string fetch = "<fetch><entity name='account'><attribute name='name' /><order attribute='name' descending='true' /></entity></fetch>";
            EntityCollection result = Fetch(Org(), fetch);
            Assert.Equal(new[] { "Initech", "Globex", "Acme" }, result.Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray());
        }

        [Fact]
        public void All_attributes_returns_every_column()
        {
            string fetch = "<fetch top='1'><entity name='account'><all-attributes /><order attribute='name' /></entity></fetch>";
            Entity first = Assert.Single(Fetch(Org(), fetch).Entities);
            Assert.Equal("Acme", first.GetAttributeValue<string>("name"));
            Assert.Equal(10, first.GetAttributeValue<int>("numberofemployees")); // a non-projected column survives
        }

        [Fact]
        public void Outer_link_with_alias_filter_and_nested_link_is_parsed()
        {
            // Outer join keeps Initech (no contacts); the contact filter narrows the matched rows; the nested
            // link-entity is parsed even though it adds no extra rows here.
            string fetch = @"
                <fetch>
                  <entity name='account'>
                    <attribute name='name' />
                    <link-entity name='contact' from='parentcustomerid' to='accountid' link-type='outer' alias='c'>
                      <attribute name='fullname' />
                      <filter>
                        <condition attribute='fullname' operator='eq' value='Wile' />
                      </filter>
                      <link-entity name='systemuser' from='systemuserid' to='owninguser' link-type='outer' alias='u' />
                    </link-entity>
                  </entity>
                </fetch>";

            string[] names = Fetch(Org(), fetch).Entities
                .Select(e => e.GetAttributeValue<string>("name"))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            // Acme matches Wile; Globex/Initech are kept by the outer join (their contacts don't match the filter).
            Assert.Contains("Acme", names);
            Assert.Contains("Initech", names);
        }

        [Fact]
        public void Or_filter_with_a_nested_filter_is_parsed()
        {
            string fetch = @"
                <fetch>
                  <entity name='account'>
                    <attribute name='name' />
                    <filter type='or'>
                      <condition attribute='name' operator='eq' value='Acme' />
                      <filter type='and'>
                        <condition attribute='numberofemployees' operator='ge' value='20' />
                        <condition attribute='numberofemployees' operator='lt' value='30' />
                      </filter>
                    </filter>
                  </entity>
                </fetch>";

            string[] names = Fetch(Org(), fetch).Entities
                .Select(e => e.GetAttributeValue<string>("name"))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "Acme", "Globex" }, names); // Acme by name; Globex by the nested 20..29 range
        }

        [Fact]
        public void Aggregate_without_an_entity_element_throws()
        {
            Assert.Throws<NotSupportedException>(() => Fetch(Org(), "<fetch aggregate='true'></fetch>"));
        }

        [Fact]
        public void Aggregate_applies_a_filter_and_orders_by_a_bare_attribute()
        {
            // The filter drops Acme (10 employees); grouping by name with an unaliased order exercises the
            // filter and the "order has no alias -> use attribute" branch.
            string fetch = @"
                <fetch aggregate='true'>
                  <entity name='account'>
                    <attribute name='name' groupby='true' alias='nm' />
                    <attribute name='accountid' aggregate='count' alias='cnt' />
                    <filter>
                      <condition attribute='numberofemployees' operator='ge' value='20' />
                    </filter>
                    <order attribute='nm' />
                  </entity>
                </fetch>";

            var rows = Fetch(Org(), fetch).Entities;
            Assert.Equal(2, rows.Count); // Globex and Initech survive the filter; Acme is excluded
        }
    }
}
