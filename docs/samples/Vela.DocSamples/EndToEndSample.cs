// The plugin and test of the end-to-end example (docs/guides/end-to-end.md). Edit the region, not the page.
namespace Vela.DocSamples.EndToEnd
{
    using System;
    using Vela.DocSamples.Models;
    using Vela.Plugin;
    using Vela.Testing;
    using Xunit;

    #region plugin
    public sealed partial class AccountPostUpdate(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)          // fire only when Name is sent
                .WithPreImage(a => a.Name)
                .Handle(OnAccountUpdated);

        private void OnAccountUpdated(OnAccountUpdatedContext ctx) =>
            ctx.Trace("Account renamed from '{0}' to '{1}'", ctx.PreImage?.Name, ctx.Request.Target.Name);
    }
    #endregion

    public sealed class AccountPostUpdateTests
    {
        #region test
        [Fact]
        public void Traces_The_Old_And_New_Name()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountPostUpdate>();
            Guid id = pipeline.Service.Create(new Account { Name = "Zava" });

            pipeline.Service.Update(new Account { Id = id, Name = "Zava Ltd" });

            Assert.Contains("Account renamed from 'Zava' to 'Zava Ltd'", pipeline.Trace.Output);
        }
        #endregion
    }
}
