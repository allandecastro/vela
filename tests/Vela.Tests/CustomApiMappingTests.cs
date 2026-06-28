using Vela.Context;
using Vela.CustomApi;
using Vela.Testing;
using Xunit;

namespace Vela.Tests
{
    public sealed class CustomApiMappingTests
    {
        public sealed class ScoreRequest
        {
            public int AccountId { get; set; }
        }

        public sealed class ScoreResponse
        {
            public int Score { get; set; }

            public string Rating { get; set; } = string.Empty;
        }

        // Minimal typed Custom API: the mapping (input params -> request, response -> output params)
        // is driven by reflection over the cached PropertyInfo[] arrays, independent of registration.
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
    }
}
