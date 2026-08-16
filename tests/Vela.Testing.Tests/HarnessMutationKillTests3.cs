using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Third wave of mutation-survivor kills for the in-memory query core (InMemoryQueryEngine / FetchXmlParser /
    // EntityValues). Targets the *behavioural* survivors — result EntityName, the QueryByAttribute path,
    // link-entity order collection and alias/all-columns projection, NotBetween boundary inclusivity, aggregate
    // filtering, and the "clear message" throw contracts. The remaining survivors are equivalent mutants
    // (unreachable empty-fetch guard behind XDocument.Parse, never-null/never-empty edges) left suppressed.
    public sealed class HarnessMutationKillTests3
    {
        private static InMemoryOrganizationService Crm(params Entity[] rows) =>
            new InMemoryOrganizationService().Seed(rows);

        // BuildCollection stamps the queried logical name onto the result (kills emptying the
        // `new EntityCollection { EntityName = entityName }` initializer).
        [Fact]
        public void RetrieveMultiple_result_carries_the_entity_name()
        {
            EntityCollection result = Crm(new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava" })
                .RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet("name") });

            Assert.Equal("account", result.EntityName);
        }

        // ExecuteAggregate stamps the entity name onto its result too (kills the same initializer on the aggregate
        // path, which is a distinct code location).
        [Fact]
        public void Aggregate_result_carries_the_entity_name()
        {
            EntityCollection result = Crm(new Entity("account") { Id = Guid.NewGuid(), ["numberofemployees"] = 10 })
                .RetrieveMultiple(new FetchExpression(
                    "<fetch aggregate='true'><entity name='account'>" +
                    "<attribute name='numberofemployees' alias='total' aggregate='sum'/></entity></fetch>"));

            Assert.Equal("account", result.EntityName);
        }

        // QueryByAttribute must EXCLUDE a non-matching row (kills flipping the loop's `return false` to `return
        // true`, which would let every row through).
        [Fact]
        public void QueryByAttribute_excludes_non_matching_rows()
        {
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Acme" });

            QueryByAttribute query = new QueryByAttribute("account") { ColumnSet = new ColumnSet("name") };
            query.AddAttributeValue("name", "Zava");

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Zava", match.GetAttributeValue<string>("name"));
        }

        // With fewer Values than Attributes, the missing value is compared as null (kills the off-by-one
        // `i <= query.Values.Count` and the `(true ? Values[i] : null)` conditional, both of which would index
        // past the end of Values and throw).
        [Fact]
        public void QueryByAttribute_treats_a_missing_value_as_null()
        {
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava" }); // no statecode -> null

            QueryByAttribute query = new QueryByAttribute("account") { ColumnSet = new ColumnSet("name") };
            query.Attributes.Add("name");
            query.Attributes.Add("statecode");
            query.Values.Add("Zava"); // deliberately one fewer value than attributes

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Zava", match.GetAttributeValue<string>("name"));
        }

        // NotBetween is inclusive on both bounds: a value equal to the lower/upper bound IS "between", so
        // NotBetween excludes it (kills relaxing `>=`/`<=` to `>`/`<`, which would include the boundary value).
        [Fact]
        public void NotBetween_excludes_a_value_on_the_boundary()
        {
            InMemoryOrganizationService crm = Crm(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "low", ["actualdurationminutes"] = 50 },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "onbound", ["actualdurationminutes"] = 100 });

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition("actualdurationminutes", ConditionOperator.NotBetween, 100, 200);

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("low", match.GetAttributeValue<string>("subject")); // the boundary row (100) is excluded
        }

        // NotBetween's UPPER bound is inclusive too: a value equal to the upper bound IS "between", so it is
        // excluded (kills relaxing the upper `<=` to `<`, which the lower-boundary test above doesn't cover).
        [Fact]
        public void NotBetween_excludes_a_value_on_the_upper_boundary()
        {
            InMemoryOrganizationService crm = Crm(
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "high", ["actualdurationminutes"] = 300 },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "onupper", ["actualdurationminutes"] = 200 });

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition("actualdurationminutes", ConditionOperator.NotBetween, 100, 200);

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("high", match.GetAttributeValue<string>("subject")); // the upper-boundary row (200) is excluded
        }

        // A FetchXML <attribute name='...'/> projects exactly the named columns (kills forcing the empty
        // ColumnSet(false) branch instead of ColumnSet(names)).
        [Fact]
        public void FetchXml_projects_only_the_named_attributes()
        {
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava", ["numberofemployees"] = 50 });

            FetchExpression fetch = new FetchExpression(
                "<fetch><entity name='account'><attribute name='name' /></entity></fetch>");

            Entity row = Assert.Single(crm.RetrieveMultiple(fetch).Entities);
            Assert.Equal("Zava", row.GetAttributeValue<string>("name")); // the named column IS projected
            Assert.False(row.Contains("numberofemployees"));             // ...and only that one (not all attributes)
        }

        // A link-entity Order is collected and applied under the link's alias (kills dropping the
        // CollectLinkOrders call, the OrderSpec initializer, and the alias selection).
        [Fact]
        public void Link_entity_order_is_applied_under_its_alias()
        {
            Guid zava = Guid.NewGuid();
            Guid acme = Guid.NewGuid();
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = zava, ["name"] = "Zava" },
                new Entity("account") { Id = acme, ["name"] = "Acme" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "C-Zava", ["parentcustomerid"] = new EntityReference("account", zava) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "C-Acme", ["parentcustomerid"] = new EntityReference("account", acme) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet("name");
            link.Orders.Add(new OrderExpression("name", OrderType.Ascending));

            var rows = crm.RetrieveMultiple(query).Entities;

            // Ordered by the linked account's name ascending: Acme's contact before Zava's.
            Assert.Equal("C-Acme", rows[0].GetAttributeValue<string>("fullname"));
            Assert.Equal("C-Zava", rows[1].GetAttributeValue<string>("fullname"));
        }

        // A nested (grand-child) link-entity Order is collected recursively (kills dropping the recursive
        // CollectLinkOrders call on child links).
        [Fact]
        public void Nested_link_entity_order_is_applied()
        {
            Guid zava = Guid.NewGuid();
            Guid acme = Guid.NewGuid();
            Guid cZava = Guid.NewGuid();
            Guid cAcme = Guid.NewGuid();
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = zava, ["name"] = "Zava" },
                new Entity("account") { Id = acme, ["name"] = "Acme" },
                new Entity("contact") { Id = cZava, ["fullname"] = "Con-Z", ["parentcustomerid"] = new EntityReference("account", zava) },
                new Entity("contact") { Id = cAcme, ["fullname"] = "Con-A", ["parentcustomerid"] = new EntityReference("account", acme) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "T-Z", ["regardingobjectid"] = new EntityReference("contact", cZava) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "T-A", ["regardingobjectid"] = new EntityReference("contact", cAcme) });

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            LinkEntity contactLink = query.AddLink("contact", "regardingobjectid", "contactid", JoinOperator.Inner);
            contactLink.EntityAlias = "c";
            LinkEntity accountLink = contactLink.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            accountLink.EntityAlias = "a";
            accountLink.Columns = new ColumnSet("name");
            accountLink.Orders.Add(new OrderExpression("name", OrderType.Ascending));

            var rows = crm.RetrieveMultiple(query).Entities;

            // Ordered by the grand-child account's name ascending: Acme's task before Zava's.
            Assert.Equal("T-A", rows[0].GetAttributeValue<string>("subject"));
            Assert.Equal("T-Z", rows[1].GetAttributeValue<string>("subject"));
        }

        // A link-entity with AllColumns projects every attribute of the target row as an aliased column, including
        // ones not explicitly listed (kills the `link.Columns == null || AllColumns` -> `&&` mutation and the
        // conditional that would fall back to link.Columns.Columns).
        [Fact]
        public void Link_entity_all_columns_projects_every_target_attribute()
        {
            Guid zava = Guid.NewGuid();
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = zava, ["name"] = "Zava", ["numberofemployees"] = 42 },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Wile", ["parentcustomerid"] = new EntityReference("account", zava) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet(true); // all columns

            Entity row = Assert.Single(crm.RetrieveMultiple(query).Entities);

            // numberofemployees was not explicitly listed, but AllColumns must expose it under the alias.
            AliasedValue employees = row.GetAttributeValue<AliasedValue>("acct.numberofemployees");
            Assert.NotNull(employees);
            Assert.Equal(42, employees.Value);
        }

        // Without an EntityAlias, aliased link columns fall back to the linked entity's logical name (kills the
        // conditional that would always use the empty EntityAlias).
        [Fact]
        public void Link_entity_without_alias_uses_the_linked_entity_name()
        {
            Guid zava = Guid.NewGuid();
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = zava, ["name"] = "Zava" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Wile", ["parentcustomerid"] = new EntityReference("account", zava) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.Columns = new ColumnSet("name"); // no EntityAlias set

            Entity row = Assert.Single(crm.RetrieveMultiple(query).Entities);

            Assert.Equal("Zava", row.GetAttributeValue<AliasedValue>("account.name").Value);
        }

        // An aggregate query applies its <filter> before aggregating (kills dropping the AddFilter call, which
        // would count every row instead of only the filtered ones).
        [Fact]
        public void Aggregate_applies_the_filter_before_counting()
        {
            InMemoryOrganizationService crm = Crm(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "A", ["statecode"] = new OptionSetValue(0) },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "B", ["statecode"] = new OptionSetValue(0) },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "C", ["statecode"] = new OptionSetValue(1) });

            EntityCollection result = crm.RetrieveMultiple(new FetchExpression(
                "<fetch aggregate='true'><entity name='account'>" +
                "<attribute name='accountid' alias='cnt' aggregate='count'/>" +
                "<filter><condition attribute='statecode' operator='eq' value='0'/></filter>" +
                "</entity></fetch>"));

            Entity row = Assert.Single(result.Entities);
            Assert.Equal(2, row.GetAttributeValue<AliasedValue>("cnt").Value); // only the two statecode=0 rows
        }

        // Unsupported FetchXML operators throw a clear, operator-naming message (kills mutating the interpolated
        // message to a constant).
        [Fact]
        public void Unsupported_operator_names_the_operator_in_the_message()
        {
            InMemoryOrganizationService crm = Crm(new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava" });

            NotSupportedException ex = Assert.Throws<NotSupportedException>(() => crm.RetrieveMultiple(new FetchExpression(
                "<fetch><entity name='account'><attribute name='name'/>" +
                "<filter><condition attribute='name' operator='super-fuzzy' value='z'/></filter></entity></fetch>")));

            Assert.Contains("super-fuzzy", ex.Message);
        }

        // Fetch without an <entity> element throws a message that names the missing element (kills mutating the
        // message to a constant; the throw itself is already covered elsewhere).
        [Fact]
        public void Missing_entity_element_message_names_the_entity_element()
        {
            InMemoryOrganizationService crm = Crm(new Entity("account") { Id = Guid.NewGuid() });

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => crm.RetrieveMultiple(new FetchExpression("<fetch></fetch>")));

            Assert.Contains("entity", ex.Message);
        }

        // Comparing against a missing operand throws the "cannot compare" message (kills mutating that message and
        // covers the previously-uncovered throw in EntityValues.Compare).
        [Fact]
        public void Comparison_without_an_operand_throws_the_compare_message()
        {
            InMemoryOrganizationService crm = Crm(new Entity("account") { Id = Guid.NewGuid(), ["numberofemployees"] = 10 });

            NotSupportedException ex = Assert.Throws<NotSupportedException>(() => crm.RetrieveMultiple(new FetchExpression(
                "<fetch><entity name='account'><attribute name='name'/>" +
                "<filter><condition attribute='numberofemployees' operator='gt'/></filter></entity></fetch>")));

            Assert.Contains("compare", ex.Message);
        }
    }
}
