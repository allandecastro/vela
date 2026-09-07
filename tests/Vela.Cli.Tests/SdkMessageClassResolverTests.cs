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
    [InlineData("zava_DoThing")]              // a Custom API name, no SDK type
    [InlineData("DefinitelyNotARealMessage")]
    [InlineData("QualifyLead")]               // Microsoft.Crm.Sdk.Messages.* — NOT referenced, so not detected
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
}
