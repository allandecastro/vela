using System;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
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
    // handler. One plugin registers a step for every modelled message any table accepts plus a custom message, and
    // one plugin per table covers the messages Dataverse accepts on a single table (OnSendEmail() on email, ...).
    // Each step is bound to a single ILocalPluginContext handler (assignable to every typed context by
    // contravariance); the test runs the plugin once per message and asserts the matching step fired.
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
                RegisterStep().OnAssign().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnMessage(CustomMessage).PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRetrievePrincipalAccess().PostOperation().Sync().Handle(OnAny);
                RegisterStep().OnRetrieveSharedPrincipalsAndAccess().PostOperation().Sync().Handle(OnAny);
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
        [InlineData(PluginMessages.Assign)]
        [InlineData(PluginMessages.RetrievePrincipalAccess)]
        [InlineData(PluginMessages.RetrieveSharedPrincipalsAndAccess)]
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

            // The platform sends the sdkmessage, which differs from the request name for some messages
            // (SendEmail runs as Send); the context builder translates it the same way.
            Assert.Equal(SdkMessageNames.ToSdkMessage(message), probe.Seen);
        }

        // A table-bound message: one plugin on its table, whose step must be the one that runs and must hand its
        // handler the typed context of the trigger (OnWinQuote() gets a WinQuote context, not a WinOpportunity one,
        // although both register on "Win").
        [Theory]
        [MemberData(nameof(TableBoundTriggers))]
        public void Each_table_bound_message_dispatches_to_its_typed_handler(string trigger, string message, string table)
        {
            ITableProbe probe = TableProbe.Create(trigger, table);

            probe.Execute(message);

            Assert.Equal(message, probe.SeenMessage);
            Assert.Equal(trigger, probe.SeenContext);
        }

        public static TheoryData<string, string, string> TableBoundTriggers() => new TheoryData<string, string, string>
        {
            { "QualifyLead", "QualifyLead", "lead" },
            { "WinOpportunity", "Win", "opportunity" },
            { "LoseOpportunity", "Lose", "opportunity" },
            { "ConvertSalesOrderToInvoice", "ConvertSalesOrderToInvoice", "salesorder" },
            { "GenerateQuoteFromOpportunity", "GenerateQuoteFromOpportunity", "quote" },
            { "AddToQueue", "AddToQueue", "queueitem" },
            { "RemoveFromQueue", "RemoveFromQueue", "queueitem" },
            { "PickFromQueue", "PickFromQueue", "queueitem" },
            { "RouteTo", "RouteTo", "queueitem" },
            { "ReleaseToQueue", "ReleaseToQueue", "queueitem" },
            { "SendEmail", "Send", "email" },
            { "SendEmailFromTemplate", "SendFromTemplate", "email" },
            { "DeliverIncomingEmail", "DeliverIncoming", "email" },
            { "DeliverPromoteEmail", "DeliverPromote", "email" },
            { "AddMembersTeam", "AddMembers", "team" },
            { "RemoveMembersTeam", "RemoveMembers", "team" },
            { "AddListMembersList", "AddListMembers", "list" },
            { "AddMemberList", "AddMember", "list" },
            { "RemoveMemberList", "RemoveMember", "list" },
            { "CloseIncident", "Close", "incident" },
            { "CancelContract", "Cancel", "contract" },
            { "CloneContract", "Clone", "contract" },
            { "WinQuote", "Win", "quote" },
        };

        [Fact]
        public void Table_bound_trigger_on_another_table_is_rejected()
        {
            WrongTableProbe probe = new WrongTableProbe();

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => probe.GetStepDefinitions());

            Assert.Contains("OnWinQuote() registers on the 'Win' message of table 'quote'", error.Message);
            Assert.Contains("'opportunity'", error.Message);
        }

        private sealed class WrongTableProbe : EntityPluginBase<TableEntity.Opportunity>
        {
            public WrongTableProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnWinQuote().PostOperation().Sync().Handle(OnWin);

            private void OnWin(IWinQuotePostContext<TableEntity.Opportunity> ctx) { }
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

        // OnAssign() builds a typed AssignPostContext<TEntity> at dispatch; prove the handler actually runs with a
        // typed ctx.Request projected over the live InputParameters (the Assignee lookup), not just that the step
        // shape is right.
        private sealed class AssignTypedProbe : EntityPluginBase<TestAccount>
        {
            public Guid? SeenAssignee { get; private set; }

            public AssignTypedProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterStep().OnAssign().PostOperation().Sync().Handle(OnAssign);

            private void OnAssign(IAssignPostContext<TestAccount> ctx) => SeenAssignee = ctx.Request.Assignee?.Id;
        }

        [Fact]
        public void OnAssign_dispatches_a_typed_context_exposing_the_assignee()
        {
            AssignTypedProbe probe = new AssignTypedProbe();
            Guid assigneeId = Guid.NewGuid();

            PluginContext.For<TestAccount>()
                .OnMessage(PluginMessages.Assign)
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithPrimaryEntity(TestAccount.EntityLogicalNameValue, Guid.NewGuid())
                .WithInputParameter("Assignee", new EntityReference("systemuser", assigneeId))
                .Execute(probe);

            Assert.Equal(assigneeId, probe.SeenAssignee);
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

        private interface ITableProbe
        {
            string? SeenMessage { get; }

            string? SeenContext { get; }

            void Execute(string message);
        }

        // One early-bound class per table a modelled message is bound to, so a probe can be an EntityPluginBase<T>
        // on that table.
        private static class TableEntity
        {
            [EntityLogicalName("lead")] public sealed class Lead() : Entity("lead");
            [EntityLogicalName("opportunity")] public sealed class Opportunity() : Entity("opportunity");
            [EntityLogicalName("salesorder")] public sealed class SalesOrder() : Entity("salesorder");
            [EntityLogicalName("quote")] public sealed class Quote() : Entity("quote");
            [EntityLogicalName("queueitem")] public sealed class QueueItem() : Entity("queueitem");
            [EntityLogicalName("email")] public sealed class Email() : Entity("email");
            [EntityLogicalName("team")] public sealed class Team() : Entity("team");
            [EntityLogicalName("list")] public sealed class List() : Entity("list");
            [EntityLogicalName("incident")] public sealed class Incident() : Entity("incident");
            [EntityLogicalName("contract")] public sealed class Contract() : Entity("contract");
        }

        private static class TableProbe
        {
            public static ITableProbe Create(string trigger, string table) => table switch
            {
                "lead" => new TableProbe<TableEntity.Lead>(trigger),
                "opportunity" => new TableProbe<TableEntity.Opportunity>(trigger),
                "salesorder" => new TableProbe<TableEntity.SalesOrder>(trigger),
                "quote" => new TableProbe<TableEntity.Quote>(trigger),
                "queueitem" => new TableProbe<TableEntity.QueueItem>(trigger),
                "email" => new TableProbe<TableEntity.Email>(trigger),
                "team" => new TableProbe<TableEntity.Team>(trigger),
                "list" => new TableProbe<TableEntity.List>(trigger),
                "incident" => new TableProbe<TableEntity.Incident>(trigger),
                "contract" => new TableProbe<TableEntity.Contract>(trigger),
                _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
            };
        }

        // Registers the one typed trigger named by `trigger` on TEntity's table, and records the message the step
        // ran on and the modelled message of the typed context its handler received (WinQuotePostContext ->
        // "WinQuote").
        private sealed class TableProbe<TEntity>(string trigger) : EntityPluginBase<TEntity>(string.Empty, string.Empty), ITableProbe
            where TEntity : Entity, new()
        {
            public string? SeenMessage { get; private set; }

            public string? SeenContext { get; private set; }

            public void Execute(string message)
                => PluginContext.For<TEntity>()
                    .OnMessage(message)
                    .Stage(PluginStages.PostOperation)
                    .Mode(ExecutionMode.Synchronous)
                    .WithPrimaryEntity(new TEntity().LogicalName, Guid.NewGuid())
                    .Execute(this);

            protected override void RegisterPluginSteps()
            {
                IStepTrigger<TEntity> step = RegisterStep();
                switch (trigger)
                {
                    case "QualifyLead": step.OnQualifyLead().PostOperation().Sync().Handle(OnAny); break;
                    case "WinOpportunity": step.OnWinOpportunity().PostOperation().Sync().Handle(OnAny); break;
                    case "LoseOpportunity": step.OnLoseOpportunity().PostOperation().Sync().Handle(OnAny); break;
                    case "ConvertSalesOrderToInvoice": step.OnConvertSalesOrderToInvoice().PostOperation().Sync().Handle(OnAny); break;
                    case "GenerateQuoteFromOpportunity": step.OnGenerateQuoteFromOpportunity().PostOperation().Sync().Handle(OnAny); break;
                    case "AddToQueue": step.OnAddToQueue().PostOperation().Sync().Handle(OnAny); break;
                    case "RemoveFromQueue": step.OnRemoveFromQueue().PostOperation().Sync().Handle(OnAny); break;
                    case "PickFromQueue": step.OnPickFromQueue().PostOperation().Sync().Handle(OnAny); break;
                    case "RouteTo": step.OnRouteTo().PostOperation().Sync().Handle(OnAny); break;
                    case "ReleaseToQueue": step.OnReleaseToQueue().PostOperation().Sync().Handle(OnAny); break;
                    case "SendEmail": step.OnSendEmail().PostOperation().Sync().Handle(OnAny); break;
                    case "SendEmailFromTemplate": step.OnSendEmailFromTemplate().PostOperation().Sync().Handle(OnAny); break;
                    case "DeliverIncomingEmail": step.OnDeliverIncomingEmail().PostOperation().Sync().Handle(OnAny); break;
                    case "DeliverPromoteEmail": step.OnDeliverPromoteEmail().PostOperation().Sync().Handle(OnAny); break;
                    case "AddMembersTeam": step.OnAddMembersTeam().PostOperation().Sync().Handle(OnAny); break;
                    case "RemoveMembersTeam": step.OnRemoveMembersTeam().PostOperation().Sync().Handle(OnAny); break;
                    case "AddListMembersList": step.OnAddListMembersList().PostOperation().Sync().Handle(OnAny); break;
                    case "AddMemberList": step.OnAddMemberList().PostOperation().Sync().Handle(OnAny); break;
                    case "RemoveMemberList": step.OnRemoveMemberList().PostOperation().Sync().Handle(OnAny); break;
                    case "CloseIncident": step.OnCloseIncident().PostOperation().Sync().Handle(OnAny); break;
                    case "CancelContract": step.OnCancelContract().PostOperation().Sync().Handle(OnAny); break;
                    case "CloneContract": step.OnCloneContract().PostOperation().Sync().Handle(OnAny); break;
                    case "WinQuote": step.OnWinQuote().PostOperation().Sync().Handle(OnAny); break;
                    default: throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null);
                }
            }

            private void OnAny(ILocalPluginContext ctx)
            {
                SeenMessage = ctx.MessageName;
                string context = ctx.GetType().Name;
                SeenContext = context.Substring(0, context.IndexOf("PostContext", StringComparison.Ordinal));
            }
        }
    }
}
