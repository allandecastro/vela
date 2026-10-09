using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    public sealed class TelemetryUpdatePlugin : EntityPluginBase<FakeAccount>
    {
        public TelemetryUpdatePlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<FakeAccount> ctx)
        {
            ctx.LogWarning("renamed");
            ctx.LogMetric("Renames", 1, new Dictionary<string, object?> { ["Source"] = "test" });
        }
    }

    // The pipeline serves the platform's Application Insights logger type, like the sandbox, so ctx.Log* and
    // ctx.LogMetric reach LogCapture across the cascade.
    public sealed class PipelineTelemetryTests
    {
        [Fact]
        public void Logs_and_metrics_from_a_step_reach_the_pipeline_log_capture()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<TelemetryUpdatePlugin>()
                .Seed(new FakeAccount { Id = id, Name = "Old" });

            pipeline.Service.Update(new FakeAccount { Id = id, Name = "New" });

            LogEntry warning = Assert.Single(pipeline.LogCapture.Entries, e => e.Level == LogLevel.Warning);
            Assert.Equal("renamed", warning.Message);
            Assert.Equal("OnUpdated", warning.Scope["Method"]);
            MetricEntry metric = Assert.Single(pipeline.LogCapture.Metrics);
            Assert.Equal("Renames", metric.Name);
            Assert.Equal("test", metric.Dimensions["Source"]);
            Assert.Equal("Update", metric.Dimensions["MessageName"]);
        }
    }
}
