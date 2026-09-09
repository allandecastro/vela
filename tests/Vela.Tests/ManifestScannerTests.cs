using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Vela;
using Vela.Context;
using Vela.Constants;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class ManifestScannerTests
{
    [Fact]
    public void RegisterPlugin_UsesTypedMessageName()
    {
        StepDefinition definition = Register.RegisterPluginStep<TestAccount>()
            .OnMessage<ZavaAccountUpdateMessage>()
            .PostOperation()
            .Mode(ExecutionMode.Synchronous)
            .Build();

        Assert.Equal("zava_account_update", definition.Message);
    }

    [Fact]
    public void RegisterPluginStep_IsEnabledByDefault()
    {
        StepDefinition definition = Register.RegisterPluginStep<TestAccount>()
            .OnUpdate()
            .PostOperation()
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)
            .Build();

        Assert.True(definition.Enabled);
        Assert.Equal(string.Empty, definition.UnsecureConfig);
        Assert.Equal(string.Empty, definition.SecureConfig);
    }

    [Fact]
    public void RegisterPluginStep_DisabledAndConfiguration_AreCaptured()
    {
        StepDefinition definition = Register.RegisterPluginStep<TestAccount>()
            .OnUpdate()
            .PostOperation()
            .Mode(ExecutionMode.Synchronous)
            .FilteringAttributes(a => a.Name)
            .Disabled()
            .UnsecureConfig("u-cfg")
            .SecureConfig("s-cfg")
            .Build();

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
            RegisterSteps().OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);
        }

        protected override void Execute(ILocalPluginContext ctx)
        {
        }
    }

    private sealed class AccountMultiStepPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<TestAccount>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(OnCreate);

            RegisterSteps().OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(OnUpdate);
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

        protected override void Execute(ILocalPluginContext ctx)
        {
        }
    }
}
