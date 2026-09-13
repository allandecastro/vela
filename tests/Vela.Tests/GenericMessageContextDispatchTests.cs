using System;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    // Proves Phase 1: a permissive OnMessage(...) step bound with the generic Handler<TRequest>(...) overload gets a
    // strongly-typed ctx.Request (and, at post-stage, ctx.Response) without a hand-authored per-message context. The
    // request type is any OrganizationRequest-derived type with a parameterless ctor; dispatch resolves it from the
    // handler method's parameter (IMessagePreContext<TRequest> / IMessagePostContext<TRequest>) and builds the
    // matching generic context over the live InputParameters / OutputParameters.
    public sealed class GenericMessageContextDispatchTests
    {
        private const string CustomMessage = "vela_DoThing";

        // A stand-in for a Vela-generated Custom API request (or any SDK request): parameterless ctor, a typed
        // property over Parameters, and its own RequestName.
        private sealed class DoThingRequest : OrganizationRequest
        {
            public DoThingRequest()
            {
                RequestName = CustomMessage;
            }

            public string? Payload
            {
                get => Parameters.TryGetValue(nameof(Payload), out object? value) ? (string?)value : null;
                set => Parameters[nameof(Payload)] = value!;
            }
        }

        private sealed class PostProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedPayload;
            public bool ResponseAvailable;

            public PostProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnMessage(CustomMessage).PostOperation().Sync().Handler<DoThingRequest>(OnDoThing);

            private void OnDoThing(IMessagePostContext<DoThingRequest> ctx)
            {
                CapturedPayload = ctx.Request.Payload;   // typed, inferred
                ResponseAvailable = ctx.Response != null; // base OrganizationResponse at post-stage
            }
        }

        private sealed class PreProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedPayload;

            public PreProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnMessage(CustomMessage).PreOperation().Sync().Handler<DoThingRequest>(OnDoThing);

            private void OnDoThing(IMessagePreContext<DoThingRequest> ctx)
                => CapturedPayload = ctx.Request.Payload;   // typed, inferred, Request-side data in pre
        }

        // Entity-typed variants: the two-arg Handler<TRequest, TEntity>(...) overload adds early-bound typed
        // images (ctx.PreImage / ctx.PostImage) on top of the typed ctx.Request. The primary record is read via
        // the ctx.GetTarget<TEntity>() extension.
        private sealed class TypedTargetPostProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedPayload;
            public string? CapturedTargetName;
            public string? CapturedPostImageName;
            public bool ResponseAvailable;

            public TypedTargetPostProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnMessage(CustomMessage).PostOperation().Sync().Handler<DoThingRequest>(OnDoThing);

            private void OnDoThing(IMessagePostContext<DoThingRequest, TestAccount> ctx)
            {
                CapturedPayload = ctx.Request.Payload;      // typed, inferred
                CapturedTargetName = ctx.GetTarget<TestAccount>().Name; // early-bound TestAccount over the live Target
                CapturedPostImageName = ctx.PostImage?.Name; // typed image, no <TestAccount> in the body
                ResponseAvailable = ctx.Response != null;   // base OrganizationResponse at post-stage
            }
        }

        private sealed class TypedTargetPreProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedPayload;
            public string? CapturedTargetName;

            public TypedTargetPreProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnMessage(CustomMessage).PreOperation().Sync().Handler<DoThingRequest>(OnDoThing);

            private void OnDoThing(IMessagePreContext<DoThingRequest, TestAccount> ctx)
            {
                CapturedPayload = ctx.Request.Payload;
                CapturedTargetName = ctx.GetTarget<TestAccount>().Name;
            }
        }

        [Fact]
        public void Post_stage_entity_typed_handler_receives_typed_request_response_and_target()
        {
            TypedTargetPostProbe probe = new TypedTargetPostProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(CustomMessage)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Name = "Acme" })
                .WithPostImage(new TestAccount { Name = "AcmePost" })
                .WithInputParameter(nameof(DoThingRequest.Payload), "hello")
                .Execute(probe);

            Assert.Equal("hello", probe.CapturedPayload);
            Assert.Equal("Acme", probe.CapturedTargetName);
            Assert.Equal("AcmePost", probe.CapturedPostImageName);
            Assert.True(probe.ResponseAvailable);
        }

        [Fact]
        public void Pre_stage_entity_typed_handler_receives_typed_request_and_target()
        {
            TypedTargetPreProbe probe = new TypedTargetPreProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(CustomMessage)
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Name = "Globex" })
                .WithInputParameter(nameof(DoThingRequest.Payload), "world")
                .Execute(probe);

            Assert.Equal("world", probe.CapturedPayload);
            Assert.Equal("Globex", probe.CapturedTargetName);
        }

        [Fact]
        public void Post_stage_generic_handler_receives_typed_request_and_response()
        {
            PostProbe probe = new PostProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(CustomMessage)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter(nameof(DoThingRequest.Payload), "hello")
                .Execute(probe);

            Assert.Equal("hello", probe.CapturedPayload);
            Assert.True(probe.ResponseAvailable);
        }

        [Fact]
        public void Pre_stage_generic_handler_receives_typed_request()
        {
            PreProbe probe = new PreProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(CustomMessage)
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter(nameof(DoThingRequest.Payload), "world")
                .Execute(probe);

            Assert.Equal("world", probe.CapturedPayload);
        }
    }
}
