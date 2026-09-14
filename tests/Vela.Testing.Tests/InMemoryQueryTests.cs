using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class InMemoryQueryTests
    {
        private static InMemoryOrganizationService SeededAccounts(params string[] names)
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            foreach (string name in names)
            {
                crm.Seed(new Entity("account") { Id = Guid.NewGuid(), ["name"] = name });
            }

            return crm;
        }

        [Fact]
        public void InnerJoin_ExcludesUnmatched_AndProjectsAliasedColumns()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = accountId, ["name"] = "Zava" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", accountId) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Bob" });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet("name");

            EntityCollection result = crm.RetrieveMultiple(query);

            Entity contact = Assert.Single(result.Entities); // Bob has no parent -> excluded by the inner join
            Assert.Equal("Jane", contact.GetAttributeValue<string>("fullname"));
            Assert.Equal("Zava", contact.GetAttributeValue<AliasedValue>("acct.name").Value);
        }

        [Fact]
        public void LeftOuterJoin_KeepsUnmatchedRows()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = accountId, ["name"] = "Zava" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", accountId) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Bob" });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.LeftOuter);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet("name");

            EntityCollection result = crm.RetrieveMultiple(query);

            Assert.Equal(2, result.Entities.Count);
            Entity bob = result.Entities.Single(e => e.GetAttributeValue<string>("fullname") == "Bob");
            Assert.False(bob.Contains("acct.name")); // no match -> no aliased column
        }

        [Fact]
        public void LinkCriteria_FiltersOnTheLinkedEntity()
        {
            Guid zava = Guid.NewGuid();
            Guid fabrikam = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = zava, ["name"] = "Zava" },
                new Entity("account") { Id = fabrikam, ["name"] = "Fabrikam" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", zava) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Bob", ["parentcustomerid"] = new EntityReference("account", fabrikam) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.LinkCriteria.AddCondition("name", ConditionOperator.Equal, "Zava");

            EntityCollection result = crm.RetrieveMultiple(query);

            Entity contact = Assert.Single(result.Entities);
            Assert.Equal("Jane", contact.GetAttributeValue<string>("fullname"));
        }

        [Fact]
        public void NestedLinks_BubbleUpAliasedColumns()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = accountId, ["name"] = "Zava" },
                new Entity("contact") { Id = contactId, ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", accountId) },
                new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "Follow up", ["regardingobjectid"] = new EntityReference("contact", contactId) });

            // account -> contact -> task
            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            LinkEntity contactLink = query.AddLink("contact", "accountid", "parentcustomerid", JoinOperator.Inner);
            contactLink.EntityAlias = "c";
            LinkEntity taskLink = contactLink.AddLink("task", "contactid", "regardingobjectid", JoinOperator.Inner);
            taskLink.EntityAlias = "t";
            taskLink.Columns = new ColumnSet("subject");

            EntityCollection result = crm.RetrieveMultiple(query);

            Entity row = Assert.Single(result.Entities);
            Assert.Equal("Zava", row.GetAttributeValue<string>("name"));
            Assert.Equal("Follow up", row.GetAttributeValue<AliasedValue>("t.subject").Value);
        }

        [Fact]
        public void OrderBy_SortsAscendingAndDescending()
        {
            InMemoryOrganizationService crm = SeededAccounts("Charlie", "Alpha", "Bravo");

            QueryExpression ascending = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            ascending.AddOrder("name", OrderType.Ascending);
            Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, crm.RetrieveMultiple(ascending).Entities.Select(e => e.GetAttributeValue<string>("name")));

            QueryExpression descending = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            descending.AddOrder("name", OrderType.Descending);
            Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, crm.RetrieveMultiple(descending).Entities.Select(e => e.GetAttributeValue<string>("name")));
        }

        [Fact]
        public void TopCount_LimitsAfterOrdering()
        {
            InMemoryOrganizationService crm = SeededAccounts("Charlie", "Alpha", "Bravo");

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), TopCount = 2 };
            query.AddOrder("name", OrderType.Ascending);

            EntityCollection result = crm.RetrieveMultiple(query);

            Assert.Equal(new[] { "Alpha", "Bravo" }, result.Entities.Select(e => e.GetAttributeValue<string>("name")));
        }

        [Theory]
        [InlineData(ConditionOperator.BeginsWith, "Zav", "Zava")]
        [InlineData(ConditionOperator.EndsWith, "ava", "Zava")]
        [InlineData(ConditionOperator.Like, "%av%", "Zava")]
        public void StringOperators_Match(ConditionOperator op, string value, string expected)
        {
            InMemoryOrganizationService crm = SeededAccounts("Zava", "Fabrikam");

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("name", op, value);

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal(expected, match.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void QueryByAttribute_AppliesOrderAndTopCount()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Charlie", ["statecode"] = new OptionSetValue(0) },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Alpha", ["statecode"] = new OptionSetValue(0) },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Bravo", ["statecode"] = new OptionSetValue(1) });

            QueryByAttribute query = new QueryByAttribute("account") { ColumnSet = new ColumnSet("name"), TopCount = 1 };
            query.AddAttributeValue("statecode", new OptionSetValue(0));
            query.AddOrder("name", OrderType.Ascending);

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Alpha", match.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void FetchXml_FiltersOrdersAndProjects()
        {
            InMemoryOrganizationService crm = SeededAccounts("Alpha", "Apex", "Bravo");

            string fetch = @"
                <fetch>
                  <entity name='account'>
                    <attribute name='name' />
                    <filter><condition attribute='name' operator='begins-with' value='A' /></filter>
                    <order attribute='name' descending='true' />
                  </entity>
                </fetch>";

            EntityCollection result = crm.RetrieveMultiple(new FetchExpression(fetch));

            Assert.Equal(new[] { "Apex", "Alpha" }, result.Entities.Select(e => e.GetAttributeValue<string>("name")));
        }

        [Fact]
        public void FetchXml_WithLinkEntity_ProjectsAliasedColumns()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = accountId, ["name"] = "Zava" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", accountId) });

            string fetch = @"
                <fetch>
                  <entity name='contact'>
                    <attribute name='fullname' />
                    <link-entity name='account' from='accountid' to='parentcustomerid' alias='acct'>
                      <attribute name='name' />
                    </link-entity>
                  </entity>
                </fetch>";

            Entity contact = Assert.Single(crm.RetrieveMultiple(new FetchExpression(fetch)).Entities);
            Assert.Equal("Jane", contact.GetAttributeValue<string>("fullname"));
            Assert.Equal("Zava", contact.GetAttributeValue<AliasedValue>("acct.name").Value);
        }

        [Fact]
        public void Paging_ReturnsRequestedPage_AndFlagsMoreRecords()
        {
            InMemoryOrganizationService crm = SeededAccounts("Charlie", "Alpha", "Bravo");

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddOrder("name", OrderType.Ascending);
            query.PageInfo = new PagingInfo { PageNumber = 2, Count = 1 };

            EntityCollection result = crm.RetrieveMultiple(query);

            Entity match = Assert.Single(result.Entities);
            Assert.Equal("Bravo", match.GetAttributeValue<string>("name")); // page 2 of [Alpha, Bravo, Charlie]
            Assert.True(result.MoreRecords);
        }

        [Fact]
        public void Paging_LastPage_DoesNotFlagMoreRecords()
        {
            InMemoryOrganizationService crm = SeededAccounts("Charlie", "Alpha", "Bravo");

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddOrder("name", OrderType.Ascending);
            query.PageInfo = new PagingInfo { PageNumber = 3, Count = 1 };

            EntityCollection result = crm.RetrieveMultiple(query);

            Entity match = Assert.Single(result.Entities);
            Assert.Equal("Charlie", match.GetAttributeValue<string>("name")); // last of [Alpha, Bravo, Charlie]
            Assert.False(result.MoreRecords);
        }

        [Fact]
        public void Distinct_RemovesDuplicateProjectedRows()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Same", ["telephone1"] = "1" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Same", ["telephone1"] = "2" });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), Distinct = true };

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities); // both project to { name = "Same" }
            Assert.Equal("Same", match.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void TypedQuery_RunsDirectlyAgainstTheInMemoryEngine()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new FakeAccount { Id = Guid.NewGuid(), Name = "Zava" },
                new FakeAccount { Id = Guid.NewGuid(), Name = "Fabrikam" });

            // The typed builder converts implicitly to QueryExpression, so it passes straight in.
            EntityCollection result = crm.RetrieveMultiple(
                Query.For<FakeAccount>()
                    .Select(a => a.Name)
                    .Where(a => a.Name, ConditionOperator.BeginsWith, "Zav")
                    .OrderBy(a => a.Name));

            Entity match = Assert.Single(result.Entities);
            Assert.Equal("Zava", match.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void FetchXml_Aggregate_SumGroupedByCategory()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A", ["estimatedvalue"] = new Money(100m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A", ["estimatedvalue"] = new Money(50m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "B", ["estimatedvalue"] = new Money(30m) });

            string fetch = @"
                <fetch aggregate='true'>
                  <entity name='opportunity'>
                    <attribute name='category' groupby='true' alias='cat' />
                    <attribute name='estimatedvalue' aggregate='sum' alias='total' />
                    <order alias='cat' />
                  </entity>
                </fetch>";

            var rows = crm.RetrieveMultiple(new FetchExpression(fetch)).Entities;

            Assert.Equal(2, rows.Count);
            Assert.Equal("A", rows[0].GetAttributeValue<AliasedValue>("cat").Value);
            Assert.Equal(150m, rows[0].GetAttributeValue<AliasedValue>("total").Value);
            Assert.Equal("B", rows[1].GetAttributeValue<AliasedValue>("cat").Value);
            Assert.Equal(30m, rows[1].GetAttributeValue<AliasedValue>("total").Value);
        }

        [Fact]
        public void FetchXml_Aggregate_MultiColumnGroupBy_DoesNotCollideDistinctTuples()
        {
            // Two distinct (city, region) tuples that concatenate to the same string: ("New","York") and
            // ("NewYork",""). Without a group-key separator they collapse into one group of 2.
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["address1_city"] = "New", ["address1_stateorprovince"] = "York" },
                new Entity("account") { Id = Guid.NewGuid(), ["address1_city"] = "NewYork", ["address1_stateorprovince"] = "" });

            string fetch = @"
                <fetch aggregate='true'>
                  <entity name='account'>
                    <attribute name='address1_city' groupby='true' alias='city' />
                    <attribute name='address1_stateorprovince' groupby='true' alias='region' />
                    <attribute name='accountid' aggregate='count' alias='cnt' />
                  </entity>
                </fetch>";

            var rows = crm.RetrieveMultiple(new FetchExpression(fetch)).Entities;

            Assert.Equal(2, rows.Count); // two distinct groups, not one merged group
            Assert.All(rows, r => Assert.Equal(1, (int)r.GetAttributeValue<AliasedValue>("cnt").Value));
        }

        [Fact]
        public void FetchXml_Aggregate_CountAvgMinMax_NoGrouping()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("opportunity") { Id = Guid.NewGuid(), ["estimatedvalue"] = new Money(100m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["estimatedvalue"] = new Money(50m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["estimatedvalue"] = new Money(30m) });

            string fetch = @"
                <fetch aggregate='true'>
                  <entity name='opportunity'>
                    <attribute name='opportunityid' aggregate='count' alias='cnt' />
                    <attribute name='estimatedvalue' aggregate='avg' alias='average' />
                    <attribute name='estimatedvalue' aggregate='min' alias='lowest' />
                    <attribute name='estimatedvalue' aggregate='max' alias='highest' />
                  </entity>
                </fetch>";

            Entity row = Assert.Single(crm.RetrieveMultiple(new FetchExpression(fetch)).Entities);

            Assert.Equal(3, row.GetAttributeValue<AliasedValue>("cnt").Value);
            Assert.Equal(60m, row.GetAttributeValue<AliasedValue>("average").Value);
            Assert.Equal(30m, ((Money)row.GetAttributeValue<AliasedValue>("lowest").Value).Value);
            Assert.Equal(100m, ((Money)row.GetAttributeValue<AliasedValue>("highest").Value).Value);
        }

        [Fact]
        public void ConditionOnLinkedEntity_RecordsNotInASet()
        {
            Guid withContact = Guid.NewGuid();
            Guid withoutContact = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = withContact, ["name"] = "HasContact" },
                new Entity("account") { Id = withoutContact, ["name"] = "Lonely" },
                new Entity("contact") { Id = Guid.NewGuid(), ["parentcustomerid"] = new EntityReference("account", withContact) });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            LinkEntity link = query.AddLink("contact", "accountid", "parentcustomerid", JoinOperator.LeftOuter);
            link.EntityAlias = "c";
            query.Criteria.AddCondition(new ConditionExpression("c", "parentcustomerid", ConditionOperator.Null));

            Entity account = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Lonely", account.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void NotEqual_ExcludesRowsWhereAttributeIsNull()
        {
            // Dataverse three-valued logic: a row with no value for the attribute must NOT match a "ne" filter.
            Guid named = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = named, ["name"] = "Zava" },
                new Entity("account") { Id = Guid.NewGuid() }); // no name attribute at all

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("name", ConditionOperator.NotEqual, "Other");

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal(named, match.Id); // the null-name row is excluded, not returned
        }

        [Fact]
        public void NativeQueryExpressionAggregation_GroupBySum()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A", ["estimatedvalue"] = new Money(100m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "A", ["estimatedvalue"] = new Money(50m) },
                new Entity("opportunity") { Id = Guid.NewGuid(), ["category"] = "B", ["estimatedvalue"] = new Money(30m) });

            QueryExpression query = new QueryExpression("opportunity")
            {
                ColumnSet = new ColumnSet
                {
                    AttributeExpressions =
                    {
                        new XrmAttributeExpression { AttributeName = "category", Alias = "cat", HasGroupBy = true },
                        new XrmAttributeExpression { AttributeName = "estimatedvalue", Alias = "total", AggregateType = XrmAggregateType.Sum }
                    }
                }
            };
            query.AddOrder("cat", OrderType.Ascending);

            var rows = crm.RetrieveMultiple(query).Entities;
            Assert.Equal("A", rows[0].GetAttributeValue<AliasedValue>("cat").Value);
            Assert.Equal(150m, rows[0].GetAttributeValue<AliasedValue>("total").Value);
        }

        [Fact]
        public void ColumnAlias_NonAggregate_ReturnsAliasedValue()
        {
            InMemoryOrganizationService crm = SeededAccounts("Zava");

            QueryExpression query = new QueryExpression("account")
            {
                ColumnSet = new ColumnSet { AttributeExpressions = { new XrmAttributeExpression { AttributeName = "name", Alias = "company" } } }
            };

            Entity row = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Zava", row.GetAttributeValue<AliasedValue>("company").Value);
        }

        [Fact]
        public void CompareColumns_MatchesWhenTwoColumnsAreEqual()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("contact") { Id = Guid.NewGuid(), ["firstname"] = "Jordan", ["lastname"] = "Jordan" },
                new Entity("contact") { Id = Guid.NewGuid(), ["firstname"] = "Alex", ["lastname"] = "Smith" });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("firstname") };
            query.Criteria.AddCondition(new ConditionExpression("firstname", ConditionOperator.Equal, true, "lastname"));

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Jordan", match.GetAttributeValue<string>("firstname"));
        }

        [Fact]
        public void AnyFilter_OnRelatedRecords()
        {
            Guid primary = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("contact") { Id = primary, ["fullname"] = "Jane" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Bob" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava", ["primarycontactid"] = new EntityReference("contact", primary) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            query.Criteria.AnyAllFilterLinkEntity = new LinkEntity("contact", "account", "contactid", "primarycontactid", JoinOperator.Any)
            {
                LinkCriteria = new FilterExpression { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, "Zava") } }
            };

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Jane", match.GetAttributeValue<string>("fullname"));
        }

        [Fact]
        public void ExistsJoin_ReturnsParentOnce_WithoutMultiplying()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = accountId, ["name"] = "Zava" },
                new Entity("contact") { Id = Guid.NewGuid(), ["parentcustomerid"] = new EntityReference("account", accountId) },
                new Entity("contact") { Id = Guid.NewGuid(), ["parentcustomerid"] = new EntityReference("account", accountId) });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddLink("contact", "accountid", "parentcustomerid", JoinOperator.Exists);

            Entity account = Assert.Single(crm.RetrieveMultiple(query).Entities); // 2 contacts, parent returned once
            Assert.Equal("Zava", account.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void BetweenAndEqualUserId_Operators()
        {
            Guid me = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(me)
                .Seed(
                    new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "mine", ["ownerid"] = new EntityReference("systemuser", me), ["actualdurationminutes"] = 15 },
                    new Entity("task") { Id = Guid.NewGuid(), ["subject"] = "theirs", ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()), ["actualdurationminutes"] = 200 });

            QueryExpression query = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
            query.Criteria.AddCondition(new ConditionExpression("ownerid", ConditionOperator.EqualUserId));
            query.Criteria.AddCondition(new ConditionExpression("actualdurationminutes", ConditionOperator.Between, new object[] { 10, 60 }));

            Entity task = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("mine", task.GetAttributeValue<string>("subject"));
        }

        [Fact]
        public void OrderByLinkedColumn_UsesEntityName()
        {
            Guid bId = Guid.NewGuid();
            Guid aId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = bId, ["name"] = "Bravo" },
                new Entity("account") { Id = aId, ["name"] = "Alpha" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "child-of-bravo", ["parentcustomerid"] = new EntityReference("account", bId) },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "child-of-alpha", ["parentcustomerid"] = new EntityReference("account", aId) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet("name");
            query.Orders.Add(new OrderExpression("name", OrderType.Ascending) { EntityName = "acct" });

            var rows = crm.RetrieveMultiple(query).Entities;
            Assert.Equal("child-of-alpha", rows[0].GetAttributeValue<string>("fullname"));
            Assert.Equal("child-of-bravo", rows[1].GetAttributeValue<string>("fullname"));
        }

        [Fact]
        public void ReturnTotalRecordCount_SetsCountAndLimitFlag()
        {
            InMemoryOrganizationService crm = SeededAccounts("A", "B", "C");

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.AddOrder("name", OrderType.Ascending);
            query.PageInfo = new PagingInfo { PageNumber = 1, Count = 2, ReturnTotalRecordCount = true };

            EntityCollection result = crm.RetrieveMultiple(query);
            Assert.Equal(2, result.Entities.Count);
            Assert.Equal(3, result.TotalRecordCount);
            Assert.False(result.TotalRecordCountLimitExceeded);
            Assert.True(result.MoreRecords);
        }

        [Fact]
        public void TypedBuilder_Distinct_RunsOnEngine()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new FakeAccount { Id = Guid.NewGuid(), Name = "Same" },
                new FakeAccount { Id = Guid.NewGuid(), Name = "Same" });

            EntityCollection result = crm.RetrieveMultiple(
                Query.For<FakeAccount>().Select(a => a.Name).Distinct());

            Entity match = Assert.Single(result.Entities);
            Assert.Equal("Same", match.GetAttributeValue<string>("name"));
        }
    }
}
