using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Testing;
using Xunit;

namespace Vela.Tests
{
    public sealed class CustomApiMappingTests
    {
        // Request/response derive from OrganizationRequest/OrganizationResponse: the typed properties read and
        // write the underlying Parameters/Results bags, matching the CustomApiMessageGenerator shape.
        public sealed class ScoreRequest : OrganizationRequest
        {
            public ScoreRequest()
            {
                RequestName = "zava_Score";
            }

            public int AccountId
            {
                get => Parameters.Contains(nameof(AccountId)) ? (int)Parameters[nameof(AccountId)] : default;
                set => Parameters[nameof(AccountId)] = value;
            }
        }

        public sealed class ScoreResponse : OrganizationResponse
        {
            public int Score
            {
                get => Results.Contains(nameof(Score)) ? (int)Results[nameof(Score)] : default;
                set => Results[nameof(Score)] = value;
            }

            public string Rating
            {
                get => Results.Contains(nameof(Rating)) ? (string)Results[nameof(Rating)] : string.Empty;
                set => Results[nameof(Rating)] = value;
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
    }
}
