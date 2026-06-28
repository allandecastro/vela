using System;
using System.ServiceModel;
using Vela.Cli;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Cli.Tests;

public class IgnorableExceptionTests
{
    private const string InvalidStageMessage = "Invalid plug-in registration stage.";

    [Fact]
    public void Dataverse_fault_for_invalid_stage_is_ignorable()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault(), InvalidStageMessage);

        Assert.True(DataverseRegistration.IsIgnorableCustomApiStageException(fault));
    }

    [Fact]
    public void Dataverse_fault_wrapped_in_outer_exception_is_ignorable()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault(), InvalidStageMessage);
        var wrapped = new InvalidOperationException("apply failed", fault);

        Assert.True(DataverseRegistration.IsIgnorableCustomApiStageException(wrapped));
    }

    [Fact]
    public void Generic_exception_with_matching_message_is_not_ignorable()
    {
        // A non-Dataverse exception that merely mentions the phrase must NOT be swallowed,
        // otherwise a real programming error would be hidden.
        var ex = new InvalidOperationException(InvalidStageMessage);

        Assert.False(DataverseRegistration.IsIgnorableCustomApiStageException(ex));
    }

    [Fact]
    public void Unrelated_dataverse_fault_is_not_ignorable()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault(), "Some other platform error.");

        Assert.False(DataverseRegistration.IsIgnorableCustomApiStageException(fault));
    }
}
