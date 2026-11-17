using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Xunit;

namespace Vela.Testing.Tests
{
    [Vela.Constants.CustomMessage("vela_StagedGreet")]
    public sealed class StagedGreetMessage { }

    [Vela.Constants.CustomMessage("vela_ClosedGreet")]
    public sealed class ClosedGreetMessage { }

    [Vela.Constants.CustomMessage("vela_AsyncOnly")]
    public sealed class AsyncOnlyMessage { }

    // A-60: a Custom API runs the whole pipeline in the harness. Steps registered on its message fire around the
    // main operation as far as AllowedCustomProcessingStepType allows, a bound API's context carries the bound table
    // and the Target id, and inputs are checked against their declared types.
    public sealed class CustomApiPipelineStageTests
    {
        private const int InvalidArgument = unchecked((int)0x80040315);
        private const int MissingRequiredField = unchecked((int)0x80040203);

        public CustomApiPipelineStageTests()
        {
            ApiProbe.Events.Clear();
        }

        private static OrganizationRequest Greet(object name)
            => new OrganizationRequest("vela_StagedGreet") { ["Name"] = name };

        [Fact]
        public void Steps_on_the_message_fire_around_the_main_operation_and_can_change_inputs_and_outputs()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<StagedGreetApi>()
                .Register<StagedGreetSteps>();

            OrganizationResponse response = pipeline.Service.Execute(Greet("ada"));

            Assert.Equal(new[] { "prevalidation", "preoperation", "main:ADA", "postoperation:Hello ADA" }, ApiProbe.Events);
            // The Post step changed the output after the main operation set it.
            Assert.Equal("Hello ADA!", response["Greeting"]);
        }

        [Fact]
        public void A_step_on_an_api_that_allows_no_custom_processing_step_fails_the_call()
        {
            PluginPipeline pipeline = PluginPipeline.Empty()
                .Register<ClosedGreetApi>()
                .Register<ClosedGreetStep>();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => pipeline.Service.Execute(new OrganizationRequest("vela_ClosedGreet") { ["Name"] = "x" }));

