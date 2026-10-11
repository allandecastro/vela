using Microsoft.Xrm.Sdk;
using Vela.Messages;
using Vela.Tests.TestSupport;
using Xunit;

namespace Vela.Tests;

// The Vela typed requests' Target getter mirrors ctx.GetTarget<T>(): the same conversion and, when Target is absent
// or not an Entity, the same InvalidPluginExecutionException instead of a null.
public sealed class TypedRequestTargetTests
{
    [Fact]
    public void Target_converts_the_entity_to_the_early_bound_type()
    {
        UpdateRequest<TestAccount> request = new UpdateRequest<TestAccount>();
        request.Parameters["Target"] = new Entity("account") { ["name"] = "Contoso" };

        Assert.Equal("Contoso", request.Target.Name);
    }

    [Fact]
    public void Target_throws_when_absent_or_not_an_entity()
    {
        CreateRequest<TestAccount> absent = new CreateRequest<TestAccount>();
        UpsertRequest<TestAccount> reference = new UpsertRequest<TestAccount>();
        reference.Parameters["Target"] = new EntityReference("account", System.Guid.NewGuid());

        InvalidPluginExecutionException ex = Assert.Throws<InvalidPluginExecutionException>(() => absent.Target);
        Assert.Equal("Input parameter 'Target' is not an Entity.", ex.Message);
        Assert.Throws<InvalidPluginExecutionException>(() => reference.Target);
    }
}
