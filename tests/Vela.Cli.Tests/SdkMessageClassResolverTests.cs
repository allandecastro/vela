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
    [InlineData("QualifyLead")]        // Microsoft.Crm.Sdk.Messages.QualifyLeadRequest (curated supplement)
    [InlineData("WinOpportunity")]     // Microsoft.Crm.Sdk.Messages.WinOpportunityRequest (curated supplement)
    [InlineData("RenewContract")]      // backlog candidate, Crm.Sdk-backed (supplement)
    [InlineData("qualifylead")]        // supplement lookup is case-insensitive
    public void HasSdkRequestClass_True_ForCuratedCrmSdkMessages(string messageName)
    {
        // Not found by reflection (Microsoft.CrmSdk.CoreAssemblies is deliberately not referenced), but skipped
        // via the curated Crm.Sdk supplement so ALL mode doesn't emit a duplicate wrapper.
        Assert.True(SdkMessageClassResolver.HasSdkRequestClass(messageName));
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