            Assert.Contains("AllowedCustomProcessingStepType", error.Message);
            Assert.Empty(ApiProbe.Events);
        }

        [Fact]
        public void An_async_only_api_rejects_a_sync_step_and_runs_an_async_one_after_the_call()
        {
            PluginPipeline syncStep = PluginPipeline.Empty().Register<AsyncOnlyApi>().Register<AsyncOnlySyncStep>();
            Assert.Throws<InvalidOperationException>(() => syncStep.Service.Execute(new OrganizationRequest("vela_AsyncOnly")));

            PluginPipeline asyncStep = PluginPipeline.Empty().Register<AsyncOnlyApi>().Register<AsyncOnlyAsyncStep>();
            asyncStep.Service.Execute(new OrganizationRequest("vela_AsyncOnly"));

            Assert.Equal(new[] { "main", "async-post" }, ApiProbe.Events);
        }

        [Fact]
        public void A_bound_api_context_carries_the_bound_table_and_the_target_id()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<BoundProbeApi>();

            OrganizationResponse response = pipeline.Service.Execute(
                new OrganizationRequest("vela_BoundProbe") { ["Target"] = new EntityReference("account", id) });

            Assert.Equal("account", response["Entity"]);
            Assert.Equal(id, response["Id"]);
        }

        [Fact]
        public void A_bound_api_rejects_a_missing_target_or_a_target_of_another_table()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<BoundProbeApi>();

            FaultException<OrganizationServiceFault> missing = Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => pipeline.Service.Execute(new OrganizationRequest("vela_BoundProbe")));
            InvalidPluginExecutionException wrongTable = Assert.Throws<InvalidPluginExecutionException>(
                () => pipeline.Service.Execute(new OrganizationRequest("vela_BoundProbe") { ["Target"] = new EntityReference("contact", Guid.NewGuid()) }));

            Assert.Equal(MissingRequiredField, missing.Detail.ErrorCode);
            Assert.Contains("Target", missing.Detail.Message);
            Assert.Contains("contact", wrongTable.Message);
        }

        [Fact]
        public void An_input_of_the_wrong_type_is_rejected_before_anything_runs()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<StagedGreetApi>().Register<StagedGreetSteps>();

            FaultException<OrganizationServiceFault> error = Assert.Throws<FaultException<OrganizationServiceFault>>(() => pipeline.Service.Execute(Greet(42)));

            // The platform's fault, verified live (raised before any step, PreValidation included).
            Assert.Equal(InvalidArgument, error.Detail.ErrorCode);
            Assert.Equal("Input field type 'Int32' does not match expected type 'String' for field 'Name'", error.Detail.Message);
            Assert.Empty(ApiProbe.Events);
        }

        [Fact]
        public void An_undeclared_input_is_rejected_before_anything_runs()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<StagedGreetApi>().Register<StagedGreetSteps>();
            OrganizationRequest request = Greet("ada");
            request["Extra"] = 1;
            request["tag"] = "allowed";

            FaultException<OrganizationServiceFault> error = Assert.Throws<FaultException<OrganizationServiceFault>>(() => pipeline.Service.Execute(request));

            Assert.Equal(InvalidArgument, error.Detail.ErrorCode);
            Assert.Equal("Unrecognized request parameter: Extra", error.Detail.Message);
            Assert.Empty(ApiProbe.Events);
        }

        [Fact]
        public void The_main_operation_of_a_global_api_runs_at_stage_30_on_table_none_under_its_implementation_step()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<GlobalProbeApi>();

            OrganizationResponse response = pipeline.Service.Execute(new OrganizationRequest("vela_GlobalProbe"));

            Assert.Equal("none", response["Entity"]);
            Assert.Equal(30, response["Stage"]);
            Assert.Equal("CustomApi 'vela_GlobalProbe' implementation", response["Owner"]);
        }
    }

    public sealed class GlobalProbeApi : CustomApiBase
    {
        public GlobalProbeApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi() => RegisterCustomApi("vela_GlobalProbe", nameof(Handle)).AsAction();

        private void Handle(ILocalPluginContext ctx)
        {
            ctx.SetOutputParameter("Entity", ctx.PluginExecutionContext.PrimaryEntityName);
            ctx.SetOutputParameter("Stage", ctx.PluginExecutionContext.Stage);
            ctx.SetOutputParameter("Owner", ctx.PluginExecutionContext.OwningExtension?.Name);
        }
    }

    internal static class ApiProbe
    {
        public static List<string> Events { get; } = new List<string>();
    }

    public sealed class StagedGreetApi : CustomApiBase
    {
        public StagedGreetApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_StagedGreet", nameof(Handle)).AsAction()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .RequestParameter<string>("Name").ResponseParameter<string>("Greeting");

        private void Handle(ILocalPluginContext ctx)
        {
            string name = ctx.GetInputParameter<string>("Name")!;
            ApiProbe.Events.Add("main:" + name);
            ctx.SetOutputParameter("Greeting", "Hello " + name);
        }
    }

    public sealed class StagedGreetSteps : EntityPluginBase<FakeAccount>
    {
        public StagedGreetSteps(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnMessage<StagedGreetMessage>().PreValidation().Sync().Handle(ctx => ApiProbe.Events.Add("prevalidation"));
            RegisterStep().OnMessage<StagedGreetMessage>().PreOperation().Sync().Handle(ctx =>
            {
                ApiProbe.Events.Add("preoperation");
                ParameterCollection inputs = ctx.PluginExecutionContext.InputParameters;
                inputs["Name"] = ((string)inputs["Name"]).ToUpperInvariant();
            });
            RegisterStep().OnMessage<StagedGreetMessage>().PostOperation().Sync().Handle(ctx =>
            {
                ParameterCollection outputs = ctx.PluginExecutionContext.OutputParameters;
                ApiProbe.Events.Add("postoperation:" + outputs["Greeting"]);
                outputs["Greeting"] = outputs["Greeting"] + "!";
            });
        }
    }

    public sealed class ClosedGreetApi : CustomApiBase
    {
        public ClosedGreetApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_ClosedGreet", nameof(Handle)).AsAction().RequestParameter<string>("Name");

        private void Handle(ILocalPluginContext ctx) => ApiProbe.Events.Add("main");
    }

    public sealed class ClosedGreetStep : EntityPluginBase<FakeAccount>
    {
        public ClosedGreetStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<ClosedGreetMessage>().PostOperation().Sync().Handle(ctx => ApiProbe.Events.Add("post"));
    }

    public sealed class AsyncOnlyApi : CustomApiBase
    {
        public AsyncOnlyApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_AsyncOnly", nameof(Handle)).AsAction()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.AsyncOnly);

        private void Handle(ILocalPluginContext ctx) => ApiProbe.Events.Add("main");
    }

    public sealed class AsyncOnlySyncStep : EntityPluginBase<FakeAccount>
    {
        public AsyncOnlySyncStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<AsyncOnlyMessage>().PreOperation().Sync().Handle(ctx => ApiProbe.Events.Add("sync-pre"));
    }

    public sealed class AsyncOnlyAsyncStep : EntityPluginBase<FakeAccount>
    {
        public AsyncOnlyAsyncStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<AsyncOnlyMessage>().PostOperation().Async().Handle(ctx => ApiProbe.Events.Add("async-post"));
    }

    public sealed class BoundProbeApi : CustomApiBase
    {
        public BoundProbeApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi()
            => RegisterCustomApi("vela_BoundProbe", nameof(Handle)).AsAction().BoundTo("account");

        private void Handle(ILocalPluginContext ctx)
        {
            ctx.SetOutputParameter("Entity", ctx.PluginExecutionContext.PrimaryEntityName);
            ctx.SetOutputParameter("Id", ctx.PluginExecutionContext.PrimaryEntityId);
        }
    }
}
