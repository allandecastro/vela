using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Testing.Tests
{
    // Small public helpers and the N:N registry edge cases: AutoNumber padding on/off, SequentialIds bounds,
    // RelationshipRegistry validation guards and the self-referential / second-side attribute mapping
    // (reached through the in-memory service's WithManyToMany + Associate).
    public sealed class MiscDoublesCoverageTests
    {
        [Theory]
        [InlineData("ACC-", 5, "", 42, "ACC-00042")] // padded
        [InlineData("ID", 0, "!", 42, "ID42!")]       // unpadded (length 0)
        public void AutoNumber_sequential_pads_only_when_a_length_is_given(string prefix, int length, string suffix, int n, string expected)
        {
            Func<int, object> generator = AutoNumber.Sequential(prefix, length, suffix);
            Assert.Equal(expected, generator(n));
        }

        [Fact]
        public void SequentialIds_At_formats_the_index_and_rejects_negatives()
        {
            Assert.Equal(new Guid("00000000-0000-0000-0000-000000000007"), SequentialIds.At(7));
            Assert.Throws<ArgumentOutOfRangeException>(() => SequentialIds.At(-1));
        }

        [Fact]
        public void SequentialIds_Starting_yields_increasing_values_from_the_seed()
        {
            Func<Guid> seq = SequentialIds.Starting(10);
            Assert.Equal(SequentialIds.At(10), seq());
            Assert.Equal(SequentialIds.At(11), seq());
        }

        [Fact]
        public void WithManyToMany_requires_a_schema_name_and_an_intersect_entity()
        {
            Assert.Throws<ArgumentException>(() => new InMemoryOrganizationService()
                .WithManyToMany(string.Empty, "intersect", "a", "aid", "b", "bid"));
            Assert.Throws<ArgumentException>(() => new InMemoryOrganizationService()
                .WithManyToMany("schema", string.Empty, "a", "aid", "b", "bid"));
        }

        [Fact]
        public void Associate_over_an_undeclared_relationship_throws_with_guidance()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Assert.Throws<InvalidOperationException>(() => crm.Associate(
                "account", Guid.NewGuid(), new Relationship("not_declared"),
                new EntityReferenceCollection { new EntityReference("contact", Guid.NewGuid()) }));
        }

        [Fact]
        public void Associate_from_the_second_entity_side_maps_the_intersect_columns_positionally()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithManyToMany("account_contacts", "account_contact", "account", "accountid", "contact", "contactid");

            // Caller is the SECOND entity (contact) -> its lookup column is the second attribute.
            crm.Associate("contact", contactId, new Relationship("account_contacts"),
                new EntityReferenceCollection { new EntityReference("account", accountId) });

            Entity row = Assert.Single(crm.GetAll("account_contact"));
            Assert.Equal(contactId, row.GetAttributeValue<EntityReference>("contactid").Id);
            Assert.Equal(accountId, row.GetAttributeValue<EntityReference>("accountid").Id);
        }

        [Fact]
        public void Associate_over_a_self_referential_relationship_maps_columns_by_position()
        {
            Guid parentId = Guid.NewGuid();
            Guid childId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithManyToMany("account_parent", "account_hierarchy", "account", "parentid", "account", "childid");

            crm.Associate("account", parentId, new Relationship("account_parent"),
                new EntityReferenceCollection { new EntityReference("account", childId) });

            Entity row = Assert.Single(crm.GetAll("account_hierarchy"));
            Assert.Equal(parentId, row.GetAttributeValue<EntityReference>("parentid").Id);
            Assert.Equal(childId, row.GetAttributeValue<EntityReference>("childid").Id);
        }
    }
}
