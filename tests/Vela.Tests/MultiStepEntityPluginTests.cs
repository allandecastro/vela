using System;
using System.Collections.Generic;
using Vela;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.StepBuilder;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Tests;

public sealed class MultiStepEntityPluginTests
{
    [Fact]
    public void EntityPluginBase_DispatchesToMatchingHandler()
    {
        MultiStepPlugin plugin = new MultiStepPlugin();
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount())
            .BuildServiceProvider();

        plugin.Execute(provider);

        Assert.Equal("HandleUpdate", plugin.LastHandler);
    }

    [Fact]
    public void EntityPluginBase_UsesExecuteForSingleStep()
    {
        SingleStepPlugin plugin = new SingleStepPlugin();
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount())
            .BuildServiceProvider();

        plugin.Execute(provider);

        Assert.True(plugin.Executed);
    }

    [Fact]
    public void EntityPluginBase_BuildsTargetFromEntityReference_OnDelete()
    {
        Guid id = Guid.NewGuid();
        CaptureDeletePlugin plugin = new CaptureDeletePlugin();
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnDelete()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithInputParameter("Target", new EntityReference("account", id))
            .BuildServiceProvider();

        plugin.Execute(provider);

        Assert.True(plugin.Executed);
        Assert.Equal(id, plugin.CapturedId);
    }

    [Fact]
    public void EntityPluginBase_SuppliesEmptyTypedTarget_WhenMessageHasNoTarget()
    {
        // RetrieveMultiple carries no Target; the base must hand the handler a non-null typed entity.
        TargetProbePlugin plugin = new TargetProbePlugin();
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnMessage(PluginMessages.RetrieveMultiple)
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .BuildServiceProvider();

        plugin.Execute(provider);

        Assert.True(plugin.Executed);
        Assert.Equal("account", plugin.CapturedLogicalName);
        Assert.Equal(Guid.Empty, plugin.CapturedId);
    }

    [Fact]
    public void EntityPluginBase_ThrowsWhenHandlerIsMissing()
    {
        MissingHandlerPlugin plugin = new MissingHandlerPlugin();
        IServiceProvider provider = PluginContext.For<TestAccount>()
            .OnUpdate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount())
            .BuildServiceProvider();

        InvalidPluginExecutionException exception = Assert.Throws<InvalidPluginExecutionException>(() => plugin.Execute(provider));
        Assert.Contains("missing Handler", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntityPluginBase_RegisterStepsOutsideRegistration_Throws()
    {
        RegisterStepsOutsidePlugin plugin = new RegisterStepsOutsidePlugin();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => plugin.TryRegisterOutside());
        Assert.Contains("inside RegisterPluginSteps", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MultiStepPlugin : EntityPluginBase<TestAccount>
    {
        public MultiStepPlugin() : base(string.Empty, string.Empty)
        {
        }

        public string LastHandler { get; private set; } = string.Empty;

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(HandleCreate);

            RegisterSteps().OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name)
                .Handler(HandleUpdate);
        }

        private void HandleCreate(ILocalPluginContext ctx, TestAccount target)
        {
            LastHandler = "HandleCreate";
        }

        private void HandleUpdate(ILocalPluginContext ctx, TestAccount target)
        {
            LastHandler = "HandleUpdate";
        }
    }

    private sealed class SingleStepPlugin : EntityPluginBase<TestAccount>
    {
        public SingleStepPlugin() : base(string.Empty, string.Empty)
        {
        }

        public bool Executed { get; private set; }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);
        }

        protected override void Execute(ILocalPluginContext ctx, TestAccount target)
        {
            Executed = true;
        }
    }

    private sealed class CaptureDeletePlugin : EntityPluginBase<TestAccount>
    {
        public CaptureDeletePlugin() : base(string.Empty, string.Empty)
        {
        }

        public bool Executed { get; private set; }

        public Guid CapturedId { get; private set; }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnDelete()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous);
        }

        protected override void Execute(ILocalPluginContext ctx, TestAccount target)
        {
            Executed = true;
            CapturedId = target.Id;
        }
    }

    private sealed class TargetProbePlugin : EntityPluginBase<TestAccount>
    {
        public TargetProbePlugin() : base(string.Empty, string.Empty)
        {
        }

        public bool Executed { get; private set; }

        public Guid CapturedId { get; private set; }

        public string CapturedLogicalName { get; private set; } = string.Empty;

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnRetrieveMultiple()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous);
        }

        protected override void Execute(ILocalPluginContext ctx, TestAccount target)
        {
            Executed = true;
            CapturedId = target.Id;
            CapturedLogicalName = target.LogicalName;
        }
    }

    private sealed class MissingHandlerPlugin : EntityPluginBase<TestAccount>
    {
        public MissingHandlerPlugin() : base(string.Empty, string.Empty)
        {
        }

        protected override void RegisterPluginSteps()
        {
            RegisterSteps().OnCreate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);

            RegisterSteps().OnUpdate()
                .PostOperation()
                .Mode(ExecutionMode.Synchronous)
                .FilteringAttributes(a => a.Name);
        }
    }

    private sealed class RegisterStepsOutsidePlugin : EntityPluginBase<TestAccount>
    {
        public RegisterStepsOutsidePlugin() : base(string.Empty, string.Empty)
        {
        }

        public void TryRegisterOutside()
        {
            RegisterSteps();
        }

        protected override void RegisterPluginSteps()
        {
        }

        protected override void Execute(ILocalPluginContext ctx, TestAccount target)
        {
        }
    }
}
