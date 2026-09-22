using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Query;
using Vela.Context;
using Xunit;

namespace Vela.Testing.Tests
{
    // Mirrors the generated RelationshipSchemaNameAttribute (consumer-namespace type matched by name).
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class RelationshipSchemaNameAttribute : Attribute
    {
        public RelationshipSchemaNameAttribute(string schemaName) => SchemaName = schemaName ?? string.Empty;

        public string SchemaName { get; }
    }

    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class DeepAccount : Entity
    {
        public const string EntityLogicalNameValue = "account";

        public DeepAccount()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }

        [RelationshipSchemaName("contact_customer_accounts")]
        public IList<DeepContact> Contacts { get; set; } = new List<DeepContact>();
    }

    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class DeepContact : Entity
    {
        public const string EntityLogicalNameValue = "contact";

        public DeepContact()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("firstname")]
        public string FirstName
        {
            get => GetAttributeValue<string>("firstname");
            set => SetAttributeValue("firstname", value);
        }

        [AttributeLogicalName("parentcustomerid")]
        public EntityReference ParentCustomerId
        {
            get => GetAttributeValue<EntityReference>("parentcustomerid");
            set => SetAttributeValue("parentcustomerid", value);
        }
    }

    public sealed class RelationshipDeepInsertTests
    {
        private static InMemoryOrganizationService NewService() =>
            new InMemoryOrganizationService()
                .WithParentalRelationship("account", "contact", "parentcustomerid");

        [Fact]
        public void CreateWithRelated_creates_the_children_and_links_them_to_the_parent()
        {
            InMemoryOrganizationService service = NewService();
            DeepAccount account = new DeepAccount { Name = "Contoso" };
            account.Contacts.Add(new DeepContact { FirstName = "Ada" });
            account.Contacts.Add(new DeepContact { FirstName = "Grace" });

            Guid accountId = service.CreateWithRelated(account);

            List<Entity> contacts = service.RetrieveMultiple(new QueryExpression("contact") { ColumnSet = new ColumnSet(true) })
                .Entities.ToList();
            Assert.Equal(2, contacts.Count);
            Assert.All(contacts, c => Assert.Equal(accountId, c.GetAttributeValue<EntityReference>("parentcustomerid")?.Id));
            Assert.Contains(contacts, c => c.GetAttributeValue<string>("firstname") == "Ada");
        }

        [Fact]
        public void CreateWithRelated_with_no_related_records_behaves_like_a_plain_create()
        {
            InMemoryOrganizationService service = NewService();
            Guid id = service.CreateWithRelated(new DeepAccount { Name = "Empty" });

            Assert.NotEqual(Guid.Empty, id);
            Assert.Empty(service.RetrieveMultiple(new QueryExpression("contact") { ColumnSet = new ColumnSet(true) }).Entities);
        }

        [Fact]
        public void CreateWithRelated_throws_a_clear_error_when_the_relationship_is_not_declared()
        {
            // No WithRelationship declared -> the harness cannot know the child foreign-key column.
            InMemoryOrganizationService service = new InMemoryOrganizationService();
            DeepAccount account = new DeepAccount { Name = "Contoso" };
            account.Contacts.Add(new DeepContact { FirstName = "Ada" });

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => service.CreateWithRelated(account));
            Assert.Contains("relationship", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
