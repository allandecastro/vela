using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // #461: typed triggers register on the sdkmessage, which differs from the SDK request name for some business
    // messages (verified live on 2026-10-09: none of the request names exists as an sdkmessage).
    public sealed class SdkMessageNamesTests
    {
        private sealed class QuoteWon : EntityPluginBase<TestAccount>
        {
            public QuoteWon() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnWinQuote().PostOperation().Sync().Handle(_ => { });
        }

        [Theory]
        [InlineData("SendEmail", "Send")]
        [InlineData("SendEmailFromTemplate", "SendFromTemplate")]
        [InlineData("DeliverIncomingEmail", "DeliverIncoming")]
        [InlineData("DeliverPromoteEmail", "DeliverPromote")]
        [InlineData("WinOpportunity", "Win")]
        [InlineData("WinQuote", "Win")]
        [InlineData("LoseOpportunity", "Lose")]
        [InlineData("CloseIncident", "Close")]
        [InlineData("CancelContract", "Cancel")]
        [InlineData("CloneContract", "Clone")]
        [InlineData("AddMembersTeam", "AddMembers")]
        [InlineData("RemoveMembersTeam", "RemoveMembers")]
        [InlineData("AddListMembersList", "AddListMembers")]
        [InlineData("AddMemberList", "AddMember")]
        [InlineData("RemoveMemberList", "RemoveMember")]
        [InlineData("sendemail", "Send")]
        [InlineData("Create", "Create")]
        [InlineData("Win", "Win")]
        [InlineData("acme_DoThing", "acme_DoThing")]
        public void Request_names_map_to_the_sdkmessage(string requestName, string sdkMessage)
            => Assert.Equal(sdkMessage, SdkMessageNames.ToSdkMessage(requestName));

        [Fact]
        public void A_typed_trigger_registers_on_the_sdkmessage_and_keeps_the_request_name()
        {
            StepDefinition definition = Assert.Single(new QuoteWon().GetStepDefinitions());

            Assert.Equal("Win", definition.Message);
            Assert.Equal("WinQuote", definition.RequestName);
        }

    }
}
