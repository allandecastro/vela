using Microsoft.Xrm.Sdk;
using Vela.Plugin;
using Vela.Steps;
using Xunit;

namespace Vela.Testing.Tests
{
    // A step whose handler is a lambda that captures a LOCAL variable. The C# compiler emits such a lambda as a
    // method on a hidden closure display class, not on the plugin type. The old name-based dispatch resolved the
    // handler by looking up a method of that name on the plugin type, so this handler could not be found. Now the
    // framework retains and invokes the actual delegate, so it runs.
    public sealed class LambdaLocalCaptureOnCreate : EntityPluginBase<FakeAccount>
    {
        public LambdaLocalCaptureOnCreate(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
        {
            string subject = "captured-local"; // captured by the lambda below -> hoisted to a closure class
            RegisterStep().OnCreate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(ctx => ctx.SystemService.Create(new Entity("task") { ["subject"] = subject }));
        }
    }

    public sealed class LambdaHandlerDispatchTests
    {
        [Fact]
        public void Lambda_handler_capturing_a_local_executes()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<LambdaLocalCaptureOnCreate>();

            pipeline.Service.Create(new FakeAccount { Name = "Zava" });

            Entity task = Assert.Single(pipeline.GetAll("task"));
            Assert.Equal("captured-local", task.GetAttributeValue<string>("subject"));
        }
    }
}
