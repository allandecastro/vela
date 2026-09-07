using System;
using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class CustomApiMessageGeneratorTests
{
    private static string Generate(CustomApiMessage message) =>
        CustomApiMessageGenerator.Generate(new[] { message }, "Acme.Shared.Models.Generated").Single().Code;

    [Fact]
    public void Generates_TypedRequestAndResponse()
    {
        CustomApiMessage message = new CustomApiMessage(
            "zava_Recalculate",
            IsBoundToEntity: true,
            RequestParameters: new[] { new CustomApiParameter("AccountId", 7) },   // Integer
            ResponseProperties: new[] { new CustomApiParameter("Total", 2) });      // Decimal

        string code = Generate(message);

        Assert.Contains("public partial class zava_RecalculateRequest : OrganizationRequest", code);
        Assert.Contains("RequestName = \"zava_Recalculate\";", code);
        Assert.Contains("public EntityReference Target", code);   // bound → implicit Target
        Assert.Contains("public int AccountId", code);
        Assert.Contains("Parameters[\"AccountId\"] = value;", code);

        Assert.Contains("public partial class zava_RecalculateResponse : OrganizationResponse", code);
        Assert.Contains("public static zava_RecalculateResponse From(OrganizationResponse response)", code);
        Assert.Contains("public decimal Total =>", code);
    }

    [Fact]
    public void Unbound_HasNoTarget()
    {
        string code = Generate(new CustomApiMessage("zava_Ping", false, Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>()));

        Assert.DoesNotContain("public EntityReference Target", code);
        Assert.Contains("public partial class zava_PingRequest : OrganizationRequest", code);
    }

    [Fact]
    public void FileName_IsUniqueName()
    {
        GeneratedModel model = CustomApiMessageGenerator
            .Generate(new[] { new CustomApiMessage("zava_X", false, Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>()) }, "N")
            .Single();

        Assert.Equal("Messages/zava_X.cs", model.FileName);
    }
}
