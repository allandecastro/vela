using System;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // Default behaviour: the runtime traces which handler fired, by name, complementing the plugin-level
    // "Executing ..." line (a plugin can register several steps, so the name is the missing dimension).
    public sealed class TracedUpdatePlugin : EntityPluginBase<FakeAccount>
    {
        public TracedUpdatePlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx) { }
    }

    // Opt-out: overriding AutoTraceHandlerEntry silences the handler-entry line.
    public sealed class SilentUpdatePlugin : EntityPluginBase<FakeAccount>
    {
        public SilentUpdatePlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override bool AutoTraceHandlerEntry => false;

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx) { }
    }

    public sealed class HandlerEntryTraceTests
    {
        [Fact]
        public void Traces_the_handler_name_on_entry_by_default()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TracedUpdatePlugin>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            Assert.Contains("Entering handler 'OnUpdated' for message 'Update'.", pipeline.Trace.Traces);
        }

        [Fact]
        public void Does_not_trace_the_handler_when_opted_out()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<SilentUpdatePlugin>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            Assert.DoesNotContain(pipeline.Trace.Traces, t => t.Contains("Entering handler"));
        }
    }
}
