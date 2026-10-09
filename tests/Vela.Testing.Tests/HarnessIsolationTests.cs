using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Context;
using Vela.Plugin;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // A-61: isolation between the test, the harness and plugin executions.
    public sealed class HarnessIsolationTests
    {
        [Fact]
        public void A_modelled_message_that_fails_in_the_store_fails_even_when_steps_are_registered()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<IsoMergeStep>();

            // Dataverse supports Merge only for account/contact/lead/incident: the store rejects a task merge, and the
            // registered Merge step no longer turns that failure into a silent success.
            Assert.Throws<InvalidPluginExecutionException>(() => pipeline.Service.Execute(new MergeRequest
            {
                Target = new EntityReference("task", Guid.NewGuid()),
                SubordinateId = Guid.NewGuid(),
                UpdateContent = new Entity("task"),
            }));
        }

        [Fact]
        public void A_step_reuses_one_plugin_instance_across_executions_like_the_platform()
        {
            IsoCountsExecutions.Seen.Clear();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<IsoCountsExecutions>();

            pipeline.Service.Create(new IsoRecord { Name = "a" });
            pipeline.Service.Create(new IsoRecord { Name = "b" });

            // The same instance ran both times, so state kept in a field leaks between executions, as in production.
            Assert.Equal(new[] { 1, 2 }, IsoCountsExecutions.Seen);
        }

        [Fact]
        public void A_custom_api_reuses_one_plugin_instance_across_calls()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<IsoCountingApi>();

            pipeline.Service.Execute(new OrganizationRequest("vela_IsoCount"));
            OrganizationResponse second = pipeline.Service.Execute(new OrganizationRequest("vela_IsoCount"));

            Assert.Equal(2, second["Count"]);
        }

        [Fact]
        public void The_context_builders_system_service_over_a_secured_store_is_not_subject_to_security()
        {
            Guid user = Guid.NewGuid();
            Guid bu = Guid.NewGuid();
            InMemoryOrganizationService store = new InMemoryOrganizationService()
                .WithCurrentUser(user)
                .WithSecurity(s => s.BusinessUnit(bu).Role("none", r => { }).User(user, bu, "none"));

            new PluginContextBuilder().WithOrganizationService(store).Execute(new CreatingPlugin());

            // CreatingPlugin writes through ctx.SystemService: SYSTEM is not bound by the user's (empty) role.
            Assert.Single(store.GetAll("account"));
            // The user service still is.
            Assert.ThrowsAny<Exception>(() => store.Create(new Entity("account")));
        }
    }

    [EntityLogicalName(LogicalNameValue)]
    public sealed class IsoRecord : Entity
    {
        public const string LogicalNameValue = "vela_isorecord";

        public IsoRecord() : base(LogicalNameValue) { }

        [AttributeLogicalName("vela_name")]
        public string? Name
        {
            get => GetAttributeValue<string>("vela_name");
            set => SetAttributeValue("vela_name", value);
        }
    }

    public sealed class IsoMergeStep : EntityPluginBase<IsoRecord>
    {
        public IsoMergeStep(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnMessage<MergeRequest>().PostOperation().Sync().Handle(ctx => { });
    }

    public sealed class IsoCountsExecutions : EntityPluginBase<IsoRecord>
    {
        public static readonly List<int> Seen = new List<int>();
        private int _executions;

        public IsoCountsExecutions(string u, string s) : base(u, s) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(ctx => Seen.Add(++_executions));
    }

    public sealed class IsoCountingApi : Vela.CustomApi.CustomApiBase
    {
        private int _calls;

        public IsoCountingApi(string u, string s) : base(u, s) { }

        protected override void RegisterCustomApi() => RegisterCustomApi("vela_IsoCount", nameof(Handle)).AsAction();

        private void Handle(ILocalPluginContext ctx) => ctx.SetOutputParameter("Count", ++_calls);
    }
}
