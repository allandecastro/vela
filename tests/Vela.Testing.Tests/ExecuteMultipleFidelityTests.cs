using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xunit;

namespace Vela.Testing.Tests
{
    // Fidelity: ExecuteMultiple runs each child in order, is NOT transactional, honours ContinueOnError, and
    // reports each fault with its real ErrorCode and the child's RequestIndex — via both the store service and
    // the pipeline service (which must carry the same fault fidelity as the store).
    public sealed class ExecuteMultipleFidelityTests
    {
        // 0x80040217 ObjectDoesNotExist — the platform fault for operating on a record that does not exist.
        private const int ObjectDoesNotExist = unchecked((int)0x80040217);

        private static Entity NewAccount(string name) => new Entity("account") { ["name"] = name };

        private static ExecuteMultipleRequest Batch(bool continueOnError, params OrganizationRequest[] requests)
        {
            ExecuteMultipleRequest batch = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = continueOnError, ReturnResponses = true },
                Requests = new OrganizationRequestCollection(),
            };
            foreach (OrganizationRequest request in requests)
            {
                batch.Requests.Add(request);
            }

            return batch;
        }

        private static UpdateRequest UpdateMissing() =>
            new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "ghost" } };

        // --- ContinueOnError = false: stop at the first fault --------------------------------------------

        [Fact]
        public void Store_ContinueOnError_false_stops_at_the_first_fault()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)svc.Execute(Batch(
                continueOnError: false,
                new CreateRequest { Target = NewAccount("A") }, // index 0 succeeds
                UpdateMissing(),                                 // index 1 faults -> stop
                new CreateRequest { Target = NewAccount("C") })); // index 2 never runs

            Assert.True(response.IsFaulted);
            // The success at 0 and the fault at 1 are reported; the third child did not run.
            Assert.Equal(2, response.Responses.Count);
            Assert.Equal(0, response.Responses[0].RequestIndex);
            Assert.NotNull(response.Responses[0].Response);
            Assert.Equal(1, response.Responses[1].RequestIndex);
            Assert.Equal(ObjectDoesNotExist, response.Responses[1].Fault.ErrorCode);

            // ExecuteMultiple is not transactional: the child that succeeded before the fault stays committed.
            Assert.Single(svc.GetAll("account"));
        }

        // --- ContinueOnError = true: run every child, collect every fault, preserve order ----------------

        [Fact]
        public void Store_ContinueOnError_true_runs_all_children_and_preserves_order_and_indexes()
        {
            InMemoryOrganizationService svc = new InMemoryOrganizationService();

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)svc.Execute(Batch(
                continueOnError: true,
                UpdateMissing(),                                 // index 0 faults
                new CreateRequest { Target = NewAccount("B") },  // index 1 succeeds
                new DeleteRequest { Target = new EntityReference("account", Guid.NewGuid()) })); // index 2 faults

            Assert.True(response.IsFaulted);
            Assert.Equal(3, response.Responses.Count);
            // Ordering preserved: items appear in child order with matching RequestIndex.
            Assert.Equal(new[] { 0, 1, 2 }, response.Responses.Select(i => i.RequestIndex).ToArray());
            Assert.Equal(ObjectDoesNotExist, response.Responses[0].Fault.ErrorCode);
            Assert.NotNull(response.Responses[1].Response);
            Assert.Null(response.Responses[1].Fault);
            Assert.Equal(ObjectDoesNotExist, response.Responses[2].Fault.ErrorCode);

            Assert.Single(svc.GetAll("account")); // only the successful create landed
        }

        // --- The pipeline service carries the same per-item fault fidelity (ErrorCode + RequestIndex) -----

        [Fact]
        public void Pipeline_ExecuteMultiple_carries_the_real_error_code_and_request_index()
        {
            PluginPipeline pipeline = PluginPipeline.Empty();

            ExecuteMultipleResponse response = (ExecuteMultipleResponse)pipeline.Service.Execute(Batch(
                continueOnError: true,
                new CreateRequest { Target = NewAccount("A") }, // index 0 succeeds
                UpdateMissing()));                               // index 1 faults

            Assert.True(response.IsFaulted);
            Assert.Equal(2, response.Responses.Count);
            Assert.Equal(0, response.Responses[0].RequestIndex);
            Assert.NotNull(response.Responses[0].Response);
            Assert.Equal(1, response.Responses[1].RequestIndex);
            // The fault's ErrorCode survives the pipeline path, exactly as through the store service.
            Assert.Equal(ObjectDoesNotExist, response.Responses[1].Fault.ErrorCode);
        }
    }
}
