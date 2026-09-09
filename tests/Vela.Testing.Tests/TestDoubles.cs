using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Vela.Constants;
using Vela.Context;
using Vela.CustomApi;
using Vela.Plugin;
using Vela.StepBuilder;

namespace Vela.Testing.Tests
{
    // Early-bound entity used to exercise the strongly-typed builder.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class FakeAccount : Entity
    {
        public const string EntityLogicalNameValue = "account";

        public FakeAccount()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }
    }

    // User-owned early-bound entity (exposes OwnerId/StateCode) for the Update-split and SetState tests.
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class FakeOwnable : Entity
    {
        public const string EntityLogicalNameValue = "vela_ownable";

        public FakeOwnable()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }

        [AttributeLogicalName("ownerid")]
        public EntityReference OwnerId
        {
            get => GetAttributeValue<EntityReference>("ownerid");
            set => SetAttributeValue("ownerid", value);
        }

        [AttributeLogicalName("statecode")]
        public OptionSetValue StateCode
        {
            get => GetAttributeValue<OptionSetValue>("statecode");
            set => SetAttributeValue("statecode", value);
        }
    }

    // Single-step entity plugin: records the target name when its Update/PostOperation step matches.
    public sealed class RecordingPlugin : EntityPluginBase<FakeAccount>
    {
        public RecordingPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        public string LastName { get; private set; } = string.Empty;

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);
        }

        protected override void Execute(ILocalPluginContext ctx)
        {
            FakeAccount target = ctx.GetTarget<FakeAccount>();
            LastName = target.Name;
        }
    }

    // On account create, records the 'tag' shared variable (passed by the client on the request) into a task
    // so a pipeline test can observe it after the run. Writes "<no-tag>" when none was supplied.
    public sealed class TagCapturingPlugin : EntityPluginBase<FakeAccount>
    {
        public TagCapturingPlugin(string unsecure, string secure)
            : base(unsecure, secure)
        {
        }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);
        }

        protected override void Execute(ILocalPluginContext ctx)
        {
            ctx.SystemService.Create(new Entity("task") { ["subject"] = ctx.Tag ?? "<no-tag>" });
        }
    }

    // Single-step plugin routed through a named Handler (no Execute override). Without an auto-derived
    // trigger, the default message would not match its OnCreate step and the base Execute would throw.
    public sealed class HandlerRecordingPlugin : EntityPluginBase<FakeAccount>
    {
        public HandlerRecordingPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        public string Handled { get; private set; } = string.Empty;

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(OnCreated);
        }

        private void OnCreated(ILocalPluginContext ctx)
        {
            FakeAccount target = ctx.GetTarget<FakeAccount>();
            Handled = target.Name;
        }
    }

    // Plugin that calls the system organization service so org-service injection can be observed.
    public sealed class CreatingPlugin : PluginBase
    {
        public CreatingPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
            ctx.SystemService.Create(new Entity("account"));
        }
    }

    // Plugin used to assert the depth guard throws.
    public sealed class DepthGuardedPlugin : PluginBase
    {
        public DepthGuardedPlugin()
            : base(string.Empty, string.Empty, maxDepth: 2)
        {
        }

        protected override void ExecuteDataversePlugin(ILocalPluginContext ctx)
        {
        }
    }

    public sealed class ScoreRequest : OrganizationRequest
    {
        public ScoreRequest()
        {
            RequestName = "zava_Score";
        }

        public int AccountId
        {
            get => Parameters.Contains(nameof(AccountId)) ? (int)Parameters[nameof(AccountId)] : default;
            set => Parameters[nameof(AccountId)] = value;
        }
    }

    public sealed class ScoreResponse : OrganizationResponse
    {
        public int Score
        {
            get => Results.Contains(nameof(Score)) ? (int)Results[nameof(Score)] : default;
            set => Results[nameof(Score)] = value;
        }
    }

    // Typed Custom API: hydrates the request over InputParameters, then copies the response Results to OutputParameters.
    public sealed class ScoringApi : CustomApiBase<ScoreRequest, ScoreResponse>
    {
        public ScoringApi()
            : base(string.Empty, string.Empty)
        {
        }

        protected override ScoreResponse Execute(ILocalPluginContext ctx, ScoreRequest request)
        {
            return new ScoreResponse { Score = request.AccountId * 10 };
        }
    }

    // Early-bound task entity (stands in for a modelbuilder-generated type).
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class FakeTask : Entity
    {
        public const string EntityLogicalNameValue = "task";

        public FakeTask()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("subject")]
        public string Subject
        {
            get => GetAttributeValue<string>("subject");
            set => SetAttributeValue("subject", value);
        }

        [AttributeLogicalName("ticketnumber")]
        public string TicketNumber
        {
            get => GetAttributeValue<string>("ticketnumber");
            set => SetAttributeValue("ticketnumber", value);
        }

        [AttributeLogicalName("regardingobjectid")]
        public EntityReference RegardingObjectId
        {
            get => GetAttributeValue<EntityReference>("regardingobjectid");
            set => SetAttributeValue("regardingobjectid", value);
        }
    }

    // Realistic plugin: on account create, provisions a follow-up task that references the account.
    public sealed class ProvisionAccountPlugin : EntityPluginBase<FakeAccount>
    {
        public ProvisionAccountPlugin()
            : base(string.Empty, string.Empty)
        {
        }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);
        }

        protected override void Execute(ILocalPluginContext ctx)
        {
            FakeAccount target = ctx.GetTarget<FakeAccount>();
            ctx.Trace("Provisioning account {0}", target.Name);

            FakeTask task = new FakeTask
            {
                Subject = $"Welcome {target.Name}",
                RegardingObjectId = new EntityReference(FakeAccount.EntityLogicalNameValue, ctx.PrimaryEntityId)
            };
            ctx.SystemService.Create(task);
        }
    }

    // Hand-written managed-identity service (no mocking library).
    public sealed class StubManagedIdentityService : IManagedIdentityService
    {
        private readonly string _token;

        public StubManagedIdentityService(string token)
        {
            _token = token;
        }

        public string AcquireToken(IEnumerable<string> scopes) => _token;

        public string AcquireToken(Guid applicationUserId, IEnumerable<string> scopes) => _token;
    }
}
