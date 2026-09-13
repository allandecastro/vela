using System;
using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class SdkMessageGeneratorTests
{
    private static string Generate(SdkMessage message) =>
        SdkMessageGenerator.Generate(new[] { message }, "Acme.Shared.Models.Generated").Single().Code;

    [Fact]
    public void Generates_TypedRequestAndResponse()
    {
        SdkMessage message = new SdkMessage(
            "QualifyLead",
            RequestFields: new[]
            {
                new SdkMessageField("LeadId", "EntityReference"),
                new SdkMessageField("CreateAccount", "bool"),
            },
            ResponseFields: new[]
            {
                new SdkMessageField("CreatedEntities", "EntityCollection"),
            });

        string code = Generate(message);

        Assert.Contains("public partial class QualifyLeadRequest : Vela.Messages.TypedRequest", code);
        Assert.Contains("RequestName = \"QualifyLead\";", code);
        Assert.Contains("public EntityReference LeadId", code);
        Assert.Contains("set => Set(value);", code);
        Assert.Contains("public bool CreateAccount", code);

        Assert.Contains("public partial class QualifyLeadResponse : Vela.Messages.TypedResponse", code);
        Assert.DoesNotContain("From(OrganizationResponse", code);   // wrap via ToResponse<T>() instead
        Assert.Contains("public EntityCollection CreatedEntities => Get<EntityCollection>();", code);
    }

    [Fact]
    public void RequestField_IsReadWrite_Over_Parameters()
    {
        string code = Generate(new SdkMessage(
            "DoThing",
            new[] { new SdkMessageField("Value", "int") },
            Array.Empty<SdkMessageField>()));

        Assert.Contains("get => Get<int>();", code);
        Assert.Contains("set => Set(value);", code);
    }

    [Fact]
    public void ResponseField_IsReadOnly_Over_Results()
    {
        string code = Generate(new SdkMessage(
            "DoThing",
            Array.Empty<SdkMessageField>(),
            new[] { new SdkMessageField("Total", "decimal") }));

        // Expression-bodied read-only property: no setter emitted for a response field.
        Assert.Contains("public decimal Total => Get<decimal>();", code);
        Assert.DoesNotContain("set => Set(value);", code);
    }

    [Fact]
    public void EmptyRequestAndResponse_StillEmitsBothClasses()
    {
        string code = Generate(new SdkMessage("WhoAmI", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()));

        Assert.Contains("public partial class WhoAmIRequest : Vela.Messages.TypedRequest", code);
        Assert.Contains("RequestName = \"WhoAmI\";", code);
        Assert.Contains("public partial class WhoAmIResponse : Vela.Messages.TypedResponse", code);
        Assert.DoesNotContain("From(OrganizationResponse", code);
    }

    [Fact]
    public void FileName_IsMessageName_InMessagesFolder()
    {
        GeneratedModel model = SdkMessageGenerator
            .Generate(new[] { new SdkMessage("AddToQueue", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()) }, "N")
            .Single();

        Assert.Equal("Messages/AddToQueue.cs", model.FileName);
    }

    [Fact]
    public void Orders_Messages_ByName_Ordinal()
    {
        var models = SdkMessageGenerator.Generate(
            new[]
            {
                new SdkMessage("Zeta", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
                new SdkMessage("Alpha", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
            },
            "N");

        Assert.Equal(new[] { "Messages/Alpha.cs", "Messages/Zeta.cs" }, models.Select(m => m.FileName).ToArray());
    }

    [Fact]
    public void Skips_BlankNames()
    {
        var models = SdkMessageGenerator.Generate(
            new[]
            {
                new SdkMessage("  ", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
                new SdkMessage("Real", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
            },
            "N");

        Assert.Equal(new[] { "Messages/Real.cs" }, models.Select(m => m.FileName).ToArray());
    }

    [Fact]
    public void Generate_NullMessages_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SdkMessageGenerator.Generate(null!, "N"));
    }

    [Fact]
    public void Generate_BlankNamespace_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SdkMessageGenerator.Generate(new[] { new SdkMessage("X", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()) }, "  "));
    }
}
