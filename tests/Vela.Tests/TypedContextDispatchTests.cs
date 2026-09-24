using System;
using Microsoft.Crm.Sdk.Messages;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
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
                RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnAny);
                RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnAny);
                RegisterStep().OnDelete().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnCreateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnAny);
                RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnAny);
                RegisterStep().OnRetrieve().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRetrieveMultiple().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnAssociate().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnDisassociate().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnMerge().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnGrantAccess().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnModifyAccess().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRevokeAccess().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnMessage(CustomMessage).PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnQualifyLead().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnWinOpportunity().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnLoseOpportunity().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnConvertQuoteToSalesOrder().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnConvertSalesOrderToInvoice().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnGenerateQuoteFromOpportunity().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnGenerateSalesOrderFromOpportunity().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnGenerateInvoiceFromOpportunity().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnAddToQueue().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRemoveFromQueue().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnPickFromQueue().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRouteTo().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnReleaseToQueue().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnSendEmail().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnSendEmailFromTemplate().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnDeliverIncomingEmail().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnDeliverPromoteEmail().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnAddMembersTeam().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRemoveMembersTeam().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnAddListMembersList().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnAddMemberList().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRemoveMemberList().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnCloseIncident().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnCancelContract().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnCloneContract().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRetrievePrincipalAccess().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRetrieveSharedPrincipalsAndAccess().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnWinQuote().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnReviseQuote().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnFulfillSalesOrder().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnGetQuoteProductsFromOpportunity().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnGetSalesOrderProductsFromOpportunity().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnGetInvoiceProductsFromOpportunity().PostOperation().Sync().Handle(OnAny);
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

        // Regression: on a real org an (Dis)Associate execution context carries no primary entity matching the
        // step's TEntity (the message is relationship-global), so a sync step must still match. Before the fix this
        // threw "Plugin '...' has no registered step matching message 'Associate'." A non-matching primary entity
        // reproduces the real pipeline; the earlier Each_message test masked it by driving Associate with the
        // step's own entity.
        [Theory]
        [InlineData(PluginMessages.Associate)]
        [InlineData(PluginMessages.Disassociate)]
        public void Relationship_message_dispatches_when_primary_entity_does_not_match_the_step_entity(string message)
        {
            AllMessagesProbe probe = new AllMessagesProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(message)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity("contact", Guid.NewGuid())   // NOT the step's TEntity (account)
                .Execute(probe);

            Assert.Equal(message, probe.Seen);
        }

        // Regression: On<TRequest>() where the request's RequestName collides with a modelled message (MergeRequest
        // -> "Merge"). The handler's parameter is IMessagePreContext<MergeRequest>, which the modelled
        // MergePreContext<TEntity> does NOT implement, so dispatch must build the generic MessagePreContext<TRequest>
        // (from the handler's parameter type), not the modelled per-message context, or the handler never matches.
        private sealed class TypedRequestProbe : EntityPluginBase<TestAccount>
        {
            public string? Seen { get; private set; }

            public TypedRequestProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
            {
                RegisterStep().On<MergeRequest>().PreOperation().Sync().Handle(OnMerge);
            }

            private void OnMerge(IMessagePreContext<MergeRequest> ctx) => Seen = ctx.MessageName;
        }

        [Fact]
        public void Typed_request_colliding_with_a_modelled_message_dispatches_to_its_generic_handler()
        {
            TypedRequestProbe probe = new TypedRequestProbe();

            PluginContext.For<TestAccount>()
                .OnMessage(PluginMessages.Merge)
                .Stage(PluginStages.PreOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .Execute(probe);

            Assert.Equal(PluginMessages.Merge, probe.Seen);
        }
    }
}
