using System;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class AssemblyVersionGuardTests
{
    // A real assembly on disk so the guard can read a genuine AssemblyVersion.
    private static string SomeDll => typeof(AssemblyVersionGuardTests).Assembly.Location;
    private static string DllVersion => AssemblyName.GetAssemblyName(SomeDll).Version!.ToString();

    private static Mock<IOrganizationService> ServiceWithAssembly(Guid id, string? liveVersion)
    {
        Mock<IOrganizationService> mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) =>
            {
                EntityCollection ec = new EntityCollection();
                if ((q as QueryExpression)?.EntityName == "pluginassembly" && liveVersion != null)
                {
                    ec.Entities.Add(new Entity("pluginassembly", id));
                }

                return ec;
            });
        mock.Setup(s => s.Retrieve("pluginassembly", id, It.IsAny<ColumnSet>()))
            .Returns(new Entity("pluginassembly", id) { ["version"] = liveVersion });
        return mock;
    }

    [Fact]
    public void Throws_when_a_new_type_is_added_under_an_unchanged_version()
    {
        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> mock = ServiceWithAssembly(id, DllVersion); // live version == DLL version
        AssemblyPayload payload = new AssemblyPayload("Zava.Plugins", SomeDll);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            DataverseRegistration.GuardAgainstStaleAssemblyVersion(mock.Object, payload, new[] { "Zava.Plugins.NewPlugin" }));
        Assert.Contains("version is unchanged", ex.Message);
        Assert.Contains("Zava.Plugins.NewPlugin", ex.Message);
    }

    [Fact]
    public void Does_not_throw_when_the_version_was_bumped()
    {
        Guid id = Guid.NewGuid();
        Mock<IOrganizationService> mock = ServiceWithAssembly(id, "99.0.0.0"); // different from the DLL version
        AssemblyPayload payload = new AssemblyPayload("Zava.Plugins", SomeDll);

        DataverseRegistration.GuardAgainstStaleAssemblyVersion(mock.Object, payload, new[] { "Zava.Plugins.NewPlugin" });
    }

    [Fact]
    public void Does_not_throw_on_first_registration_of_the_assembly()
    {
        Mock<IOrganizationService> mock = ServiceWithAssembly(Guid.NewGuid(), liveVersion: null); // no existing assembly
        AssemblyPayload payload = new AssemblyPayload("Zava.Plugins", SomeDll);

        DataverseRegistration.GuardAgainstStaleAssemblyVersion(mock.Object, payload, new[] { "Zava.Plugins.NewPlugin" });
    }

    [Fact]
    public void Does_not_throw_when_no_new_types_are_added()
    {
        Mock<IOrganizationService> mock = new Mock<IOrganizationService>(MockBehavior.Strict); // no service calls expected
        AssemblyPayload payload = new AssemblyPayload("Zava.Plugins", SomeDll);

        DataverseRegistration.GuardAgainstStaleAssemblyVersion(mock.Object, payload, Array.Empty<string>());
    }
}
