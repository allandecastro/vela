using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Constants;
using Vela.Plugin;
using Vela.Steps;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The typed trigger path, RegisterStep().On<TRequest>() / OnAsyncOnly<TRequest>(): every stage, mode and modifier it
// offers lands on the step definition exactly as on the guided path. A custom message is used: its images are not
// restricted, and it needs no filtering attribute (the typed path offers no FilterOn).
public sealed class TypedStepAdapterTests
{
    public sealed class PingRequest : OrganizationRequest
    {
        public PingRequest() => RequestName = "contract_Ping";
    }

    private static readonly Guid Integration = new("6f1c2a52-0d3b-4f5e-9a41-2b7c8d9e0f11");

    private sealed class DelegatePlugin(Action<IStepTrigger<TestAccount>> register) : EntityPluginBase<TestAccount>(string.Empty, string.Empty)
    {
        protected override void RegisterPluginSteps() => register(RegisterStep());
    }

    private static StepDefinition Single(Action<IStepTrigger<TestAccount>> register) =>
        Assert.Single(new DelegatePlugin(register).GetStepDefinitions());

    private static void AssertModifiers(StepDefinition d, string key, int rank)
    {
        Assert.Equal("contract_Ping", d.Message);
        Assert.Equal("typed", d.Description);
        Assert.Equal(key, d.StepKey);
        Assert.Equal(rank, d.ExecutionOrder);
        Assert.Equal(4, d.SkipWhenDepthAbove);
        Assert.Equal(Integration, d.ImpersonatingUserId);
        Assert.Equal(SupportedDeployment.Both, d.SupportedDeployment);
        Assert.Equal("cfg", d.UnsecureConfig);
        Assert.Equal("Secret", d.SecureConfigName);
        Assert.False(d.Enabled);
    }

    private static string Images(ImageDefinition[] images) =>
        string.Join(";", images.Select(i => i.Alias + ":" + string.Join(",", i.Attributes)));

    [Fact]
    public void PreValidation_carries_every_modifier_and_its_pre_images()
    {
        StepDefinition d = Single(s => s.On<PingRequest>().PreValidation().Sync()
            .WithDescription("typed").WithPreImage(a => a.Name).WithPreImage("Old", a => a.AccountId)
            .WithExecutionOrder(7).WithStepKey("pv").SkipWhenDepthAbove(4).WithImpersonation(Integration)
            .WithSupportedDeployment(SupportedDeployment.Both).WithUnsecureConfig("cfg").WithSecureConfigFrom("Secret").Disabled()
            .Handle(_ => { }));

        Assert.Equal((PluginStages.PreValidation, ExecutionMode.Synchronous), (d.Stage, d.Mode));
        AssertModifiers(d, "pv", 7);
        Assert.Equal("Old:accountid;PreImage:name", Images(d.PreImages.OrderBy(i => i.Alias, StringComparer.Ordinal).ToArray()));
        Assert.Empty(d.PostImages);
    }

    [Fact]
    public void PreOperation_carries_every_modifier_and_its_pre_images()
    {
        StepDefinition d = Single(s => s.On<PingRequest>().PreOperation().Sync()
            .WithDescription("typed").WithPreImage(a => a.Name).WithPreImage("Old", a => a.AccountId)
            .WithExecutionOrder(8).WithStepKey("po").SkipWhenDepthAbove(4).WithImpersonation(Integration)
            .WithSupportedDeployment(SupportedDeployment.Both).WithUnsecureConfig("cfg").WithSecureConfigFrom("Secret").Disabled()
            .Handle(_ => { }));

        Assert.Equal((PluginStages.PreOperation, ExecutionMode.Synchronous), (d.Stage, d.Mode));
        AssertModifiers(d, "po", 8);
        Assert.Equal("Old:accountid;PreImage:name", Images(d.PreImages.OrderBy(i => i.Alias, StringComparer.Ordinal).ToArray()));
    }

    [Fact]
    public void PostOperation_sync_carries_every_modifier_and_both_images()
    {
        StepDefinition d = Single(s => s.On<PingRequest>().PostOperation().Sync()
            .WithDescription("typed").WithPreImage(a => a.Name).WithPreImage("Old", a => a.AccountId)
            .WithPostImage(a => a.Name).WithPostImage("New", a => a.AccountId)
            .WithExecutionOrder(9).WithStepKey("post").SkipWhenDepthAbove(4).WithImpersonation(Integration)
            .WithSupportedDeployment(SupportedDeployment.Both).WithUnsecureConfig("cfg").WithSecureConfigFrom("Secret").Disabled()
            .Handle(_ => { }));

        Assert.Equal((PluginStages.PostOperation, ExecutionMode.Synchronous), (d.Stage, d.Mode));
        Assert.False(d.AsyncAutoDelete);
        AssertModifiers(d, "post", 9);
        Assert.Equal("Old:accountid;PreImage:name", Images(d.PreImages.OrderBy(i => i.Alias, StringComparer.Ordinal).ToArray()));
        Assert.Equal("New:accountid;PostImage:name", Images(d.PostImages.OrderBy(i => i.Alias, StringComparer.Ordinal).ToArray()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PostOperation_async_sets_the_mode_and_the_job_cleanup(bool explicitFlag, bool deleteJobOnSuccess)
    {
        StepDefinition d = Single(s =>
        {
            ISyncAsyncMode<ITypedPostOperationConfig<TestAccount, PingRequest>> stage = s.On<PingRequest>().PostOperation();
            (explicitFlag ? stage.Async(deleteJobOnSuccess) : stage.Async()).Handle(_ => { });
        });

        Assert.Equal((PluginStages.PostOperation, ExecutionMode.Asynchronous, deleteJobOnSuccess), (d.Stage, d.Mode, d.AsyncAutoDelete));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void An_async_only_message_is_post_operation_async(bool explicitFlag, bool deleteJobOnSuccess)
    {
        StepDefinition d = Single(s =>
        {
            IAsyncMode<ITypedPostOperationConfig<TestAccount, PingRequest>> stage = s.OnAsyncOnly<PingRequest>().PostOperation();
            (explicitFlag ? stage.Async(deleteJobOnSuccess) : stage.Async()).Handle(_ => { });
        });

        Assert.Equal((PluginStages.PostOperation, ExecutionMode.Asynchronous, deleteJobOnSuccess), (d.Stage, d.Mode, d.AsyncAutoDelete));
    }

    [Fact]
    public void The_untyped_handle_overload_binds_the_handler()
    {
        bool ran = false;
        StepDefinition d = Single(s => ((IStepConfig<TestAccount>)s.On<PingRequest>().PreOperation().Sync()).Handle(new Action<Vela.Context.ILocalPluginContext>(_ => ran = true)));

        Assert.Equal((PluginStages.PreOperation, ExecutionMode.Synchronous), (d.Stage, d.Mode));
        Assert.False(ran);
    }
}
