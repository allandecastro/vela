using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Vela.Cli.Tests;

public class ConnectionFailureClassificationTests
{
    [Fact]
    public void Network_errors_are_connection_failures_however_deep_they_are_wrapped()
    {
        Assert.True(DataverseConnectionException.IsConnectionFailure(new DataverseConnectionException("connect failed")));
        Assert.True(DataverseConnectionException.IsConnectionFailure(new HttpRequestException("No such host is known.")));
        Assert.True(DataverseConnectionException.IsConnectionFailure(new WebException("The remote name could not be resolved")));
        Assert.True(DataverseConnectionException.IsConnectionFailure(new TimeoutException()));
        Assert.True(DataverseConnectionException.IsConnectionFailure(new EndpointNotFoundException("no endpoint")));
        Assert.True(DataverseConnectionException.IsConnectionFailure(
            new InvalidOperationException("outer", new Exception("middle", new SocketException(10061)))));
        Assert.True(DataverseConnectionException.IsConnectionFailure(
            new AggregateException(new InvalidOperationException("other"), new HttpRequestException("reset"))));
    }

    [Fact]
    public void A_fault_the_service_answered_with_is_not_a_connection_failure()
    {
        Assert.False(DataverseConnectionException.IsConnectionFailure(
            new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = "no privilege" })));
        Assert.False(DataverseConnectionException.IsConnectionFailure(new FaultException("fault")));
        Assert.False(DataverseConnectionException.IsConnectionFailure(
            new InvalidOperationException("outer", new FaultException<OrganizationServiceFault>(new OrganizationServiceFault()))));
        Assert.False(DataverseConnectionException.IsConnectionFailure(new InvalidOperationException("bad state")));
        Assert.False(DataverseConnectionException.IsConnectionFailure(new AggregateException(new InvalidOperationException("x"))));
    }
}
