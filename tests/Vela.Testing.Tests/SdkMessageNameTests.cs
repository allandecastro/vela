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

    public sealed class SmPermissiveSend : EntityPluginBase<SmEmail>
    {
        public SmPermissiveSend(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage(Vela.Constants.PluginMessages.SendEmail).PostOperation().Sync()
                .Handle((ILocalPluginContext ctx) => SmSeen.Calls.Enqueue($"permissive:{ctx.MessageName}"));
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

        [Fact]
        public void OnMessage_with_a_request_name_registers_on_the_sdkmessage()
        {
            SmSeen.Drain();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SmPermissiveSend>();

            pipeline.Service.Execute(new SendEmailRequest { EmailId = Guid.NewGuid(), IssueSend = false, TrackingToken = string.Empty });

            Assert.Equal(new[] { "permissive:Send" }, SmSeen.Calls.ToArray());
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
