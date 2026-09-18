using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Vela;
using Vela.Context;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class ManifestScannerTests
{
    // The fluent transitions are Vela.Steps interface members, so a step is built by driving a StepBuilder<T>
    // through IStepTrigger<T> and then calling Build() on the kept reference.
    private static IStepTrigger<TestAccount> Trigger(StepBuilder<TestAccount> builder) => builder;

    [Fact]
    public void RegisterPlugin_UsesTypedMessageName()
    {
        StepBuilder<TestAccount> builder = new StepBuilder<TestAccount>();
        Trigger(builder)
            .OnMessage<ZavaAccountUpdateMessage>()
            .PostOperation()
            .Sync();

        StepDefinition definition = builder.Build();

        Assert.Equal("zava_account_update", definition.Message);
    }

    [Fact]
    public void RegisterPluginStep_IsEnabledByDefault()
    {
        StepBuilder<TestAccount> builder = new StepBuilder<TestAccount>();
        Trigger(builder)
            .OnUpdate()
            .PostOperation()
            .Sync()
            .FilterOn(a => a.Name);

        StepDefinition definition = builder.Build();

        Assert.True(definition.Enabled);
        Assert.Equal(string.Empty, definition.UnsecureConfig);
        Assert.Equal(string.Empty, definition.SecureConfig);
    }

    [Fact]
    public void RegisterPluginStep_DisabledAndConfiguration_AreCaptured()
    {
        StepBuilder<TestAccount> builder = new StepBuilder<TestAccount>();
        Trigger(builder)
            .OnUpdate()
            .PostOperation()
            .Sync()
            .FilterOn(a => a.Name)
            .Disabled()
            .WithUnsecureConfig("u-cfg")
            .WithSecureConfig("s-cfg");

        StepDefinition definition = builder.Build();

        Assert.False(definition.Enabled);
        Assert.Equal("u-cfg", definition.UnsecureConfig);
        Assert.Equal("s-cfg", definition.SecureConfig);
    }

    [Fact]
    public void RegisterCustomApi_BuildsDefinition()
    {
        CustomApiDefinition definition = Register.RegisterCustomApi<CalculateScoreRequest, CalculateScoreResponse>()
            .UniqueName("zava_CalculateScore")
            .DisplayName("Calculate score")
            .Description("Calculates a score")
            .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
            .WorkflowSdkStep(true)
            .BoundTo<TestAccount>()
            .Request(request => request
                .Add(item => item.AccountId)
                .Add(item => item.IncludeHistory, optional: true))
            .Response(response => response
                .Add(item => item.Score))
            .Build();

        Assert.Equal("zava_CalculateScore", definition.UniqueName);
        Assert.Equal("account", definition.BoundEntityLogicalName);
        Assert.Equal(CustomApiAllowedCustomProcessingStepType.SyncAndAsync, definition.AllowedCustomProcessingStepType);
        Assert.True(definition.WorkflowSdkStepEnabled);
        Assert.Equal(2, definition.InputParameters.Length);
        Assert.Single(definition.OutputParameters);
    }

    [Fact]
    public void RegisterCustomApi_Lightweight_BuildsDefinition()
    {
        CustomApiDefinition definition = Register.RegisterCustomApi()
            .UniqueName("zava_CalculateScoreLight")
            .DisplayName("Calculate score light")
            .Description("Calculates a score without request or response classes")
            .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.None)
            .RequestParameter<Guid>("AccountId")
            .RequestParameter<bool>("IncludeHistory", optional: true)
            .ResponseParameter<int>("Score")
            .Build();

        Assert.Equal("zava_CalculateScoreLight", definition.UniqueName);
        Assert.Equal(2, definition.InputParameters.Length);
        Assert.Single(definition.OutputParameters);
        Assert.Equal("AccountId", definition.InputParameters[0].Name);
        Assert.Equal(typeof(Guid).FullName, definition.InputParameters[0].ParameterTypeName);
    }

    [Fact]
    public void FrameworkManifestScanner_FindsPluginsAndCustomApis()
    {
        FrameworkManifest manifest = FrameworkManifestScanner.Scan(typeof(ManifestScannerTests).Assembly);

        Assert.Contains(manifest.PluginSteps, step => step.PluginTypeName == typeof(AccountUpdatePlugin).FullName);
        Assert.Contains(manifest.PluginSteps, step => step.PluginTypeName == typeof(AccountMultiStepPlugin).FullName && step.HandlerMethodName == "OnCreate");
        Assert.Contains(manifest.PluginSteps, step => step.PluginTypeName == typeof(AccountMultiStepPlugin).FullName && step.HandlerMethodName == "OnUpdate");
        Assert.Contains(manifest.CustomApis, api => api.PluginTypeName == typeof(CalculateScoreApi).FullName);
        Assert.Contains(manifest.CustomApis, api => api.PluginTypeName == typeof(CalculateScoreLightweightApi).FullName && api.HandlerMethodName == "HandleExecute");
    }

    [Fact]
    public void FrameworkManifestScanner_Strict_ThrowsOnDiagnostics()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FrameworkManifestScanner.Scan(
                typeof(BrokenPlugin).Assembly,
                new FrameworkManifestScanOptions { Strict = true }));
    }

    [Fact]
    public void FrameworkManifestScanner_IntrospectionFailure_IsReportedAsError()
    {
        // A type that IS a Custom API but fails to build its definition is excluded from the manifest, which
        // silently drops it from the plan (and could delete a live registration). It must be an Error, not a
        // skippable Warning. The InvalidOutside* types register outside RegisterCustomApi() and throw on
        // construction, so scanning this assembly produces exactly that failure.
        FrameworkManifest manifest = FrameworkManifestScanner.Scan(typeof(ManifestScannerTests).Assembly);

        Assert.Contains(manifest.Diagnostics, d =>
            d.Severity == FrameworkManifestDiagnosticSeverity.Error &&
            d.Message.Contains("discovery failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CustomApiBase_RegisterCustomApiOutsideRegistration_Throws()
    {
        InvalidOutsideLightweightApi api = new InvalidOutsideLightweightApi(string.Empty, string.Empty);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => api.TryRegisterOutside());
        Assert.Contains("inside RegisterCustomApi", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomApiBaseOfT_RegisterCustomApiOutsideRegistration_Throws()
    {
        InvalidOutsideTypedApi api = new InvalidOutsideTypedApi(string.Empty, string.Empty);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => api.TryRegisterOutside());
        Assert.Contains("inside RegisterCustomApi", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [CustomMessage("zava_account_update")]
    private sealed class ZavaAccountUpdateMessage
    {
    }

    private sealed class AccountUpdatePlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<TestAccount>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .Handle(OnUpdate);
        }

        private void OnUpdate(IUpdatePostContext<TestAccount> ctx)
        {
        }
    }

    private sealed class AccountMultiStepPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<TestAccount>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .Handle(OnCreate);

            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .Handle(OnUpdate);
        }

        private void OnCreate(ILocalPluginContext ctx)
        {
        }

        private void OnUpdate(ILocalPluginContext ctx)
        {
        }
    }

    private sealed class CalculateScoreRequest : OrganizationRequest
    {
        public CalculateScoreRequest()
        {
            RequestName = "zava_CalculateScore";
        }

        [Required]
        public Guid AccountId
        {
            get => Parameters.Contains(nameof(AccountId)) ? (Guid)Parameters[nameof(AccountId)] : default;
            set => Parameters[nameof(AccountId)] = value;
        }

        public bool IncludeHistory
        {
            get => Parameters.Contains(nameof(IncludeHistory)) ? (bool)Parameters[nameof(IncludeHistory)] : default;
            set => Parameters[nameof(IncludeHistory)] = value;
        }
    }

    private sealed class CalculateScoreResponse : OrganizationResponse
    {
        public int Score
        {
            get => Results.Contains(nameof(Score)) ? (int)Results[nameof(Score)] : default;
            set => Results[nameof(Score)] = value;
        }
    }

    private sealed class CalculateScoreApi(string unsecureConfig, string secureConfig) : CustomApiBase<CalculateScoreRequest, CalculateScoreResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_CalculateScore")
                .DisplayName("Calculate score")
                .Description("Calculates a score")
                .AsAction()
                .BoundTo<TestAccount>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .WorkflowSdkStep(true)
                .Request(request => request
                    .Add(item => item.AccountId)
                    .Add(item => item.IncludeHistory, optional: true))
                .Response(response => response
                    .Add(item => item.Score));
        }

        protected override CalculateScoreResponse Execute(ILocalPluginContext ctx, CalculateScoreRequest request)
        {
            return new CalculateScoreResponse { Score = 42 };
        }
    }

    private sealed class CalculateScoreLightweightApi(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_CalculateScoreLight", HandleExecute)
                .DisplayName("Calculate score light")
                .Description("Calculates a score without request or response classes")
                .AsAction()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.None)
                .RequestParameter<Guid>("AccountId")
                .RequestParameter<bool>("IncludeHistory", optional: true)
                .ResponseParameter<int>("Score");
        }

        private void HandleExecute(ILocalPluginContext ctx)
        {
            Guid accountId = ctx.GetInputParameter<Guid>("AccountId");
            bool includeHistory = ctx.GetInputParameter<bool>("IncludeHistory");

            _ = includeHistory;
            _ = accountId;

            ctx.SetOutputParameter("Score", 42);
        }
    }

    private sealed class InvalidOutsideLightweightApi(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)
    {
        public void TryRegisterOutside()
        {
            RegisterCustomApi("zava_InvalidOutsideLight", HandleExecute);
        }

        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_InvalidOutsideLight", HandleExecute)
                .DisplayName("Invalid outside lightweight")
                .AsAction();
        }

        private void HandleExecute(ILocalPluginContext ctx)
        {
            _ = ctx;
        }
    }

    private sealed class InvalidOutsideTypedApi(string unsecureConfig, string secureConfig) : CustomApiBase<CalculateScoreRequest, CalculateScoreResponse>(unsecureConfig, secureConfig)
    {
        public void TryRegisterOutside()
        {
            RegisterCustomApi("zava_InvalidOutsideTyped");
        }

        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("zava_InvalidOutsideTyped")
                .DisplayName("Invalid outside typed")
                .AsAction();
        }

        protected override CalculateScoreResponse Execute(ILocalPluginContext ctx, CalculateScoreRequest request)
        {
            return new CalculateScoreResponse { Score = 0 };
        }
    }

    private sealed class BrokenPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<TestAccount>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            throw new InvalidOperationException("Intentional test failure.");
        }
    }
}
