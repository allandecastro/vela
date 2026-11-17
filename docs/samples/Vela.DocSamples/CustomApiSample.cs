// The samples of the Custom API guide (docs/guides/custom-apis.md). Edit the regions, not the page.
namespace Vela.DocSamples
{
    using Microsoft.Xrm.Sdk;
    using Vela.DocSamples.Models;
    #region typed
    using System;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;

    public sealed class CalculateAccountScore(string unsecureConfig, string secureConfig)
        : CustomApiBase<ScoreRequest, ScoreResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_CalculateAccountScore")
                .DisplayName("Calculate Account Score")
                .Description("Calculates an account score.")
                .AsAction()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r
                    .Add(x => x.AccountId)
                    .Add(x => x.AsOfDate, optional: true, "Date to score against; defaults to today."))
                .Response(r => r.Add(x => x.Score));
        }

        protected override ScoreResponse Execute(ILocalPluginContext ctx, ScoreRequest request)
            => new ScoreResponse { Score = request.AccountId == Guid.Empty ? 0 : 42 };
    }

    [GlobalCustomApi]   // a global API: a step declared from this request registers with no table
    public sealed class ScoreRequest : CustomApiRequest
    {
        public ScoreRequest() : base("zava_CalculateAccountScore") { }

        public Guid AccountId { get => Get<Guid>(); set => Set(value); }

        public DateTime AsOfDate { get => Get<DateTime>(); set => Set(value); }
    }

    public sealed class ScoreResponse : CustomApiResponse
    {
        public ScoreResponse() : base("zava_CalculateAccountScore") { }

        public int Score { get => Get<int>(); set => Set(value); }
    }
    #endregion

    public sealed class AssignContact(string unsecureConfig, string secureConfig)
        : CustomApiBase<AssignContactRequest, AssignContactResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_AssignContact")
                .AsAction()
                #region entity-parameter
                .Request(r => r
                    .Add(x => x.Owner, optional: false, logicalEntityName: "systemuser")
                    .Add(x => x.Contact))                     // [LogicalEntityName("contact")] on the property
                #endregion
                .Response(r => r.Add(x => x.Assigned));
        }

        protected override AssignContactResponse Execute(ILocalPluginContext ctx, AssignContactRequest request)
            => new AssignContactResponse { Assigned = true };
    }

    public sealed class AssignContactRequest : CustomApiRequest
    {
        public AssignContactRequest() : base("zava_AssignContact") { }

        public EntityReference Owner { get => Get<EntityReference>(); set => Set(value); }

        [LogicalEntityName("contact")]
        public EntityReference Contact { get => Get<EntityReference>(); set => Set(value); }
    }

    public sealed class AssignContactResponse : CustomApiResponse
    {
        public AssignContactResponse() : base("zava_AssignContact") { }

        public bool Assigned { get => Get<bool>(); set => Set(value); }
    }

    public sealed class CreateContactLight(string unsecureConfig, string secureConfig)
        : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_CreateContactLight", ctx => ctx.SetOutputParameter("Created", null))
                .AsAction()
                #region entity-parameter-light
                .RequestParameter<EntityReference>("Owner", logicalEntityName: "systemuser")
                .ResponseParameter<EntityReference>("Created", logicalEntityName: "contact")
                #endregion
                ;
        }
    }

    #region lightweight
    public sealed class CalculateAccountScoreLight(string unsecureConfig, string secureConfig)
        : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_CalculateAccountScore", ExecuteScore)
                .DisplayName("Calculate Account Score")
                .Description("Calculates an account score.")
                .AsAction()
                .RequestParameter<Guid>("AccountId")
                .ResponseParameter<int>("Score");
        }

        private void ExecuteScore(ILocalPluginContext ctx)
        {
            Guid accountId = ctx.GetInputParameter<Guid>("AccountId");
            ctx.SetOutputParameter("Score", accountId == Guid.Empty ? 0 : 42);
        }
    }
    #endregion

    #region global-step
    public sealed partial class AfterScore(string unsecureConfig, string secureConfig)
        : Vela.Plugin.EntityPluginBase<Account>(unsecureConfig, secureConfig)   // any table: the step takes none
    {
        protected override void RegisterPluginSteps()
            => RegisterStep().On<ScoreRequest>()    // ScoreRequest is [GlobalCustomApi]
                .PostOperation().Async()
                .Handle(OnScored);

        private void OnScored(OnScoredContext ctx)
        {
            var request = ctx.Request;                                   // ScoreRequest, typed
            string table = ctx.PluginExecutionContext.PrimaryEntityName; // "none"
        }
    }
    #endregion

    public static class CustomApiCallSample
    {
        public static int Call(IOrganizationService service, Guid accountId)
        {
            #region invoke
            var response = service.Execute(new ScoreRequest { AccountId = accountId }).ToResponse<ScoreResponse>();
            #endregion
            return response.Score;
        }
    }

    public sealed class CustomApiSampleTests
    {
        [Xunit.Fact]
        public void Lightweight_lambda_handler_runs()
        {
            Vela.Testing.PluginPipeline pipeline = Vela.Testing.PluginPipeline.Empty().Register<CreateContactLight>();

            OrganizationResponse response = pipeline.Service.Execute(new OrganizationRequest("zava_CreateContactLight")
            {
                ["Owner"] = new EntityReference("systemuser", Guid.NewGuid()),
            });

            Xunit.Assert.True(response.Results.ContainsKey("Created"));
            Xunit.Assert.Null(response["Created"]);
            Xunit.Assert.True(Xunit.Assert.Single(pipeline.ExecutionLog).HandlerInvoked);
        }

        [Xunit.Fact]
        public void A_step_on_a_global_api_registers_with_no_table_and_runs_on_table_none()
        {
            Vela.Steps.StepDefinition step = Xunit.Assert.Single(new AfterScore(string.Empty, string.Empty).GetStepDefinitions());
            Xunit.Assert.Equal(string.Empty, step.EntityLogicalName);

            Vela.Testing.PluginPipeline pipeline = Vela.Testing.PluginPipeline.Empty()
                .Register<CalculateAccountScore>()
                .Register<AfterScore>();

            pipeline.Service.Execute(new ScoreRequest { AccountId = Guid.NewGuid() });

            Vela.Testing.PipelineExecutionEntry entry = Xunit.Assert.Single(pipeline.ExecutionLog, e => e.PluginTypeName == typeof(AfterScore).FullName);
            Xunit.Assert.True(entry.HandlerInvoked);
        }

        [Xunit.Fact]
        public void Lightweight_method_group_handler_runs()
        {
            Vela.Testing.PluginPipeline pipeline = Vela.Testing.PluginPipeline.Empty().Register<CalculateAccountScoreLight>();

            OrganizationResponse response = pipeline.Service.Execute(new OrganizationRequest("zava_CalculateAccountScore")
            {
                ["AccountId"] = Guid.NewGuid(),
            });

            Xunit.Assert.Equal(42, response["Score"]);
        }
    }
}
