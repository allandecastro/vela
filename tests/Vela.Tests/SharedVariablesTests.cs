using Vela.Constants;
using Vela.Context;
using Vela.Steps;
using Vela.Testing;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

public sealed class SharedVariablesTests
{
    private static LocalPluginContext Context(System.Action<PluginContextBuilder<TestAccount>> configure)
    {
        PluginContextBuilder<TestAccount> builder = PluginContext.For<TestAccount>()
            .OnCreate()
            .Stage(PluginStages.PostOperation)
            .Mode(ExecutionMode.Synchronous)
            .WithTarget(new TestAccount { Name = "X" });
        configure(builder);
        return builder.Build();
    }

    [Fact]
    public void Tag_exposes_the_tag_shared_variable()
    {
        LocalPluginContext ctx = Context(b => b.WithTag("from-client"));

        Assert.Equal("from-client", ctx.Tag);
        Assert.Equal("from-client", ctx.GetSharedVariable("tag"));
    }

    [Fact]
    public void Tag_is_null_when_not_set()
    {
        LocalPluginContext ctx = Context(_ => { });

        Assert.Null(ctx.Tag);
        Assert.Null(ctx.GetSharedVariable("tag"));
    }

    [Fact]
    public void TryGetSharedVariable_returns_typed_value_when_present()
    {
        LocalPluginContext ctx = Context(b => b.WithSharedVariable("count", 7));

        Assert.True(ctx.TryGetSharedVariable<int>("count", out int count));
        Assert.Equal(7, count);
    }

    [Fact]
    public void TryGetSharedVariable_returns_false_when_absent_or_wrong_type()
    {
        LocalPluginContext ctx = Context(b => b.WithSharedVariable("count", 7));

        Assert.False(ctx.TryGetSharedVariable<int>("missing", out _));
        Assert.False(ctx.TryGetSharedVariable<string>("count", out _)); // present, but not a string
    }
}
