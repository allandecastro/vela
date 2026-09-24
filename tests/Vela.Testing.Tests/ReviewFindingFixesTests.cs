using System;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace Vela.Testing.Tests
{
    // Regression coverage for three harness code-review findings:
    //   - transaction rollback must restore the sharing table, not only the record store;
    //   - an ordered filter with a null operand must exclude the row instead of throwing;
    //   - an ExecuteMultiple child fault routed through the pipeline must carry the platform ErrorCode.
    public sealed class ReviewFindingFixesTests
    {
        private static readonly Guid Alice = new Guid("a11ce000-0000-0000-0000-0000000000f1");

        // 0x80040217 ObjectDoesNotExist — the fault code Dataverse returns for a missing record.
        private const int ObjectDoesNotExist = unchecked((int)0x80040217);

        [Fact]
        public void Transaction_rollback_restores_a_share_granted_by_an_earlier_child()
        {
            Guid record = Guid.NewGuid();
            InMemoryOrganizationService svc = new InMemoryOrganizationService()
                .Seed(new Entity("account", record) { ["name"] = "Acme" });

            ExecuteTransactionRequest tx = new ExecuteTransactionRequest
            {
                Requests = new OrganizationRequestCollection
                {
                    new GrantAccessRequest
                    {
                        Target = new EntityReference("account", record),
                        PrincipalAccess = new PrincipalAccess
                        {
                            Principal = new EntityReference("systemuser", Alice),
                            AccessMask = AccessRights.ReadAccess,
                        },
                    },
                    // A write to a record that does not exist faults, so the whole batch rolls back.
                    new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "ghost" } },
                },
            };

            Assert.Throws<FaultException<OrganizationServiceFault>>(() => svc.Execute(tx));

            RetrieveSharedPrincipalsAndAccessResponse shares =
                (RetrieveSharedPrincipalsAndAccessResponse)svc.Execute(
                    new RetrieveSharedPrincipalsAndAccessRequest { Target = new EntityReference("account", record) });

            // The share created by the first child must be rolled back alongside the record store; a snapshot that
            // captured only _store would leave this lingering grant behind.
            Assert.Empty(shares.PrincipalAccesses);
        }

        [Fact]
        public void Ordered_filter_with_a_null_operand_excludes_the_row_instead_of_throwing()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService().Seed(
                new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Acme", ["revenue"] = new Money(100m) });

            QueryExpression query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
            query.Criteria.AddCondition(new ConditionExpression("revenue", ConditionOperator.GreaterThan, (object)null!));

            // A comparison against a null operand is "unknown", so the row is excluded rather than throwing inside
            // EntityValues.Compare(actual, null).
            EntityCollection result = svc.RetrieveMultiple(query);

            Assert.Empty(result.Entities);
        }

        [Fact]
        public void ExecuteMultiple_through_the_pipeline_carries_the_child_fault_error_code()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            ExecuteMultipleRequest request = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
                Requests = new OrganizationRequestCollection
                {
                    // Missing record -> FaultException<OrganizationServiceFault> carrying ObjectDoesNotExist.
                    new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "ghost" } },
                },
            };

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)pipeline.Service.Execute(request);

            ExecuteMultipleResponseItem item = Assert.Single(response.Responses);
            Assert.NotNull(item.Fault);
            // The ErrorCode must survive, mirroring the store's ExecuteMultipleItemFault; dropping it left this at 0.
            Assert.Equal(ObjectDoesNotExist, item.Fault.ErrorCode);
        }
    }
}
