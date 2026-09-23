using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Xunit;

namespace Vela.Testing.Tests
{
    // Covers PluginPipeline branches the main suite leaves red: registered steps on the Associate/Disassociate
    // messages (FireStage's sync/async split and Dispatch's EntityReference target + extra inputs), a pre-image
    // requested with no explicit attributes (full-column projection), and the comma-separated-string form of
    // the BypassBusinessLogicExecutionStepIds directive.
    public sealed class PluginPipelineCoverageTests
    {
        private const string Schema = "account_contacts";
        private const string Intersect = "account_contact";

        private static PluginPipeline WithRelationship(PluginPipeline pipeline)
            => pipeline.WithManyToMany(Schema, Intersect, "account", "accountid", "contact", "contactid");

        private static EntityReferenceCollection Related(Guid contactId)
            => new EntityReferenceCollection { new EntityReference("contact", contactId) };

        [Fact]
        public void Associate_step_fires_through_the_pipeline_and_persists_the_intersect()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = WithRelationship(PluginPipeline.Empty().Register<OnAccountAssociate>())
                .Seed(new Entity("account") { Id = accountId }, new Entity("contact") { Id = contactId });

            pipeline.Service.Associate("account", accountId, new Relationship(Schema), Related(contactId));

            Assert.Single(pipeline.GetAll(Intersect));
            Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains("OnAccountAssociate"));
        }

        [Fact]
        public void Disassociate_async_step_runs_after_the_commit()
        {
            Guid accountId = Guid.NewGuid();
            Guid contactId = Guid.NewGuid();
            PluginPipeline pipeline = WithRelationship(PluginPipeline.Empty().Register<OnAccountDisassociateAsync>())
                .Seed(new Entity("account") { Id = accountId }, new Entity("contact") { Id = contactId });

            Relationship rel = new Relationship(Schema);
            pipeline.Service.Associate("account", accountId, rel, Related(contactId)); // no associate step registered
            pipeline.Service.Disassociate("account", accountId, rel, Related(contactId));

            Entity note = Assert.Single(pipeline.GetAll("annotation"));
            Assert.Equal("disassociated", note.GetAttributeValue<string>("subject"));
            Assert.Empty(pipeline.GetAll(Intersect)); // the disassociate removed the intersect row
        }

        [Fact]
        public void PreImage_without_explicit_attributes_carries_every_column()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<CaptureFullPreImage>()
                .Seed(new FakeAccount { Id = id, Name = "Before" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "After" });

            FakeTask task = Assert.Single(pipeline.GetAll("task")).ToEntity<FakeTask>();
            Assert.Equal("Before", task.Subject); // the all-columns pre-image included 'name'
        }

        [Fact]
        public void Bypass_step_ids_accepts_a_comma_separated_string()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<KeyedNameChange>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            OrganizationRequest request = new UpdateRequest { Target = new FakeAccount { Id = id, Name = "New" } };
            request["BypassBusinessLogicExecutionStepIds"] = KeyedNameChange.StepKey + ", some-other-step";
            pipeline.Service.Execute(request);

            Assert.Empty(pipeline.ExecutionLog); // the named step was bypassed via the string form
        }
    }
}
