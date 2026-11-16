using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // #303 Tier-2: the Merge message (account/contact/lead/incident). Applies UpdateContent (the subordinate's own
    // columns are not copied, verified live), reparents the subordinate's children, then
    // deactivates the subordinate - per the Merge contract.
    public sealed class HarnessMergeTests
    {
        [Fact]
        public void Merge_applies_update_content_only_and_deactivates_the_subordinate()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid targetId = crm.Create(new Entity("account") { ["name"] = "Master" });
            Guid subId = crm.Create(new Entity("account") { ["name"] = "Dup", ["telephone1"] = "555-1234" });

            crm.Execute(new MergeRequest
            {
                Target = new EntityReference("account", targetId),
                SubordinateId = subId,
                UpdateContent = new Entity("account") { ["accountnumber"] = "A-1" },
                PerformParentingChecks = false,
            });

            Entity target = crm.Retrieve("account", targetId, new ColumnSet(true));
            Assert.Equal("Master", target.GetAttributeValue<string>("name"));       // target's own value is not overwritten
            Assert.False(target.Contains("telephone1"));                             // verified live: not copied from the subordinate
            Assert.Equal("A-1", target.GetAttributeValue<string>("accountnumber")); // UpdateContent applied to the target
            Assert.NotEqual(1, target.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0); // target stays active

            Entity sub = crm.Retrieve("account", subId, new ColumnSet("statecode", "statuscode", "masterid", "telephone1"));
            Assert.Equal(1, sub.GetAttributeValue<OptionSetValue>("statecode")?.Value); // subordinate deactivated
            Assert.Equal(2, sub.GetAttributeValue<OptionSetValue>("statuscode")?.Value);
            Assert.Equal(targetId, sub.GetAttributeValue<EntityReference>("masterid").Id);
            Assert.Equal("555-1234", sub.GetAttributeValue<string>("telephone1"));      // the subordinate keeps its data
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Merge_without_update_content_leaves_the_master_unchanged(bool emptyContent)
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid targetId = crm.Create(new Entity("account") { ["name"] = "Master" });
            Guid subId = crm.Create(new Entity("account") { ["name"] = "Dup", ["telephone1"] = "555-1234", ["websiteurl"] = "https://dup" });
            Entity before = crm.Retrieve("account", targetId, new ColumnSet(true));

            crm.Execute(new MergeRequest
            {
                Target = new EntityReference("account", targetId),
                SubordinateId = subId,
                UpdateContent = emptyContent ? new Entity("account") : null,
                PerformParentingChecks = false,
            });

            Entity master = crm.Retrieve("account", targetId, new ColumnSet(true));
            Assert.Equal(before.Attributes.Keys.OrderBy(k => k), master.Attributes.Keys.OrderBy(k => k));
            Assert.Equal(before.GetAttributeValue<long>("versionnumber"), master.GetAttributeValue<long>("versionnumber"));
        }

        [Fact]
        public void Merging_an_already_merged_subordinate_faults_and_changes_nothing()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid targetId = crm.Create(new Entity("account") { ["name"] = "Master" });
            Guid otherId = crm.Create(new Entity("account") { ["name"] = "Other" });
            Guid subId = crm.Create(new Entity("account") { ["name"] = "Dup" });
            MergeRequest Into(Guid master) => new MergeRequest
            {
                Target = new EntityReference("account", master),
                SubordinateId = subId,
                PerformParentingChecks = false,
            };
            crm.Execute(Into(targetId));

            FaultException<OrganizationServiceFault> fault = Assert.Throws<FaultException<OrganizationServiceFault>>(() => crm.Execute(Into(otherId)));

            Assert.Equal(unchecked((int)0x80045304), fault.Detail.ErrorCode);
            Assert.Equal(targetId, crm.Retrieve("account", subId, new ColumnSet("masterid")).GetAttributeValue<EntityReference>("masterid").Id);
        }

        [Fact]
        public void Merge_reparents_the_subordinates_children_to_the_target()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithReferentialRelationship("account", "contact", "parentcustomerid");
            Guid targetId = crm.Create(new Entity("account") { ["name"] = "Master" });
            Guid subId = crm.Create(new Entity("account") { ["name"] = "Dup" });
            Guid childId = crm.Create(new Entity("contact") { ["parentcustomerid"] = new EntityReference("account", subId) });

            crm.Execute(new MergeRequest
            {
                Target = new EntityReference("account", targetId),
                SubordinateId = subId,
                PerformParentingChecks = false,
            });

            Entity child = crm.Retrieve("contact", childId, new ColumnSet("parentcustomerid"));
            Assert.Equal(targetId, child.GetAttributeValue<EntityReference>("parentcustomerid")?.Id);
        }

        [Fact]
        public void Merge_ignores_UpdateContent_for_incident()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid targetId = crm.Create(new Entity("incident") { ["title"] = "Master case" });
            Guid subId = crm.Create(new Entity("incident") { ["title"] = "Dup case" });

            crm.Execute(new MergeRequest
            {
                Target = new EntityReference("incident", targetId),
                SubordinateId = subId,
                UpdateContent = new Entity("incident") { ["description"] = "should be ignored" },
                PerformParentingChecks = false,
            });

            Entity target = crm.Retrieve("incident", targetId, new ColumnSet(true));
            Assert.Null(target.GetAttributeValue<string>("description")); // UpdateContent is ignored when merging incidents
            Assert.Equal(1, crm.Retrieve("incident", subId, new ColumnSet("statecode")).GetAttributeValue<OptionSetValue>("statecode")?.Value);
        }

        [Fact]
        public void Merge_faults_for_an_unsupported_entity_type()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid a = crm.Create(new Entity("vela_widget"));
            Guid b = crm.Create(new Entity("vela_widget"));

            Assert.Throws<InvalidPluginExecutionException>(() => crm.Execute(new MergeRequest
            {
                Target = new EntityReference("vela_widget", a),
                SubordinateId = b,
                PerformParentingChecks = false,
            }));
        }
    }
}
