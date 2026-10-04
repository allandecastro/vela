using System;
using Microsoft.Xrm.Sdk;
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
        TestSupport.FakeOrgService service = new()
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "Zava.Plugins" }),
                "solutioncomponent" => new EntityCollection(),   // not a member of the target solution
                _ => new EntityCollection(),
            },
        };

        Assert.Equal(["Zava.Plugins"], DataverseRegistration.FindCrossSolutionAssemblyCollisions(service, Payload, "Sales"));
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
