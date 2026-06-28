using System;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.Testing;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests
{
    public sealed class ExceptionMappingTests
    {
        [Fact]
        public void PluginBase_MapsBusinessExceptionToCleanInvalidPluginExecutionException()
        {
            ThrowingPlugin plugin = new ThrowingPlugin();
            IServiceProvider provider = new PluginContextBuilder().BuildServiceProvider();

            Action act = () => plugin.Execute(provider);
            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(act);
            Assert.Equal("Clean business message.", exception.Message);
        }

        [Fact]
        public void CustomApi_HandlerBusinessException_PropagatesUnwrapped()
        {
            // The lightweight Custom API handler is invoked via reflection. Its business exception must surface
            // unwrapped (not enclosed in a TargetInvocationException), otherwise the Dataverse error mapping breaks.
            ThrowingCustomApi api = new ThrowingCustomApi();
            LocalPluginContext context = PluginContext.ForMessage("zava_Throw").Build();

            Action act = () => api.Execute(context.ServiceProvider);
            InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(act);
            Assert.Equal("Clean business message.", exception.Message);
        }

        private sealed class ThrowingPlugin : PluginBase
        {
            public ThrowingPlugin()
                : base(string.Empty, string.Empty)
            {
            }

            protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
            {
                throw new PluginBusinessException("Clean business message.");
            }
        }

        private sealed class ThrowingCustomApi : CustomApiBase
        {
            public ThrowingCustomApi()
                : base(string.Empty, string.Empty)
            {
            }

            protected override void RegisterCustomApi()
            {
                RegisterCustomApi("zava_Throw", nameof(Handle));
            }

            private void Handle(ILocalPluginContext ctx)
            {
                throw new PluginBusinessException("Clean business message.");
            }
        }
    }
}
