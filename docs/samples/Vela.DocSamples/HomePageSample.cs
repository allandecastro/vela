// The sample of the docs home page (docs/index.md) and the quickstart. Edit the regions, not the pages.
namespace Vela.DocSamples
{
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
            Assert.Throws<InvalidPluginExecutionException>(() =>
                PluginContext.For<Account>()
                    .WithTarget(new Account { Name = "" })
                    .Execute<AccountValidator>());
        }
        #endregion
    }
}
