using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vela.Analyzers.Tests;

// The one receiver-spine walk every analyzer and the generator read a step chain through (A-32).
public sealed class FluentStepTests
{
    private static string[] SpineOf(string expression)
    {
        ExpressionSyntax parsed = SyntaxFactory.ParseExpression(expression);
        return FluentStep.Spine(parsed).Select(c => c.MethodName).ToArray();
    }

    [Fact]
    public void Walks_the_calls_nearest_first_down_to_the_RegisterStep_root()
        => Assert.Equal(new[] { "PostOperation", "OnUpdate", "RegisterStep" }, SpineOf("RegisterStep().OnUpdate().PostOperation()"));

    [Fact]
    public void Sees_through_parentheses_and_the_null_forgiving_operator()
        => Assert.Equal(new[] { "Sync", "PostOperation", "OnUpdate", "RegisterStep" }, SpineOf("((RegisterStep()!).OnUpdate()).PostOperation()!.Sync()"));

    [Fact]
    public void Steps_over_a_property_hop_and_stops_at_a_non_call_root()
        => Assert.Equal(new[] { "OnCreate", "RegisterStep" }, SpineOf("this.Steps.RegisterStep().OnCreate()"));

    [Fact]
    public void Stops_at_a_local_and_never_enters_argument_lambdas()
        => Assert.Equal(new[] { "WithPreImage", "PostOperation" }, SpineOf("step.PostOperation().WithPreImage(a => a.Name.Trim())"));

    [Fact]
    public void Exposes_a_generic_trigger_name_and_its_arguments()
    {
        ChainCall call = FluentStep.Spine(SyntaxFactory.ParseExpression("RegisterStep().On<DeleteRequest>().PostOperation()")).Skip(1).First();
        Assert.Equal("On", call.MethodName);
        Assert.IsType<GenericNameSyntax>(call.Name);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void Finds_a_call_on_the_spine()
    {
        ExpressionSyntax chain = SyntaxFactory.ParseExpression("RegisterStep().OnUpdate().Async()");
        Assert.True(FluentStep.SpineHasCall(chain, "Async"));
        Assert.False(FluentStep.SpineHasCall(chain, "FilterOn"));
        Assert.False(FluentStep.SpineHasCall(null, "Async"));
    }

    [Theory]
    [InlineData("a => a.Name", "Name")]
    [InlineData("(a) => (object)a.Name", "Name")]
    [InlineData("a => (a.Name as object)", "Name")]
    [InlineData("a => (a).Name", "Name")]
    [InlineData("a => a.Parent.Name", null)]
    [InlineData("a => b.Name", null)]
    [InlineData("(a, b) => a.Name", null)]
    [InlineData("a => a.GetName()", null)]
    [InlineData("a => { return a.Name; }", null)]
    public void Reads_the_column_a_selector_selects(string lambda, string? expected)
    {
        var parsed = (LambdaExpressionSyntax)SyntaxFactory.ParseExpression(lambda);
        Assert.Equal(expected, FluentStep.SelectedMember(parsed)?.Name.Identifier.ValueText);
    }

    [Theory]
    [InlineData("OnUpdated", "OnUpdated")]
    [InlineData("this.OnUpdated", "OnUpdated")]
    [InlineData("ctx => { }", null)]
    public void Names_a_method_group_handler(string handler, string? expected)
        => Assert.Equal(expected, FluentStep.MethodGroupName(SyntaxFactory.ParseExpression(handler)));
}
