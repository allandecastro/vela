using System;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Proves the runtime dispatch builds the right per-message context for every modelled message (each arm of
    // EntityPluginBase.BuildTypedContext), and that a permissive OnMessage(...) step binds and runs an untyped
    // handler. One plugin registers a step for all 13 modelled messages plus a custom message, each bound to a
    // single ILocalPluginContext handler (assignable to every typed context by contravariance); the test runs it
    // once per message and asserts the matching step fired.
    public sealed class TypedContextDispatchTests
    {
        private sealed class AllMessagesProbe : EntityPluginBase<TestAccount>
        {
            public const string CustomMessage = "vela_custom";

            public string? Seen { get; private set; }

            public AllMessagesProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterSteps().OnCreate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnUpdate().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnDelete().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnCreateMultiple().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnUpdateMultiple().PostOperation().Mode(ExecutionMode.Synchronous).FilteringAttributes(a => a.Name).Handler(OnAny);
                RegisterSteps().OnRetrieve().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRetrieveMultiple().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnAssociate().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnDisassociate().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnMerge().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGrantAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnModifyAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRevokeAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnMessage(CustomMessage).PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnQualifyLead().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnWinOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnLoseOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnConvertQuoteToSalesOrder().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnConvertSalesOrderToInvoice().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGenerateQuoteFromOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGenerateSalesOrderFromOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGenerateInvoiceFromOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnAddToQueue().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRemoveFromQueue().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnPickFromQueue().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRouteTo().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnReleaseToQueue().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnSendEmail().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnSendEmailFromTemplate().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnDeliverIncomingEmail().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnDeliverPromoteEmail().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnAddMembersTeam().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRemoveMembersTeam().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnAddListMembersList().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnAddMemberList().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRemoveMemberList().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnCloseIncident().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnCancelContract().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnCloneContract().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRetrievePrincipalAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnRetrieveSharedPrincipalsAndAccess().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnWhoAmI().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnWinQuote().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnReviseQuote().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnFulfillSalesOrder().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGetQuoteProductsFromOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGetSalesOrderProductsFromOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
                RegisterSteps().OnGetInvoiceProductsFromOpportunity().PostOperation().Mode(ExecutionMode.Synchronous).Handler(OnAny);
            }

            // Bound to every step: contravariantly assignable to each Action<I{Msg}Context<TestAccount>> and to
            // the permissive Action<ILocalPluginContext>.
            private void OnAny(ILocalPluginContext ctx) => Seen = ctx.MessageName;
        }

        [Theory]
        [InlineData(PluginMessages.Create)]
        [InlineData(PluginMessages.Update)]
        [InlineData(PluginMessages.Delete)]
        [InlineData(PluginMessages.CreateMultiple)]
        [InlineData(PluginMessages.UpdateMultiple)]
        [InlineData(PluginMessages.Retrieve)]
        [InlineData(PluginMessages.RetrieveMultiple)]
        [InlineData(PluginMessages.Associate)]
        [InlineData(PluginMessages.Disassociate)]
        [InlineData(PluginMessages.Merge)]
        [InlineData(PluginMessages.GrantAccess)]
        [InlineData(PluginMessages.ModifyAccess)]
        [InlineData(PluginMessages.RevokeAccess)]
        [InlineData(PluginMessages.QualifyLead)]
        [InlineData(PluginMessages.WinOpportunity)]
        [InlineData(PluginMessages.LoseOpportunity)]
        [InlineData(PluginMessages.ConvertQuoteToSalesOrder)]
        [InlineData(PluginMessages.ConvertSalesOrderToInvoice)]
        [InlineData(PluginMessages.GenerateQuoteFromOpportunity)]
        [InlineData(PluginMessages.GenerateSalesOrderFromOpportunity)]
        [InlineData(PluginMessages.GenerateInvoiceFromOpportunity)]
        [InlineData(PluginMessages.AddToQueue)]
        [InlineData(PluginMessages.RemoveFromQueue)]
        [InlineData(PluginMessages.PickFromQueue)]
        [InlineData(PluginMessages.RouteTo)]
        [InlineData(PluginMessages.ReleaseToQueue)]
        [InlineData(PluginMessages.SendEmail)]
        [InlineData(PluginMessages.SendEmailFromTemplate)]
        [InlineData(PluginMessages.DeliverIncomingEmail)]
        [InlineData(PluginMessages.DeliverPromoteEmail)]
        [InlineData(PluginMessages.AddMembersTeam)]
        [InlineData(PluginMessages.RemoveMembersTeam)]
        [InlineData(PluginMessages.AddListMembersList)]
        [InlineData(PluginMessages.AddMemberList)]
        [InlineData(PluginMessages.RemoveMemberList)]
        [InlineData(PluginMessages.CloseIncident)]
        [InlineData(PluginMessages.CancelContract)]
        [InlineData(PluginMessages.CloneContract)]
        [InlineData(PluginMessages.RetrievePrincipalAccess)]
        [InlineData(PluginMessages.RetrieveSharedPrincipalsAndAccess)]
        [InlineData(PluginMessages.WhoAmI)]
        [InlineData(PluginMessages.WinQuote)]
        [InlineData(PluginMessages.ReviseQuote)]
        [InlineData(PluginMessages.FulfillSalesOrder)]
        [InlineData(PluginMessages.GetQuoteProductsFromOpportunity)]
        [InlineData(PluginMessages.GetSalesOrderProductsFromOpportunity)]
        [InlineData(PluginMessages.GetInvoiceProductsFromOpportunity)]
        [InlineData(AllMessagesProbe.CustomMessage)]
        public void Each_message_dispatches_to_its_handler(string message)
        {
            AllMessagesProbe probe = new AllMessagesProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(message)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .Execute(probe);

            Assert.Equal(message, probe.Seen);
        }
    }
}
