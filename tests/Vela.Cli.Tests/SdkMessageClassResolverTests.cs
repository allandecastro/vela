using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class SdkMessageClassResolverTests
{
    [Theory]
    [InlineData("Create")]      // Microsoft.Xrm.Sdk.Messages.CreateRequest
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("RetrieveMultiple")]
    public void HasSdkRequestClass_True_ForCoreSdkMessages(string messageName)
    {
        Assert.True(SdkMessageClassResolver.HasSdkRequestClass(messageName));
    }

    [Theory]
    [InlineData("QualifyLead")]        // Microsoft.Crm.Sdk.Messages.QualifyLeadRequest
    [InlineData("WinOpportunity")]
    [InlineData("RenewContract")]
    [InlineData("CloseQuote")]         // was missing from the old hand-kept list
    [InlineData("CancelSalesOrder")]
    [InlineData("qualifylead")]        // case-insensitive
    public void HasSdkRequestClass_True_ForCrmSdkMessages(string messageName)
    {
        // Reflected over Microsoft.Crm.Sdk.Proxy, which the CLI loads through the Dataverse client.
        Assert.True(SdkMessageClassResolver.HasSdkRequestClass(messageName));
    }

    [Theory]
    [InlineData("create", "Microsoft.Xrm.Sdk.Messages.CreateRequest")]
    [InlineData("closequote", "Microsoft.Crm.Sdk.Messages.CloseQuoteRequest")]
    [InlineData("SetState", "Microsoft.Crm.Sdk.Messages.SetStateRequest")]
    public void TryGetSdkRequestTypeName_ReturnsTheSdkCasing(string messageName, string expected)
    {
        Assert.True(SdkMessageClassResolver.TryGetSdkRequestTypeName(messageName, out string typeName));
        Assert.Equal(expected, typeName);
    }

    [Theory]
    [InlineData("zava_DoThing")]              // a Custom API name, no SDK type
    [InlineData("DefinitelyNotARealMessage")] // neither reflected nor in the supplement
    public void HasSdkRequestClass_False_ForNonSdkMessages(string messageName)
    {
        Assert.False(SdkMessageClassResolver.HasSdkRequestClass(messageName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void HasSdkRequestClass_False_ForBlank(string? messageName)
    {
        Assert.False(SdkMessageClassResolver.HasSdkRequestClass(messageName!));
    }

    private static SdkMessage Contract(string name) => new SdkMessage(name, System.Array.Empty<SdkMessageField>(), System.Array.Empty<SdkMessageField>());

    [Fact]
    public void PreferSdkType_SdkBackedRequest_PointsAtTheSdkType()
    {
        SdkMessage? contract = ModelsCommand.PreferSdkType(Contract("CloseQuote"));

        Assert.Equal("Microsoft.Crm.Sdk.Messages.CloseQuoteRequest", contract!.SdkRequestType);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("WinOpportunity")]
    public void PreferSdkType_CuratedTrigger_GeneratesNothing(string name)
    {
        Assert.Null(ModelsCommand.PreferSdkType(Contract(name)));
    }

    [Fact]
    public void PreferSdkType_NonSdkRequest_IsKept()
    {
        SdkMessage? contract = ModelsCommand.PreferSdkType(Contract("msdyn_DoThing"));

        Assert.Null(contract!.SdkRequestType);
    }

    // `vela models --messages` lists sdkmessage names, which are not always request names: Send is run by
    // SendEmailRequest, Win by WinOpportunityRequest/WinQuoteRequest. They already ship a request class, so ALL mode
    // must skip them instead of emitting a SendRequest/WinRequest duplicate.
    [Theory]
    [InlineData("Send", "SendEmail")]
    [InlineData("Win", "WinOpportunity")]
    [InlineData("Close", "CloseIncident")]
    [InlineData("AddMembers", "AddMembersTeam")]
    [InlineData("QualifyLead", "QualifyLead")]
    public void SdkRequestName_maps_an_sdkmessage_to_the_request_that_runs_it(string sdkMessage, string expected)
    {
        Assert.Equal(expected, SdkMessageClassResolver.SdkRequestName(sdkMessage));
        Assert.True(SdkMessageClassResolver.HasSdkRequestClass(sdkMessage));
    }
}
