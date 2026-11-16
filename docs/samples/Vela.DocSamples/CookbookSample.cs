// The recipes of the cookbook (docs/guides/cookbook.md). Edit the regions, not the page.
namespace Vela.DocSamples.Cookbook
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.DI;
    using Vela.DocSamples.Models;
    using Vela.Plugin;
    using Vela.Testing;
    using Xunit;

    #region single-step
    public sealed partial class AccountPostUpdate(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Handle(OnAccountUpdated);

        private void OnAccountUpdated(OnAccountUpdatedContext ctx)
        {
            Account target = ctx.Request.Target;   // the columns the caller sent
            ctx.Trace("Account {0} renamed to '{1}'", target.Id, target.Name);
        }
    }
    #endregion

    public sealed partial class AccountRouter(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        #region multi-step
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnCreated);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Handle(OnUpdated);
        }
        #endregion

        private void OnCreated(OnCreatedContext ctx) => ctx.Trace("Created {0}", ctx.Request.Target.Name);

        private void OnUpdated(OnUpdatedContext ctx) => ctx.Trace("Updated {0}", ctx.Request.Target.Name);
    }

    public sealed partial class AccountCreditCheck(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.CreditLimit)
                .Handle(OnCreditLimitChanged);

        #region typed-read
        private void OnCreditLimitChanged(OnCreditLimitChangedContext ctx)
        {
            // Read the columns the handler needs, early-bound (Vela.Context).
            Account saved = ctx.Service.Retrieve<Account>(ctx.PrimaryEntityId, a => a.Name, a => a.CreditLimit);
            ctx.Trace("{0} now has a credit limit of {1}", saved.Name, saved.CreditLimit?.Value);
        }
        #endregion
    }

    #region custom-api
    public sealed class RecalculateApi(string unsecureConfig, string secureConfig)
        : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi() =>
            RegisterCustomApi("zava_Recalculate", Recalculate)
                .Description("Recalculates the bound account.")
                .AsAction()
                .BoundTo<Account>();

        private void Recalculate(ILocalPluginContext ctx) => ctx.Trace("Recalculating {0}", ctx.PrimaryEntityId);
    }
    #endregion

    public interface IPricingService
    {
        decimal PriceOf(Guid accountId);
    }

    public sealed class PricingService(IOrganizationService service) : IPricingService
    {
        public decimal PriceOf(Guid accountId) =>
            service.Retrieve<Account>(accountId, a => a.CreditLimit).CreditLimit?.Value ?? 0m;
    }

    public sealed partial class AccountPricing(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        #region services
        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context) =>
            services.AddScoped<IPricingService>(r => new PricingService(r.GetRequiredService<IOrganizationService>()));
        #endregion

        protected override void RegisterPluginSteps() =>
            RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.CreditLimit)
                .Handle(OnPriced);

        #region resolve
        private void OnPriced(OnPricedContext ctx)
        {
            IPricingService pricing = ctx.GetRequiredService<IPricingService>();
            ctx.Trace("Price: {0}", pricing.PriceOf(ctx.PrimaryEntityId));
        }
        #endregion
    }

    public sealed class AccountPostUpdateTests
    {
        #region test
        [Fact]
        public void Fires_On_A_Rename()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountPostUpdate>();
            Guid id = pipeline.Service.Create(new Account { Name = "Zava" });

            pipeline.Service.Update(new Account { Id = id, Name = "Zava Ltd" });

            Assert.Contains(pipeline.ExecutionLog, e => e.PluginTypeName.Contains(nameof(AccountPostUpdate)));
            Assert.Equal("Zava Ltd", pipeline.Service.Retrieve<Account>(id, a => a.Name).Name);
        }
        #endregion
    }
}
