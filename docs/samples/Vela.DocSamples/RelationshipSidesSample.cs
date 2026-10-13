// The relationship-scope sample of the plugins guide (docs/guides/plugins.md). Edit the regions, not the page.
namespace Vela.DocSamples
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.DocSamples.Models;
    using Vela.Plugin;
    using Vela.Testing;
    using Xunit;

    #region plugin
    public sealed partial class ContactLinkAudit(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Contact>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnAssociate().PostOperation().Sync()
                .ForRelationship("contact_customer_accounts")
                .Handle(ctx =>
                {
                    // Target is the account or a contact, depending on the caller.
                    // Sides() splits the link by this plugin's table either way.
                    RelationshipSides sides = ctx.Sides();
                    foreach (EntityReference contact in sides.ThisSide)
                    {
                        ctx.Trace("contact {0} linked to {1} record(s)", contact.Id, sides.OtherSide.Count);
                    }
                });
    }
    #endregion

    public sealed class ContactLinkAuditTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Traces_The_Contact_Whichever_End_The_Caller_Passed(bool fromAccount)
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<ContactLinkAudit>()
                .WithManyToMany("contact_customer_accounts", "contactaccount", "account", "accountid", "contact", "contactid")
                .Seed(new Account { Id = accountId }, new Contact { Id = contactId });
            Relationship relationship = new Relationship("contact_customer_accounts");

            if (fromAccount)
            {
                pipeline.Service.Associate("account", accountId, relationship,
                    new EntityReferenceCollection { new EntityReference("contact", contactId) });
            }
            else
            {
                pipeline.Service.Associate("contact", contactId, relationship,
                    new EntityReferenceCollection { new EntityReference("account", accountId) });
            }

            Assert.Contains($"contact {contactId} linked to 1 record(s)", pipeline.Trace.Traces);
        }
    }
}
