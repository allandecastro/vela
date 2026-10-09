using Microsoft.Xrm.Sdk;
using Vela.Messages;
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
        [InlineData("CloseQuote", "Close")]
        [InlineData("CancelSalesOrder", "Cancel")]
        [InlineData("SendFax", "Send")]
        [InlineData("SendTemplate", "Send")]
        [InlineData("PublishXml", "Publish")]
        [InlineData("ValidateSavedQuery", "Validate")]
        [InlineData("SetState", "SetState")]
        [InlineData("sendemail", "Send")]
        [InlineData("Create", "Create")]
        [InlineData("Win", "Win")]
        [InlineData("acme_DoThing", "acme_DoThing")]
        public void Request_names_map_to_the_sdkmessage(string requestName, string sdkMessage)
            => Assert.Equal(sdkMessage, SdkMessageNames.ToSdkMessage(requestName));

        // A generated request whose name differs from its sdkmessage states the sdkmessage with [SdkMessage]; the
        // RequestName is deliberately one the table does not know, so only the attribute can name "Close".
        [SdkMessage("Close")]
        private sealed class CloseThingRequest : TypedRequest
        {
            public CloseThingRequest()
            {
                RequestName = "acme_CloseThing";
            }
        }

        private sealed class ThingClosed : EntityPluginBase<TestAccount>
        {
            public ThingClosed() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().On<CloseThingRequest>().PostOperation().Sync().Handle(_ => { });
        }

        [Fact]
        public void A_request_type_with_SdkMessage_registers_on_that_sdkmessage_and_keeps_the_request_name()
        {
            StepDefinition definition = Assert.Single(new ThingClosed().GetStepDefinitions());

            Assert.Equal("Close", definition.Message);
            Assert.Equal("acme_CloseThing", definition.RequestName);
        }

        [Fact]
        public void A_request_instance_resolves_its_sdkmessage_from_SdkMessage_then_the_table()
        {
            Assert.Equal("Close", SdkMessageNames.ToSdkMessage(new CloseThingRequest()));
            Assert.Equal("Close", SdkMessageNames.ToSdkMessage(new OrganizationRequest("CloseQuote")));
            Assert.Equal("acme_Other", SdkMessageNames.ToSdkMessage(new OrganizationRequest("acme_Other")));
        }

        [Fact]
        public void A_typed_trigger_registers_on_the_sdkmessage_and_keeps_the_request_name()
        {
            StepDefinition definition = Assert.Single(new QuoteWon().GetStepDefinitions());

            Assert.Equal("Win", definition.Message);
            Assert.Equal("WinQuote", definition.RequestName);
        }

    }
}
