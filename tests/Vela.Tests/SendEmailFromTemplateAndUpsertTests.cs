using System;
using Vela.Constants;
using Vela.Context;
using Vela.Messages;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests
{
    // Loose ends of the typed-requests design: SendEmailFromTemplate now has an early-bound ctx.Request.Target
    // (+ typed request properties), and Upsert has a Vela-owned typed request reachable via On<UpsertRequest<T>>().
    public sealed class SendEmailFromTemplateAndUpsertTests
    {
        private sealed class SefProbe : EntityPluginBase<TestAccount>
        {
            public string? TargetName;
            public Guid TemplateId;

            public SefProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().OnSendEmailFromTemplate().PostOperation().Sync().Handler(OnSent);

            private void OnSent(ISendEmailFromTemplatePostContext<TestAccount> ctx)
            {
                TargetName = ctx.Request.Target?.Name;      // early-bound TEntity
                TemplateId = ctx.Request.TemplateId;        // typed request property
            }
        }

        private sealed class UpsertProbe : EntityPluginBase<TestAccount>
        {
            public string? TargetName;

            public UpsertProbe() : base(string.Empty, string.Empty) { }

            protected override void RegisterPluginSteps()
                => RegisterSteps().On<UpsertRequest<TestAccount>>().PostOperation().Sync().Handler(OnUpsert);

            private void OnUpsert(IMessagePostContext<UpsertRequest<TestAccount>> ctx)
                => TargetName = ctx.Request.Target?.Name;   // early-bound TEntity, message "Upsert" from RequestName
        }

        [Fact]
        public void SendEmailFromTemplate_exposes_early_bound_target_and_typed_properties()
        {
            SefProbe probe = new SefProbe();
            Guid template = Guid.NewGuid();

            PluginContext.For<TestAccount>()
                .OnMessage("SendEmailFromTemplate")
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Name = "Acme" })
                .WithInputParameter("TemplateId", template)
                .Execute(probe);

            Assert.Equal("Acme", probe.TargetName);
            Assert.Equal(template, probe.TemplateId);
        }

        [Fact]
        public void Upsert_via_On_typed_request_exposes_early_bound_target()
        {
            UpsertProbe probe = new UpsertProbe();

            PluginContext.For<TestAccount>()
                .OnMessage("Upsert")
                .Stage(PluginStages.PostOperation)
                .Mode(ExecutionMode.Synchronous)
                .WithTarget(new TestAccount { Name = "Zava" })
                .Execute(probe);

            Assert.Equal("Zava", probe.TargetName);
        }
    }
}
