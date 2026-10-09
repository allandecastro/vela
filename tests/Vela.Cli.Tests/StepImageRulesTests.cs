using Vela.Steps;
using Xunit;

namespace Vela.Cli.Tests;

// The image capability table shared (as linked source) by the runtime builder, the harness, the analyzer and the CLI.
// The property map and the stage rules below were verified live on a Dataverse dev environment.
public sealed class StepImageRulesTests
{
    [Theory]
    [InlineData("Create", "Id")]
    [InlineData("create", "Id")]
    [InlineData("CreateMultiple", "Ids")]
    [InlineData("UpdateMultiple", "Targets")]
    [InlineData("Update", "Target")]
    [InlineData("Delete", "Target")]
    [InlineData("Assign", "Target")]
    [InlineData("Merge", "Target")]
    [InlineData("SetState", "EntityMoniker")]
    [InlineData("SetStateDynamicEntity", "EntityMoniker")]
    [InlineData("Send", "EmailId")]
    [InlineData("DeliverIncoming", "EmailId")]
    [InlineData("DeliverPromote", "EmailId")]
    [InlineData("adc_DoThing", "Target")]
    [InlineData(null, "Target")]
    public void Maps_the_message_to_its_image_property(string? message, string expected)
        => Assert.Equal(expected, StepImageRules.GetMessagePropertyName(message));

    [Fact]
    public void RouteTo_supports_no_image()
    {
        Assert.Null(StepImageRules.GetMessagePropertyName("RouteTo"));
        Assert.False(StepImageRules.IsImageAllowed("RouteTo", 40, isPreImage: true));
        Assert.False(StepImageRules.IsImageAllowed("RouteTo", 20, isPreImage: true));
    }

    // message, stage, pre-image allowed, post-image allowed
    [Theory]
    [InlineData("Update", 10, true, false)]
    [InlineData("Update", 20, true, false)]
    [InlineData("Update", 30, false, false)]
    [InlineData("Update", 40, true, true)]
    [InlineData("UpdateMultiple", 10, true, false)]
    [InlineData("UpdateMultiple", 20, true, false)]
    [InlineData("UpdateMultiple", 40, true, true)]
    [InlineData("Create", 10, false, false)]
    [InlineData("Create", 20, false, false)]
    [InlineData("Create", 40, false, true)]
    [InlineData("CreateMultiple", 10, false, false)]
    [InlineData("CreateMultiple", 20, false, false)]
    [InlineData("CreateMultiple", 40, false, true)]
    [InlineData("Delete", 10, true, false)]
    [InlineData("Delete", 20, true, false)]
    [InlineData("Delete", 40, true, false)]
    [InlineData("Assign", 10, true, false)]
    [InlineData("Assign", 40, true, true)]
    [InlineData("adc_DoThing", 10, true, false)]
    [InlineData("adc_DoThing", 40, true, true)]
    public void Applies_the_stage_rules(string message, int stage, bool preAllowed, bool postAllowed)
    {
        Assert.Equal(preAllowed, StepImageRules.IsImageAllowed(message, stage, isPreImage: true));
        Assert.Equal(postAllowed, StepImageRules.IsImageAllowed(message, stage, isPreImage: false));
    }

    [Fact]
    public void Message_rules_ignore_the_stage()
    {
        Assert.NotNull(StepImageRules.GetMessageRejectionReason("CreateMultiple", isPreImage: true));
        Assert.NotNull(StepImageRules.GetMessageRejectionReason("Delete", isPreImage: false));
        Assert.Null(StepImageRules.GetMessageRejectionReason("Update", isPreImage: false)); // valid at PostOperation
    }
}
