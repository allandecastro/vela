using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // #461: a typed trigger is named after the SDK request, but the step registers on the sdkmessage, which differs
    // for some business messages (SendEmail runs as Send, WinQuote as Win). Verified live on 2026-10-09: the plugin
    // sees the sdkmessage as MessageName, and the closing activity's lookup as PrimaryEntityName.

    [EntityLogicalName("incident")]
    public sealed class SmIncident : Entity
    {
        public SmIncident() : base("incident") { }
    }

    [EntityLogicalName("opportunity")]
    public sealed class SmOpportunity : Entity
    {
        public SmOpportunity() : base("opportunity") { }
    }

    [EntityLogicalName("quote")]
    public sealed class SmQuote : Entity
    {
        public SmQuote() : base("quote") { }
    }

    [EntityLogicalName("email")]
    public sealed class SmEmail : Entity
    {
        public SmEmail() : base("email") { }
    }

    public static class SmSeen
    {
        public static readonly ConcurrentQueue<string> Calls = new ConcurrentQueue<string>();

        public static void Drain()
        {
            while (Calls.TryDequeue(out _))
            {
            }
        }
    }

    public sealed class SmEmailSent : EntityPluginBase<SmEmail>
    {
        public SmEmailSent(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnSendEmail().PostOperation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"{ctx.MessageName}:{ctx.Request.EmailId}"));
    }

    public sealed class SmIncidentClosed : EntityPluginBase<SmIncident>
    {
        public SmIncidentClosed(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCloseIncident().PostOperation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"{ctx.MessageName}:{ctx.PrimaryEntityName}:{ctx.Request.Status.Value}"));
    }

    public sealed class SmOpportunityWon : EntityPluginBase<SmOpportunity>
    {
        public SmOpportunityWon(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnWinOpportunity().PostOperation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"WinOpportunity:{ctx.MessageName}:{ctx.PrimaryEntityName}"));
    }

    public sealed class SmQuoteWon : EntityPluginBase<SmQuote>
    {
        public SmQuoteWon(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnWinQuote().PostOperation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"WinQuote:{ctx.MessageName}:{ctx.PrimaryEntityName}"));
    }

    public sealed class SmPermissiveClose : EntityPluginBase<SmQuote>
    {
        public SmPermissiveClose(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<CloseQuoteRequest>().PostOperation().Sync()
                .Handle((ILocalPluginContext ctx) => SmSeen.Calls.Enqueue($"permissive:{ctx.MessageName}:{ctx.PrimaryEntityName}"));
    }

    [EntityLogicalName("fax")]
    public sealed class SmFax : Entity
    {
        public SmFax() : base("fax") { }
    }

    [EntityLogicalName("template")]
    public sealed class SmTemplate : Entity
    {
        public SmTemplate() : base("template") { }
    }

    public sealed class SmFaxSent : EntityPluginBase<SmFax>
    {
        public SmFaxSent(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnSendFax().PostOperation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"SendFax:{ctx.MessageName}:{ctx.PrimaryEntityName}:{ctx.Request.FaxId}"));
    }

    public sealed class SmTemplateSent : EntityPluginBase<SmTemplate>
    {
        public SmTemplateSent(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnSendTemplate().PostOperation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"SendTemplate:{ctx.MessageName}:{ctx.PrimaryEntityName}:{ctx.Request.TemplateId}"));
    }

    // UnlockSalesOrderPricing runs on the "none" sdkmessagefilter, so the step registers on no table.
    public sealed class SmPricingUnlocked : EntityPluginBase<SmQuote>
    {
        public SmPricingUnlocked(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUnlockSalesOrderPricing().PostOperation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"{ctx.MessageName}:{ctx.PrimaryEntityName}:{ctx.Request.SalesOrderId}"));
    }

    [EntityLogicalName("goal")]
    public sealed class SmGoal : Entity
    {
        public SmGoal() : base("goal") { }
    }

    [EntityLogicalName("queueitem")]
    public sealed class SmQueueItem : Entity
    {
        public SmQueueItem() : base("queueitem") { }
    }

    public sealed class SmGoalRecalculated : EntityPluginBase<SmGoal>
    {
        public SmGoalRecalculated(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnRecalculate().PreValidation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"{ctx.MessageName}:{ctx.PrimaryEntityName}:{ctx.PrimaryEntityId}"));
    }

    public sealed class SmQueued : EntityPluginBase<SmQueueItem>
    {
        public SmQueued(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnAddToQueue().PreValidation().Sync()
                .Handle(ctx => SmSeen.Calls.Enqueue($"{ctx.MessageName}:{ctx.PrimaryEntityName}:{ctx.PrimaryEntityId}"));
    }

    public sealed class SdkMessageNameTests
    {
        [Fact]
        public void An_executed_SendEmailRequest_runs_the_Send_steps()
        {
            SmSeen.Drain();
            Guid emailId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmEmailSent>();

            pipeline.Service.Execute(new SendEmailRequest { EmailId = emailId, IssueSend = false, TrackingToken = string.Empty });

            Assert.Equal(new[] { $"Send:{emailId}" }, SmSeen.Calls.ToArray());
            Assert.Equal("Send", Assert.Single(pipeline.ExecutionLog).Message);
        }

        // CloseQuote has no typed trigger, so the request type is the way to register it; it runs as Close, like
        // CloseIncident.
        [Fact]
        public void OnMessage_with_a_request_type_registers_on_the_sdkmessage()
        {
            SmSeen.Drain();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmPermissiveClose>();

            pipeline.Service.Execute(new CloseQuoteRequest
            {
                QuoteClose = new Entity("quoteclose") { ["quoteid"] = new EntityReference("quote", Guid.NewGuid()) },
                Status = new OptionSetValue(5),
            });

            Assert.Equal(new[] { "permissive:Close:quote" }, SmSeen.Calls.ToArray());
        }

        // Verified live: a table-bound message gets its Target's id as PrimaryEntityId only when the Target is a
        // record of that table (Recalculate on goal); AddToQueue runs on queueitem, so its email Target gives none.
        [Fact]
        public void A_table_bound_message_takes_the_id_of_a_Target_on_its_own_table_only()
        {
            SmSeen.Drain();
            Guid goalId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmGoalRecalculated>().Register<SmQueued>();

            pipeline.Service.Execute(new Microsoft.Crm.Sdk.Messages.RecalculateRequest { Target = new EntityReference("goal", goalId) });
            pipeline.Service.Execute(new AddToQueueRequest { Target = new EntityReference("email", Guid.NewGuid()), DestinationQueueId = Guid.NewGuid() });

            Assert.Equal(new[] { $"Recalculate:goal:{goalId}", $"AddToQueue:queueitem:{Guid.Empty}" }, SmSeen.Calls.ToArray());
        }

        [Fact]
        public void A_global_message_runs_its_step_on_no_table()
        {
            SmSeen.Drain();
            Guid orderId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmPricingUnlocked>();

            pipeline.Service.Execute(new UnlockSalesOrderPricingRequest { SalesOrderId = orderId });

            Assert.Equal(new[] { $"UnlockSalesOrderPricing:none:{orderId}" }, SmSeen.Calls.ToArray());
        }

        // SendEmail, SendFax and SendTemplate all run as Send; each request fires only the step of its own table.
        [Fact]
        public void Send_runs_only_the_step_of_the_sent_request()
        {
            SmSeen.Drain();
            Guid faxId = Guid.NewGuid();
            Guid templateId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmEmailSent>().Register<SmFaxSent>().Register<SmTemplateSent>();

            pipeline.Service.Execute(new SendFaxRequest { FaxId = faxId, IssueSend = false });
            pipeline.Service.Execute(new SendTemplateRequest
            {
                TemplateId = templateId,
                Sender = new EntityReference("systemuser", Guid.NewGuid()),
                RecipientType = "contact",
                RecipientIds = new[] { Guid.NewGuid() },
                RegardingType = "contact",
                RegardingId = Guid.NewGuid(),
            });

            Assert.Equal(new[] { $"SendFax:Send:fax:{faxId}", $"SendTemplate:Send:template:{templateId}" }, SmSeen.Calls.ToArray());
            Assert.All(pipeline.ExecutionLog, entry => Assert.Equal("Send", entry.Message));
        }

        // The IncidentResolution activity is an Entity parameter; the primary record is its incidentid, so an
        // incident-bound step fires (live: PrimaryEntityName is incident).
        [Fact]
        public void An_executed_CloseIncidentRequest_runs_the_incident_Close_steps()
        {
            SmSeen.Drain();
            Guid incidentId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmIncidentClosed>();

            pipeline.Service.Execute(new CloseIncidentRequest
            {
                IncidentResolution = new Entity("incidentresolution") { ["incidentid"] = new EntityReference("incident", incidentId) },
                Status = new OptionSetValue(5),
            });

            Assert.Equal(new[] { "Close:incident:5" }, SmSeen.Calls.ToArray());
        }

        // WinOpportunity and WinQuote share the Win sdkmessage; the primary entity tells them apart.
        [Fact]
        public void Win_runs_only_the_step_of_the_won_entity()
        {
            SmSeen.Drain();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmOpportunityWon>().Register<SmQuoteWon>();

            pipeline.Service.Execute(new WinQuoteRequest
            {
                QuoteClose = new Entity("quoteclose") { ["quoteid"] = new EntityReference("quote", Guid.NewGuid()) },
                Status = new OptionSetValue(4),
            });
            pipeline.Service.Execute(new WinOpportunityRequest
            {
                OpportunityClose = new Entity("opportunityclose") { ["opportunityid"] = new EntityReference("opportunity", Guid.NewGuid()) },
                Status = new OptionSetValue(3),
            });

            Assert.Equal(new[] { "WinQuote:Win:quote", "WinOpportunity:Win:opportunity" }, SmSeen.Calls.ToArray());
        }
    }
}
