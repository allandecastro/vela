using System;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // #303 Tier-1 harness fidelity: strict-security opt-in, 1:N Associate/Disassociate, and alternate-key lookup
    // resolution at write time.
    public sealed class HarnessTier1Tests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-000000000001");
        private static readonly Guid SalesBu = new Guid("b0000000-0000-0000-0000-0000000000b1");

        // ---- Strict security opt-in --------------------------------------------------------------------

        [Fact]
        public void Strict_security_without_a_configured_model_fails_loudly()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithStrictSecurity();

            // No WithSecurity(...) configured: instead of silently granting full access (the footgun), the operation
            // throws so an access-control test cannot pass green by forgetting to configure security.
            Assert.Throws<InvalidOperationException>(() => crm.Create(new Entity("account")));
            Assert.Throws<InvalidOperationException>(
                () => crm.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) }));
        }

        [Fact]
        public void Strict_security_with_a_configured_model_enforces_normally()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithStrictSecurity()
                .WithCurrentUser(Alice)
                .WithSecurity(s => s
                    .BusinessUnit(SalesBu)
                    .Role("author", r => r.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read))
                    .User(Alice, SalesBu, "author"));

            Guid id = crm.Create(new Entity("account"));

            Assert.NotEqual(Guid.Empty, id);
        }

        // ---- 1:N Associate / Disassociate --------------------------------------------------------------

        [Fact]
        public void Associate_and_disassociate_over_a_1N_relationship_set_and_clear_the_child_lookup()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid");
            Guid accountId = crm.Create(new Entity("account"));
            Guid contactId = crm.Create(new Entity("contact"));

            // WithRelationship names the 1:N schema "{referenced}_{referencing}".
            Relationship rel = new Relationship("account_contact");
            EntityReferenceCollection related = new EntityReferenceCollection { new EntityReference("contact", contactId) };

            crm.Associate("account", accountId, rel, related);
            Assert.Equal(accountId,
                crm.Retrieve("contact", contactId, new ColumnSet("parentcustomerid"))
                    .GetAttributeValue<EntityReference>("parentcustomerid")?.Id);

            crm.Disassociate("account", accountId, rel, related);
            Assert.Null(crm.Retrieve("contact", contactId, new ColumnSet("parentcustomerid"))
                .GetAttributeValue<EntityReference>("parentcustomerid"));
        }

        // ---- Alternate-key lookup resolution at write --------------------------------------------------

        [Fact]
        public void Create_resolves_a_lookup_supplied_as_an_alternate_key_reference()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");
            Guid accountId = crm.Create(new Entity("account") { ["accountnumber"] = "A-1" });

            Entity contact = new Entity("contact")
            {
                ["parentcustomerid"] = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A-1" } }),
            };
            Guid contactId = crm.Create(contact);

            EntityReference lookup = crm.Retrieve("contact", contactId, new ColumnSet("parentcustomerid"))
                .GetAttributeValue<EntityReference>("parentcustomerid");
            Assert.Equal(accountId, lookup?.Id);
        }

        [Fact]
        public void Update_resolves_a_lookup_supplied_as_an_alternate_key_reference()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");
            Guid accountId = crm.Create(new Entity("account") { ["accountnumber"] = "A-1" });
            Guid contactId = crm.Create(new Entity("contact"));

            crm.Update(new Entity("contact", contactId)
            {
                ["parentcustomerid"] = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A-1" } }),
            });

            EntityReference lookup = crm.Retrieve("contact", contactId, new ColumnSet("parentcustomerid"))
                .GetAttributeValue<EntityReference>("parentcustomerid");
            Assert.Equal(accountId, lookup?.Id);
        }

        [Fact]
        public void A_lookup_alternate_key_that_matches_nothing_faults()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService().WithAlternateKey("account", "accountnumber");

            Entity contact = new Entity("contact")
            {
                ["parentcustomerid"] = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "missing" } }),
            };

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => crm.Create(contact));
        }
    }
}
