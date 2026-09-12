using System;
using Vela.Constants;
using Vela.Context;
using Vela.Messages;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Proves Feature B: RegisterSteps().On<TRequest>() derives the message name from the request type (its
    // RequestName), so there is no magic string, and it hands the caller a handler that is already typed to
    // TRequest - ctx.Request is IMessagePre/PostContext<TRequest> without restating the type on Handler(...).
    public sealed class TypedMessageOnRequestTests
    {
        private const string MessageName = "vela_x";

        // A Vela-owned typed request (TypedRequest shape): its RequestName IS the message, and a typed property is
        // a one-liner over the base Get<T>() / Set(value) helpers (key defaults to the property name).
        private sealed class CalculateAccountScoreRequest : TypedRequest
        {
            public CalculateAccountScoreRequest()
            {
                RequestName = MessageName;
            }

            public int Weight
            {
                get => Get<int>();
                set => Set(value);
            }
        }

        private sealed class PostProbe : EntityPluginBase<TestAccount>
        {
            public int CapturedWeight;
            public bool ResponseAvailable;

            public PostProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().On<CalculateAccountScoreRequest>().PostOperation().Sync().Handler(OnScore);

            // No type argument on Handler: ctx.Request is typed to CalculateAccountScoreRequest already.
            private void OnScore(IMessagePostContext<CalculateAccountScoreRequest> ctx)
            {
                CapturedWeight = ctx.Request.Weight;       // typed, no cast
                ResponseAvailable = ctx.Response != null;  // base OrganizationResponse at post-stage
            }
        }

        private sealed class PreProbe : EntityPluginBase<TestAccount>
        {
            public int CapturedWeight;

            public PreProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().On<CalculateAccountScoreRequest>().PreOperation().Sync().Handler(OnScore);

            private void OnScore(IMessagePreContext<CalculateAccountScoreRequest> ctx)
                => CapturedWeight = ctx.Request.Weight;
        }

        [Fact]
        public void GetMessageName_ResolvesRequestNameFromRequestType()
        {
            Assert.Equal(MessageName, ExpressionHelper.GetMessageName<CalculateAccountScoreRequest>());
        }

        [Fact]
        public void Post_stage_On_request_fires_on_the_request_name_with_typed_request()
        {
            PostProbe probe = new PostProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(MessageName)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter(nameof(CalculateAccountScoreRequest.Weight), 42)
                .Execute(probe);

            Assert.Equal(42, probe.CapturedWeight);
            Assert.True(probe.ResponseAvailable);
        }

        [Fact]
        public void Pre_stage_On_request_fires_on_the_request_name_with_typed_request()
        {
            PreProbe probe = new PreProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(MessageName)
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter(nameof(CalculateAccountScoreRequest.Weight), 7)
                .Execute(probe);

            Assert.Equal(7, probe.CapturedWeight);
        }
    }
}
