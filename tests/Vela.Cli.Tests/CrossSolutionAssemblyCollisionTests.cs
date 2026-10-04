using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;
using TestSupport = Vela.Cli.Tests.TestSupport;

namespace Vela.Cli.Tests;

public sealed class CrossSolutionAssemblyCollisionTests
{
    private static readonly AssemblyPayload[] Payload = [new AssemblyPayload("Zava.Plugins", "/tmp/Zava.Plugins.dll")];

    [Fact]
    public void Flags_an_existing_assembly_that_is_not_in_the_target_solution()
    {
        Guid assemblyId = Guid.NewGuid();
        TestSupport.FakeOrgService service = new()
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", assemblyId) { ["name"] = "Zava.Plugins" }),
                "solutioncomponent" => new EntityCollection(),   // not a member of the target solution
                _ => new EntityCollection(),
            },
        };

        Assert.Equal(["Zava.Plugins"], DataverseRegistration.FindCrossSolutionAssemblyCollisions(service, Payload, "Sales"));

        // The membership check must query this assembly's id, joined to the solution on solutionid, filtered by the
        // target solution's unique name - assert the query shape so the test fails if any of those is wrong.
        QueryExpression membership = service.Queries.Single(q => q.EntityName == "solutioncomponent");
        Assert.Contains(membership.Criteria.Conditions, c => c.AttributeName == "objectid" && Equals(c.Values[0], assemblyId));
        LinkEntity link = Assert.Single(membership.LinkEntities);
        Assert.Equal("solution", link.LinkToEntityName);
        Assert.Equal("solutionid", link.LinkFromAttributeName);
        Assert.Equal("solutionid", link.LinkToAttributeName);
        Assert.Contains(link.LinkCriteria.Conditions, c => c.AttributeName == "uniquename" && Equals(c.Values[0], "Sales"));
    }

    [Fact]
    public void Is_silent_when_the_assembly_is_in_the_target_solution()
    {
        TestSupport.FakeOrgService service = new()
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "Zava.Plugins" }),
                "solutioncomponent" => TestSupport.FakeOrgService.Collection(new Entity("solutioncomponent", Guid.NewGuid())),   // member of the target solution
                _ => new EntityCollection(),
            },
        };

        Assert.Empty(DataverseRegistration.FindCrossSolutionAssemblyCollisions(service, Payload, "Sales"));
    }

    [Fact]
    public void Is_silent_on_a_first_deploy_when_the_assembly_does_not_exist()
    {
        // Default OnRetrieveMultiple returns an empty page, so the assembly is treated as new.
        TestSupport.FakeOrgService service = new();

        Assert.Empty(DataverseRegistration.FindCrossSolutionAssemblyCollisions(service, Payload, "Sales"));
    }
}
