// The sample of the docs home page (docs/index.md). Edit the regions, not the page.
namespace Vela.DocSamples
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.DocSamples.Models;
    using Vela.Plugin;
    using Vela.Testing;
    using Xunit;

    #region plugin
    public sealed partial class AccountValidator(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(ctx =>
                {
                    if (string.IsNullOrWhiteSpace(ctx.Request.Target.Name))
                    {
                        throw new InvalidPluginExecutionException("Account name is required.");
                    }
                });
    }
    #endregion

    public sealed class AccountValidatorTests
    {
        #region test
        [Fact]
        public void Rejects_An_Empty_Name()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountValidator>();
            Guid id = pipeline.Service.Create(new Account { Name = "Zava" });

            Assert.Throws<InvalidPluginExecutionException>(() =>
                pipeline.Service.Update(new Account { Id = id, Name = "" }));
        }
        #endregion
    }
}
