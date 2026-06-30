using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Second wave of mutation-survivor kills: clone fidelity, Like anchoring/case, and link column projection.
    // (The equivalent survivors — exception messages, never-empty groups, key separators — are suppressed with
    // `// Stryker disable once` comments at the source instead of asserted here.)
    public sealed class HarnessMutationKillTests2
    {
        // Clone preserves EntityReference.Name (kills dropping the `{ Name = ... }` initializer in CloneValue).
        // Retrieve returns a deep clone, so the read-back reference exercises CloneValue.
        [Fact]
        public void Clone_preserves_entity_reference_name()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account")
                {
                    Id = id,
                    ["name"] = "Zava",
                    ["primarycontactid"] = new EntityReference("contact", Guid.NewGuid()) { Name = "Jane Doe" }
                });

            Entity read = crm.Retrieve("account", id, new ColumnSet(true));

            Assert.Equal("Jane Doe", read.GetAttributeValue<EntityReference>("primarycontactid").Name);
        }

        // Clone preserves EntityCollection.EntityName (kills dropping the `{ EntityName = ... }` initializer).
        [Fact]
        public void Clone_preserves_entity_collection_name()
        {
            Guid id = Guid.NewGuid();
            EntityCollection party = new EntityCollection(new[]
            {
                new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()) }
            })
            { EntityName = "activityparty" };

            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("email") { Id = id, ["subject"] = "Hi", ["to"] = party });

            Entity read = crm.Retrieve("email", id, new ColumnSet(true));

            Assert.Equal("activityparty", read.GetAttributeValue<EntityCollection>("to").EntityName);
        }

        // Like is anchored ('^pattern$'): a wildcard-free pattern matches the whole value only, not a substring
        // (kills emptying the regex anchors).
        [Fact]
        public void Like_is_anchored_and_does_not_match_a_substring()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "ava" },
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Zava" });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("name", ConditionOperator.Like, "ava");

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("ava", match.GetAttributeValue<string>("name")); // "Zava" must NOT match an anchored 'ava'
        }

        // Like is case-insensitive (kills flipping RegexOptions.IgnoreCase | Singleline to a bitwise-AND, which
        // drops IgnoreCase).
        [Fact]
        public void Like_is_case_insensitive()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "ZAVA" });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition("name", ConditionOperator.Like, "zava");

            Entity match = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("ZAVA", match.GetAttributeValue<string>("name"));
        }

        // A link-entity with an explicit ColumnSet projects ONLY those columns (kills forcing all linked columns).
        [Fact]
        public void Link_with_explicit_columns_excludes_unlisted_linked_columns()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = accountId, ["name"] = "Zava", ["telephone1"] = "555" },
                new Entity("contact") { Id = Guid.NewGuid(), ["fullname"] = "Jane", ["parentcustomerid"] = new EntityReference("account", accountId) });

            QueryExpression query = new QueryExpression("contact") { ColumnSet = new ColumnSet("fullname") };
            LinkEntity link = query.AddLink("account", "parentcustomerid", "accountid", JoinOperator.Inner);
            link.EntityAlias = "acct";
            link.Columns = new ColumnSet("name"); // telephone1 deliberately not requested

            Entity contact = Assert.Single(crm.RetrieveMultiple(query).Entities);
            Assert.Equal("Zava", contact.GetAttributeValue<AliasedValue>("acct.name").Value);
            Assert.False(contact.Contains("acct.telephone1")); // unlisted linked column must not be projected
        }
    }
}
