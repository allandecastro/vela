using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Constants;
using Vela.Context;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class PluginContextBuilderTests
    {
        [Fact]
        public void Build_DrivesEntityPlugin_AndRoutesToHandler()
        {
            RecordingPlugin plugin = PluginContext.For<FakeAccount>()
                .OnUpdate()
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithTarget(new FakeAccount { Name = "Zava" })
                .Execute<RecordingPlugin>();

            Assert.Equal("Zava", plugin.LastName);
        }

        [Fact]
        public void Execute_CreatePostStep_SurfacesTheCreatedIdAsCtxResponse()
        {
            // The auto-derived Create/PostOperation trigger surfaces the Target id as OutputParameters["id"], as
            // Dataverse does, so a post-create handler's ctx.Response.id resolves without restating the output.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid accountId = Guid.NewGuid();

            PluginContext.For<FakeAccount>()
                .WithPrimaryEntity(FakeAccount.EntityLogicalNameValue, accountId)
                .WithTarget(new FakeAccount { Id = accountId, Name = "Zava" })
                .WithOrganizationService(crm)
                .Execute(new StampResponseIdOnCreate(string.Empty, string.Empty));

            EntityCollection tasks = crm.RetrieveMultiple(
                new QueryExpression("task") { ColumnSet = new ColumnSet(true) });
            Entity task = Assert.Single(tasks.Entities);
            Assert.Equal(accountId, task.GetAttributeValue<EntityReference>("regardingobjectid").Id);
        }

        [Fact]
        public void Builder_SurfacesConfiguredContextValues_WithNonEmptyIdentityIds()
        {
            LocalPluginContext ctx = new PluginContextBuilder()
                .OnUpdate()
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Asynchronous)
                .Depth(2)
                .Build();

            Assert.Equal(PluginMessages.Update, ctx.MessageName);
            Assert.Equal((int)PluginStages.PreOperation, ctx.Stage);
            Assert.Equal((int)ExecutionMode.Asynchronous, ctx.Mode);
            Assert.Equal(2, ctx.Depth);
            Assert.NotEqual(Guid.Empty, ctx.UserId);
            Assert.NotEqual(Guid.Empty, ctx.CorrelationId);
        }

        [Fact]
        public void For_DerivesPrimaryEntityName_AndTargetIsTyped()
        {
            LocalPluginContext ctx = PluginContext.For<FakeAccount>()
                .WithTarget(new FakeAccount { Name = "Typed" })
                .Build();

            Assert.Equal(FakeAccount.EntityLogicalNameValue, ctx.PrimaryEntityName);
            Assert.Equal("Typed", ctx.GetTarget<FakeAccount>().Name);
        }

        [Fact]
        public void TypedCustomApi_MapsInputToRequest_AndResponseToOutput()
        {
            LocalPluginContext ctx = PluginContext.ForMessage("vela_Score")
                .WithInputParameter("AccountId", 5)
                .Build();

            new ScoringApi().Execute(ctx.ServiceProvider);

            Assert.Equal(50, ctx.PluginExecutionContext.OutputParameters["Score"]);
        }

        [Fact]
        public void WithPreImage_PopulatesImageCollection()
        {
            LocalPluginContext ctx = PluginContext.For<FakeAccount>()
                .WithPreImage(new FakeAccount { Name = "Old" })
                .Build();

            Entity preImage = ctx.PluginExecutionContext.PreEntityImages[PluginImageAliases.PreImage];
            Assert.Equal("Old", preImage.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void WithManagedIdentityService_AcquireToken_ReturnsToken()
        {
            LocalPluginContext ctx = new PluginContextBuilder()
                .WithManagedIdentityService(new StubManagedIdentityService("token-123"))
                .Build();

            Assert.Equal("token-123", ctx.AcquireManagedIdentityToken(new[] { "scope" }));
        }

        [Fact]
        public void AcquireToken_ThrowsWhenManagedIdentityServiceAbsent()
        {
            LocalPluginContext ctx = new PluginContextBuilder().Build();

            Assert.Throws<InvalidPluginExecutionException>(
                () => ctx.AcquireManagedIdentityToken(new[] { "scope" }));
        }

        [Fact]
        public void WithOrganizationService_InjectsConsumerFake_AndHookIsObserved()
        {
            List<Entity> created = new List<Entity>();
            HookOrganizationService service = new HookOrganizationService
            {
                OnCreate = entity =>
                {
                    created.Add(entity);
                    return Guid.NewGuid();
                }
            };

            new PluginContextBuilder()
                .WithOrganizationService(service)
                .Execute(new CreatingPlugin());

            Assert.Single(created);
            Assert.Equal("account", created[0].LogicalName);
        }

        [Fact]
        public void TraceCapture_RecordsTraceMessages()
        {
            PluginContextBuilder builder = new PluginContextBuilder();
            LocalPluginContext ctx = builder.Build();

            ctx.Trace("hello {0}", "world");

            Assert.Contains("hello world", builder.TraceCapture.Traces);
        }

        [Fact]
        public void Depth_ExceedingMaxDepth_ThrowsDepthGuard()
        {
            Assert.Throws<InvalidPluginExecutionException>(
                () => new PluginContextBuilder().Depth(3).Execute(new DepthGuardedPlugin()));
        }

        [Fact]
        public void WithTargets_MarksBulkOperation_AndExposesTargets()
        {
            LocalPluginContext ctx = PluginContext.For<FakeAccount>()
                .WithTargets(new FakeAccount { Name = "A" }, new FakeAccount { Name = "B" })
                .Build();

            Assert.True(ctx.IsBulkOperation);
            Assert.NotNull(ctx.BulkTargets);
            Assert.Equal(2, ctx.BulkTargets!.Entities.Count);
        }

        [Fact]
        public void Execute_AutoDerivesTrigger_FromPluginRegistration()
        {
            // No OnCreate()/Stage()/Mode() specified — the trigger is derived from the plugin's single step.
            HandlerRecordingPlugin plugin = PluginContext.For<FakeAccount>()
                .WithTarget(new FakeAccount { Name = "Z" })
                .Execute<HandlerRecordingPlugin>();

            Assert.Equal("Z", plugin.Handled);
        }

        [Fact]
        public void WithParentContext_SetsParentContext()
        {
            IPluginExecutionContext parent = new FakePluginExecutionContext();

            LocalPluginContext ctx = new PluginContextBuilder().WithParentContext(parent).Build();

            Assert.Same(parent, ctx.PluginExecutionContext.ParentContext);
        }
    }
}
