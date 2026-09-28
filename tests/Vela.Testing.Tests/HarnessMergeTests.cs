using System;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // #303 Tier-2: the Merge message (account/contact/lead/incident). Moves the subordinate's non-conflicting data
    // onto the target without overwriting it, applies UpdateContent, reparents the subordinate's children, then
    // deactivates the subordinate - per the Merge contract.
    public sealed class HarnessMergeTests
    {
        [Fact]
        public void Merge_fills_the_target_without_overwriting_applies_update_content_and_deactivates_subordinate()
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
            Assert.Equal("Master", target.GetAttributeValue<string>("name"));         // target's own value is not overwritten
            Assert.Equal("555-1234", target.GetAttributeValue<string>("telephone1")); // filled from the subordinate
            Assert.Equal("A-1", target.GetAttributeValue<string>("accountnumber"));   // UpdateContent applied to the target
            Assert.NotEqual(1, target.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0); // target stays active

            Entity sub = crm.Retrieve("account", subId, new ColumnSet("statecode"));
            Assert.Equal(1, sub.GetAttributeValue<OptionSetValue>("statecode")?.Value); // subordinate deactivated
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
