using System;
using Vela.Context;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Crm.Sdk.Messages;
using Xunit;

namespace Vela.Tests
{
    // Covers the typed request/response accessors (PluginContextExtensions.Requests): the SDK request/response
    // classes are thin views over a ParameterCollection, so hydrating them over the plugin's
    // InputParameters/OutputParameters gives typed access to every message parameter at no copy cost.
    public sealed class TypedRequestResponseTests
    {
        private static LocalPluginContext Ctx() => new PluginContextBuilder().Build();

        [Fact]
        public void GetRequest_ReadsTypedMessageParameters()
        {
            LocalPluginContext ctx = Ctx();
            Guid subordinate = Guid.NewGuid();
            ctx.PluginExecutionContext.InputParameters["Target"] = new EntityReference("account", Guid.NewGuid());
            ctx.PluginExecutionContext.InputParameters["SubordinateId"] = subordinate;
            ctx.PluginExecutionContext.InputParameters["PerformParentingChecks"] = true;

            MergeRequest req = ctx.GetRequest<MergeRequest>();

            Assert.Equal(subordinate, req.SubordinateId);
            Assert.Equal("account", req.Target.LogicalName);
            Assert.True(req.PerformParentingChecks);
        }

        [Fact]
        public void GetRequest_IsALiveViewOverInputParameters()
        {
            LocalPluginContext ctx = Ctx();
            ctx.PluginExecutionContext.InputParameters["ColumnSet"] = new Microsoft.Xrm.Sdk.Query.ColumnSet("name");

            RetrieveRequest req = ctx.GetRequest<RetrieveRequest>();

            Assert.Contains("name", req.ColumnSet.Columns);
        }

        [Fact]
        public void GetResponse_ReadsTypedOutputParameters()
        {
            LocalPluginContext ctx = Ctx();
            Guid id = Guid.NewGuid();
            ctx.PluginExecutionContext.OutputParameters["id"] = id;

            CreateResponse resp = ctx.GetResponse<CreateResponse>();

            Assert.Equal(id, resp.id);
        }

        [Fact]
        public void GetRequest_TypesTheTargetEntityViaToEntity()
        {
            LocalPluginContext ctx = Ctx();
            Entity target = new Entity(TestAccount.EntityLogicalNameValue) { Id = Guid.NewGuid() };
            target["name"] = "Zava";
            ctx.PluginExecutionContext.InputParameters["Target"] = target;

            // The SDK request exposes Target as Entity; ToEntity<T>() types it (the v1 pattern, no wrappers).
            TestAccount typed = ctx.GetRequest<UpdateRequest>().Target.ToEntity<TestAccount>();

            Assert.Equal("Zava", typed.Name);
        }

        [Fact]
        public void GetRequest_NullContext_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((ILocalPluginContext)null!).GetRequest<CreateRequest>());
        }
    }
}
