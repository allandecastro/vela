using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.Steps;

namespace ContractPluginFixture
{
    // Every value below is pinned by tests/contracts/plugin-contract.golden.json: change one, change the golden file.
    public sealed class AccountContractPlugin : EntityPluginBase<ContractAccount>
    {
        public static readonly Guid IntegrationUserId = new Guid("6f1c2a52-0d3b-4f5e-9a41-2b7c8d9e0f11");

        public AccountContractPlugin(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterPluginSteps()
        {
            // Explicit key and rank, a pre-image with the default alias, attributes declared out of order.
            RegisterStep().OnUpdate().PreOperation().Sync()
                .FilterOn(a => a.Telephone1, a => a.Name)
                .WithStepKey("account-normalize")
                .WithExecutionOrder(5)
                .WithPreImage(a => a.Name)
                .Handle(_ => { });

            // Async, impersonated, both configs, named images on both sides.
            RegisterStep().OnUpdate().PostOperation().Async(deleteJobOnSuccess: true)
                .FilterOn(a => a.Revenue)
                .WithStepKey("account-sync")
                .WithDescription("Pushes revenue changes to the ERP.")
                .WithImpersonation(IntegrationUserId)
                .WithUnsecureConfig("{\"endpoint\":\"https://erp.example\"}")
                .WithSecureConfigFrom("ContractErpKey")
                .WithPreImage("Before", a => a.Revenue, a => a.Name)
                .WithPostImage("After", a => a.Revenue)
                .Handle(_ => { });

            // No step key (the framework derives one), disabled, offline + server deployment, a post-image.
            RegisterStep().OnCreate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .Disabled()
                .WithSupportedDeployment(SupportedDeployment.Both)
                .WithPostImage(a => a.Name, a => a.Telephone1)
                .Handle(_ => { });

            // PreValidation: no filter, no image; a runtime-only depth limit (not deployed).
            RegisterStep().OnDelete().PreValidation().Sync()
                .WithStepKey("account-guard-delete")
                .SkipWhenDepthAbove(2)
                .Handle(_ => { });

            // Two steps on one trigger (Update, PostOperation, Sync) told apart by their filters, neither keyed: the
            // framework derives a distinct key for each, and the rank is not part of it.
            RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.CreditLimit)
                .WithExecutionOrder(3)
                .Handle(_ => { });
            RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Telephone1)
                .WithPostImage(a => a.Telephone1)
                .Handle(_ => { });
        }
    }

    public sealed class ContactContractPlugin : EntityPluginBase<ContractContact>
    {
        public ContactContractPlugin(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterPluginSteps()
        {
            // A global message: registered with no primary entity, whatever TEntity is.
            RegisterStep().OnAssociate().PostOperation().Sync()
                .WithStepKey("contact-associate")
                .ForRelationship("contact_customer_accounts")
                .WithExecutionOrder(2)
                .Handle(_ => { });

            // A global Custom API (its request marked [GlobalCustomApi]): registered with no primary entity, whatever
            // TEntity is, as the API's only message filter has no table.
            RegisterStep().On<PingRequest>().PostOperation().Async()
                .WithStepKey("contact-ping")
                .Handle(_ => { });
        }
    }

    // Lightweight global Custom API that allows steps of both modes.
    public sealed class PingApi : CustomApiBase
    {
        public PingApi(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("contract_Ping", _ => { })
                .AsAction()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync);
        }
    }

    // Typed Custom API: bound action, explicit parameters (required by default, one optional), workflow-enabled.
    public sealed class ScoreAccountApi : CustomApiBase<ScoreAccountRequest, ScoreAccountResponse>
    {
        public ScoreAccountApi(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("contract_ScoreAccount")
                .DisplayName("Score account")
                .Description("Scores an account.")
                .AsAction()
                .BoundTo("account")
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .WorkflowSdkStep(true)
                .Request(r => r
                    .Add(x => x.Reason)
                    .Add(x => x.Weight)
                    .Add(x => x.Strict)
                    .Add(x => x.CorrelationKey)
                    .Add(x => x.AsOf, optional: true)
                    .Add(x => x.Budget)
                    .Add(x => x.Factor)
                    .Add(x => x.Tier))
                .Response(r => r
                    .Add(x => x.Score)
                    .Add(x => x.Explanation));
        }

        protected override ScoreAccountResponse Execute(ILocalPluginContext ctx, ScoreAccountRequest request)
            => new ScoreAccountResponse { Score = request.Weight };
    }

    // Lightweight Custom API: global private function with an execute privilege and the entity-typed parameters.
    public sealed class ExportContactsApi : CustomApiBase
    {
        public ExportContactsApi(string unsecureConfig, string secureConfig) : base(unsecureConfig, secureConfig) { }

        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("contract_ExportContacts", _ => { })
                .AsFunction()
                .Private()
                .ExecutePrivilege("prvReadContact")
                .RequestParameter<EntityReference>("Owner", logicalEntityName: "systemuser")
                .RequestParameter<string[]>("Tags", optional: true)
                .RequestParameter<EntityCollection>("Seed", optional: true, logicalEntityName: "contact")
                .RequestParameter<double>("Ratio", optional: true)
                .ResponseParameter<Entity>("First", logicalEntityName: "contact")
                .ResponseParameter<EntityCollection>("Rows")
                .ResponseParameter<string[]>("Warnings");
        }
    }
}
