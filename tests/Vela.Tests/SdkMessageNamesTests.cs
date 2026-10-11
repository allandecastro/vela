using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
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
        // OnWinQuote() is accepted on the quote table only.
        [EntityLogicalName("quote")]
        private sealed class Quote() : Entity("quote");

        private sealed class QuoteWon : EntityPluginBase<Quote>
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
        // Requests no typed trigger models, read from sdkmessagerequest too: On<CloseQuoteRequest>() registers on Close.
        [InlineData("CloseQuote", "Close")]
        [InlineData("CancelSalesOrder", "Cancel")]
        [InlineData("SendFax", "Send")]
        [InlineData("PublishXml", "Publish")]
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

        // The table each request runs on (verified live for the modelled ones): the harness and the typed triggers
        // rely on it to tell WinQuote (quote) from WinOpportunity (opportunity), and the queue messages run on
        // queueitem whatever their Target.
        [Theory]
        [InlineData("SendEmail", "email")]
        [InlineData("WinQuote", "quote")]
        [InlineData("WinOpportunity", "opportunity")]
        [InlineData("CloseIncident", "incident")]
        [InlineData("AddToQueue", "queueitem")]
        [InlineData("QualifyLead", "lead")]
        [InlineData("Create", null)]
        [InlineData("acme_DoThing", null)]
        public void Request_names_map_to_their_table(string requestName, string? table)
            => Assert.Equal(table, SdkMessageNames.PrimaryEntityOf(requestName));

        [Fact]
        public void An_sdkmessage_lists_the_requests_it_runs()
            => Assert.Equal(new[] { "WinOpportunity", "WinQuote" }, SdkMessageNames.RequestNamesFor("Win").OrderBy(n => n).ToArray());
    }
}
