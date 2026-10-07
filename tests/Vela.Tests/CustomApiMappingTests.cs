using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Messages;
using Vela.Plugin;
using Vela.Testing;
using Xunit;

namespace Vela.Tests
{
    public sealed class CustomApiMappingTests
    {
        // Request/response derive from TypedRequest/TypedResponse: each typed property is a one-liner over the
        // base Get<T>() / Set(value) helpers (key defaults to the property name), matching the generator shape.
        public sealed class ScoreRequest : TypedRequest
        {
            public ScoreRequest()
            {
                RequestName = "zava_Score";
            }

            public int AccountId
            {
                get => Get<int>();
                set => Set(value);
            }
        }

        public sealed class ScoreResponse : TypedResponse
        {
            public int Score
            {
                get => Get<int>();
                set => Set(value);
            }

            public string Rating
            {
                get => Get<string>();
                set => Set(value);
            }
        }

        // Minimal typed Custom API: the request is hydrated over the live InputParameters and the response's
        // Results are copied back to OutputParameters, independent of registration.
        private sealed class ScoringApi : CustomApiBase<ScoreRequest, ScoreResponse>
        {
            public ScoringApi() : base(string.Empty, string.Empty)
            {
            }

            protected override ScoreResponse Execute(ILocalPluginContext ctx, ScoreRequest request)
            {
                return new ScoreResponse { Score = request.AccountId * 10, Rating = "High" };
            }
        }

        [Fact]
        public void TypedCustomApi_MapsInputToRequest_AndResponseToOutput()
        {
            ScoringApi api = new ScoringApi();
            LocalPluginContext context = PluginContext.ForMessage("zava_Score").Build();
            context.PluginExecutionContext.InputParameters["AccountId"] = 5;

            api.Execute(context.ServiceProvider);

            Assert.Equal(50, context.PluginExecutionContext.OutputParameters["Score"]);
            Assert.Equal("High", context.PluginExecutionContext.OutputParameters["Rating"]);
        }

        // A request whose property is a plain auto-property: nothing populates its backing field under the
        // bag-hydration model, so an input value would silently bind to its default.
        public sealed class AutoPropRequest : OrganizationRequest
        {
            public AutoPropRequest()
            {
                RequestName = "zava_Legacy";
            }

            public int AccountId { get; set; }
        }

        public sealed class EmptyResponse : OrganizationResponse
        {
        }

        private sealed class LegacyApi : CustomApiBase<AutoPropRequest, EmptyResponse>
        {
            public LegacyApi() : base(string.Empty, string.Empty)
            {
            }

            protected override EmptyResponse Execute(ILocalPluginContext ctx, AutoPropRequest request)
            {
                return new EmptyResponse();
            }
        }

        [Fact]
        public void TypedCustomApi_AutoPropertyRequest_FailsLoudlyInsteadOfBindingDefault()
        {
            LegacyApi api = new LegacyApi();
            LocalPluginContext context = PluginContext.ForMessage("zava_Legacy").Build();
            context.PluginExecutionContext.InputParameters["AccountId"] = 7;

            InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(
                () => api.Execute(context.ServiceProvider));
            Assert.Contains("AccountId", ex.Message);
            Assert.Contains("auto-property", ex.Message);
        }
    }

    // The auto-property guard is computed once per request type, then checked per execution against the inputs.
    public sealed class CustomApiRequestBindingCacheTests
    {
        private sealed class DepthProbeApi : CustomApiBase<CustomApiMappingTests.AutoPropRequest, CustomApiMappingTests.EmptyResponse>
        {
            public DepthProbeApi() : base(string.Empty, string.Empty)
            {
            }

            public int ObservedMaxDepth => MaxDepth;

            public int Calls { get; private set; }

            protected override CustomApiMappingTests.EmptyResponse Execute(ILocalPluginContext ctx, CustomApiMappingTests.AutoPropRequest request)
            {
                Calls++;
                return new CustomApiMappingTests.EmptyResponse();
            }
        }

        [Fact]
        public void The_guard_keeps_failing_on_every_execution_once_cached()
        {
            DepthProbeApi api = new DepthProbeApi();
            for (int run = 0; run < 3; run++)
            {
                LocalPluginContext context = PluginContext.ForMessage("zava_Legacy").Build();
                context.PluginExecutionContext.InputParameters["AccountId"] = 7;

                InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => api.Execute(context.ServiceProvider));
                Assert.Contains("AccountId", ex.Message);
            }

            Assert.Equal(0, api.Calls);
        }

        [Fact]
        public void An_input_that_matches_no_auto_property_runs_the_handler()
        {
            DepthProbeApi api = new DepthProbeApi();
            LocalPluginContext context = PluginContext.ForMessage("zava_Legacy").Build();
            context.PluginExecutionContext.InputParameters["SomethingElse"] = 7;

            api.Execute(context.ServiceProvider);

            Assert.Equal(1, api.Calls);
        }

        [Fact]
        public void Custom_API_bases_default_to_the_shared_depth_constant()
        {
            Assert.Equal(PluginBase.DefaultMaxDepth, new DepthProbeApi().ObservedMaxDepth);
        }
    }
}
