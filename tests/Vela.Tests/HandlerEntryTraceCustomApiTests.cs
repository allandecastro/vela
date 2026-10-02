using Vela.Context;
using Vela.CustomApi;
using Vela.Testing;
using Xunit;

namespace Vela.Tests
{
    public sealed class HandlerEntryTraceCustomApiTests
    {
        // Method-based Custom API: dispatched through CustomApiBase.ExecuteRegisteredHandler, which names the
        // handler the same way plugin steps do.
        private sealed class GreetApi : CustomApiBase
        {
            public GreetApi() : base(string.Empty, string.Empty)
            {
            }

            protected override void RegisterCustomApi() => RegisterCustomApi("vela_Greet", nameof(Handle));

            private void Handle(ILocalPluginContext ctx) => ctx.SetOutputParameter("Message", "hi");
        }

        [Fact]
        public void Traces_the_custom_api_handler_name_on_entry()
        {
            PluginContextBuilder builder = PluginContext.ForMessage("vela_Greet");
            LocalPluginContext context = builder.Build();

            new GreetApi().Execute(context.ServiceProvider);

            Assert.Contains("Entering handler 'Handle' for message 'vela_Greet'.", builder.TraceCapture.Traces);
        }
    }
}
