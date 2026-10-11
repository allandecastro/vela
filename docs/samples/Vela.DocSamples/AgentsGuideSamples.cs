// Compiled copies of the snippets in the AGENTS.md that `vela init` scaffolds
// (ScaffoldTemplates.AgentsGuide in src/Vela.Cli). Keep the two in sync: when a snippet changes here, change it
// there too.
namespace Vela.DocSamples
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.DocSamples.Models;
    using Vela.Messages;
    using Vela.Plugin;
    using Vela.Testing;
    using Xunit;

    // ---- A plugin with two steps on one table ----
    public sealed partial class AccountCreditPlugin(string unsecureConfig, string secureConfig)
        : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.CreditLimit)          // required on Create/Update (VELA020)
                .WithPreImage(a => a.CreditLimit)      // only the columns you read
                .Handle(OnCreditLimitChanging);

            RegisterStep().OnUpdate().PostOperation().Async()
                .FilterOn(a => a.CreditLimit)
                .WithStepKey("credit-review")          // needed only when two steps on one trigger look identical
                .Handle(OnCreditLimitChanged);
        }

        // OnCreditLimitChangingContext is generated (the class must be partial): PreImage holds the captured columns.
        private void OnCreditLimitChanging(OnCreditLimitChangingContext ctx)
        {
            decimal before = ctx.PreImage.CreditLimit?.Value ?? 0m;
            decimal after = ctx.Request.Target.CreditLimit?.Value ?? 0m;
            if (before > 0 && after > before * 2)
            {
                throw new InvalidPluginExecutionException("The credit limit cannot more than double at once.");
            }
        }

        // An async step runs after the commit: write through a service, never to the Target (VELA018).
        private void OnCreditLimitChanged(IUpdatePostContext<Account> ctx) =>
            ctx.Service.Update(new Account { Id = ctx.PrimaryEntityId, Description = "Credit limit reviewed." });
    }

    // ---- A typed Custom API ----
    public sealed class EstimateValueApi(string unsecureConfig, string secureConfig)
        : CustomApiBase<EstimateValueRequest, EstimateValueResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_EstimateValue")
                .DisplayName("Estimate value")
                .Description("Estimates the value of an account.")
                .AsFunction()
                .Request(r => r
                    .Add(x => x.AccountId)                                       // required by default
                    .Add(x => x.Discount, optional: true, "Discount to apply."))
                .Response(r => r.Add(x => x.Value));                             // a function needs a response
        }

        protected override EstimateValueResponse Execute(ILocalPluginContext ctx, EstimateValueRequest request) =>
            new EstimateValueResponse { Value = 1000m - request.Discount };
    }

    public sealed class EstimateValueRequest : CustomApiRequest
    {
        public EstimateValueRequest() : base("zava_EstimateValue") { }

        public Guid AccountId { get => Get<Guid>(); set => Set(value); }

        public decimal Discount { get => Get<decimal>(); set => Set(value); }
    }

    public sealed class EstimateValueResponse : CustomApiResponse
    {
        public EstimateValueResponse() : base("zava_EstimateValue") { }

        public decimal Value { get => Get<decimal>(); set => Set(value); }
    }

    // ---- Tests ----
    public sealed class AgentsGuideSampleTests
    {
        [Fact]
        public void Rejects_A_Credit_Limit_That_More_Than_Doubles()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountCreditPlugin>();
            Guid id = pipeline.Service.Create(new Account { Name = "Zava", CreditLimit = new Money(100m) });

            Assert.Throws<InvalidPluginExecutionException>(() =>
                pipeline.Service.Update(new Account { Id = id, CreditLimit = new Money(500m) }));
        }

        [Fact]
        public void Reviews_A_Credit_Limit_Change_After_The_Commit()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<AccountCreditPlugin>();
            Guid id = pipeline.Service.Create(new Account { Name = "Zava", CreditLimit = new Money(100m) });

            pipeline.Service.Update(new Account { Id = id, CreditLimit = new Money(150m) });

            Entity stored = Assert.Single(pipeline.GetAll(Account.EntityLogicalNameValue));
            Assert.Equal("Credit limit reviewed.", stored.GetAttributeValue<string>("description"));
        }

        [Fact]
        public void Estimates_A_Value()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().Register<EstimateValueApi>();

            EstimateValueResponse response = pipeline.Service
                .Execute(new EstimateValueRequest { AccountId = Guid.NewGuid(), Discount = 100m })
                .ToResponse<EstimateValueResponse>();

            Assert.Equal(900m, response.Value);
        }
    }
}
