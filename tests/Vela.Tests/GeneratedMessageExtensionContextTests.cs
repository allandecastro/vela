using System;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    // A step registered through the On{X}() extension `vela models --custom-apis` writes to Messages/{Name}.Steps.cs
    // binds a handler typed with its generated {Handler}Context (MessagePre/PostContext<TRequest, TEntity>, hand-written
    // here as the generator emits it), which runs and reads the typed ctx.Request and image.
    public sealed class GeneratedMessageExtensionContextTests
    {
        private sealed class PostProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedReason;
            public string? CapturedPostImageName;
            public bool ResponseAvailable;

            public PostProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnScore().PostOperation().Sync().WithPostImage(a => a.Name).Handle(OnScored);

            private void OnScored(OnScoredContext ctx)
            {
                CapturedReason = ctx.Request.Reason;
                CapturedPostImageName = ctx.PostImage?.Name;
                ResponseAvailable = ctx.Response != null;
            }

            public sealed class OnScoredContext : MessagePostContext<vela_ScoreRequest, TestAccount>
            {
                public OnScoredContext(ILocalPluginContext inner) : base(inner) { }
            }
        }

        private sealed class PreProbe : EntityPluginBase<TestAccount>
        {
            public string? CapturedReason;

            public PreProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnScore().PreOperation().Sync().Handle(OnScoring);

            private void OnScoring(OnScoringContext ctx) => CapturedReason = ctx.Request.Reason;

            public sealed class OnScoringContext : MessagePreContext<vela_ScoreRequest, TestAccount>
            {
                public OnScoringContext(ILocalPluginContext inner) : base(inner) { }
            }
        }

        [Fact]
        public void Post_stage_handler_context_reads_the_typed_request_and_image()
        {
            PostProbe probe = new PostProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(vela_ScoreRequest.MessageName)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithPostImage(new TestAccount { Name = "Contoso" })
                .WithInputParameter(nameof(vela_ScoreRequest.Reason), "renewal")
                .Execute(probe);

            Assert.Equal("renewal", probe.CapturedReason);
            Assert.Equal("Contoso", probe.CapturedPostImageName);
            Assert.True(probe.ResponseAvailable);
        }

        [Fact]
        public void Pre_stage_handler_context_reads_the_typed_request()
        {
            PreProbe probe = new PreProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(vela_ScoreRequest.MessageName)
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter(nameof(vela_ScoreRequest.Reason), "upsell")
                .Execute(probe);

            Assert.Equal("upsell", probe.CapturedReason);
        }
    }

    // A stand-in for the generated Custom API request.
    internal sealed class vela_ScoreRequest : OrganizationRequest
    {
        public const string MessageName = "vela_Score";

        public vela_ScoreRequest()
        {
            RequestName = MessageName;
        }

        public string? Reason
        {
            get => Parameters.TryGetValue(nameof(Reason), out object? value) ? (string?)value : null;
            set => Parameters[nameof(Reason)] = value!;
        }
    }

    // The shape of the Messages/vela_Score.Steps.cs extension `vela models --custom-apis` writes for a bound API.
    internal static class vela_ScoreStepExtensions
    {
        public static ITypedStages<TestAccount, vela_ScoreRequest> OnScore(this IStepTrigger<TestAccount> builder)
            => builder.On<vela_ScoreRequest>();
    }
}
