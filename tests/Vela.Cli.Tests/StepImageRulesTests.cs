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

    [Theory]
    [InlineData("Merge", null, null)]
    [InlineData("Merge", "", null)]
    [InlineData("Merge", "Target", null)]
    [InlineData("Merge", "SubordinateId", null)]
    [InlineData("merge", "subordinateid", null)]
    [InlineData("Update", "Target", null)]
    [InlineData("Create", "Id", null)]
    [InlineData("Update", "SubordinateId", "only a Merge step")]   // 0x8004416B, verified live
    [InlineData("Merge", "Id", "not 'Id'")]
    public void Checks_an_explicit_image_property(string message, string? property, string? rejection)   // #462
    {
        string? reason = StepImageRules.GetMessagePropertyRejectionReason(message, property);
        if (rejection is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(rejection, reason);
        }
    }

    [Theory]
    [InlineData("Merge", null, "Target")]
    [InlineData("Merge", "SubordinateId", "SubordinateId")]
    [InlineData("Create", "", "Id")]
    [InlineData("RouteTo", null, null)]
    public void Resolves_the_registered_image_property(string message, string? property, string? expected)
        => Assert.Equal(expected, StepImageRules.ResolveMessagePropertyName(message, property));

    // RouteTo fails with 0x80044189; SendFromTemplate "does not support image registration" (both verified live).
    [Theory]
    [InlineData("RouteTo")]
    [InlineData("SendFromTemplate")]
    public void Message_supports_no_image(string message)
    {
        Assert.Null(StepImageRules.GetMessagePropertyName(message));
        Assert.False(StepImageRules.IsImageAllowed(message, 40, isPreImage: true));
        Assert.False(StepImageRules.IsImageAllowed(message, 40, isPreImage: false));
        Assert.False(StepImageRules.IsImageAllowed(message, 20, isPreImage: true));
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
