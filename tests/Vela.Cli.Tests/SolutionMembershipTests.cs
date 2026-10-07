using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// A-44: a Vela component that did not change is never written, so it was never added to <c>--solution</c>. Plan lists
/// it (add-to-solution) and apply adds it with AddSolutionComponent.
/// </summary>
public sealed class SolutionMembershipTests : IDisposable
{
    private const string Solution = "ZavaSales";
    private const string Managed = "Zava.Plugins.AccountPlugin";
    private const string Classic = "Zava.Plugins.ClassicPlugin";
    private const int CustomApiType = 10_101;
    private const int ParameterType = 10_102;
    private const int ResponseType = 10_103;
    private const int PackageType = 10_104;

    private readonly Guid _assemblyId = Guid.NewGuid();
    private readonly Guid _managedTypeId = Guid.NewGuid();
    private readonly Guid _classicTypeId = Guid.NewGuid();
    private readonly Guid _stepId = Guid.NewGuid();
    private readonly Guid _mainOperationStepId = Guid.NewGuid();
    private readonly Guid _apiId = Guid.NewGuid();
    private readonly Guid _parameterId = Guid.NewGuid();
    private readonly Guid _responseId = Guid.NewGuid();
    private readonly Guid _packageId = Guid.NewGuid();
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (string file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private FakeOrgService Org(HashSet<Guid> members, bool resolveTableTypes = true)
    {
        return new FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => FakeOrgService.Collection(new Entity("pluginassembly", _assemblyId) { ["name"] = "Zava.Plugins" }),
                "plugintype" => FakeOrgService.Collection(
                    new Entity("plugintype", _managedTypeId) { ["typename"] = Managed },
                    new Entity("plugintype", _classicTypeId) { ["typename"] = Classic }),
                "sdkmessageprocessingstep" => FakeOrgService.Collection(
                    new Entity("sdkmessageprocessingstep", _stepId) { ["name"] = "Account step", ["stage"] = new OptionSetValue(40) },
                    new Entity("sdkmessageprocessingstep", _mainOperationStepId) { ["name"] = "CustomApi main", ["stage"] = new OptionSetValue(30) }),
                "customapi" => FakeOrgService.Collection(new Entity("customapi", _apiId) { ["uniquename"] = "zava_DoThing" }),
                "customapirequestparameter" => FakeOrgService.Collection(new Entity("customapirequestparameter", _parameterId) { ["uniquename"] = "zava_DoThing.In" }),
                "customapiresponseproperty" => FakeOrgService.Collection(new Entity("customapiresponseproperty", _responseId) { ["name"] = "Out" }),
                "pluginpackage" => FakeOrgService.Collection(new Entity("pluginpackage", _packageId) { ["name"] = "zava_Plugins" }),
                "solution" => FakeOrgService.Collection(new Entity("solution", Guid.NewGuid()) { ["publisherid"] = new EntityReference("publisher", Guid.NewGuid()) }),
                "solutioncomponentdefinition" => resolveTableTypes
                    ? FakeOrgService.Collection(
                        new Entity("solutioncomponentdefinition", Guid.NewGuid()) { ["primaryentityname"] = "customapi", ["solutioncomponenttype"] = CustomApiType },
                        new Entity("solutioncomponentdefinition", Guid.NewGuid()) { ["primaryentityname"] = "customapirequestparameter", ["solutioncomponenttype"] = new OptionSetValue(ParameterType) },
                        new Entity("solutioncomponentdefinition", Guid.NewGuid()) { ["primaryentityname"] = "customapiresponseproperty", ["solutioncomponenttype"] = ResponseType },
                        new Entity("solutioncomponentdefinition", Guid.NewGuid()) { ["primaryentityname"] = "pluginpackage", ["solutioncomponenttype"] = PackageType })
                    : new EntityCollection(),
                "solutioncomponent" => FakeOrgService.Collection(members.Select(id => new Entity("solutioncomponent", Guid.NewGuid()) { ["objectid"] = id }).ToArray()),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id) { ["customizationprefix"] = "zava" },
        };
    }

    private PluginPackagePayload[] Package()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zava_Plugins.{Guid.NewGuid():N}.nupkg");
        _files.Add(path);
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("zava_Plugins.nuspec");
        return [new PluginPackagePayload("zava_Plugins", "1.0.0", path)];
    }

    private IReadOnlyList<SolutionMembershipAction> Compute(FakeOrgService org, List<string> info, PluginPackagePayload[]? packages = null) =>
        DataverseRegistration.ComputeSolutionMembershipActions(org, Solution, ["Zava.Plugins", "zava.plugins"], packages ?? [], [Classic], info);

    [Fact]
    public void Every_owned_component_outside_the_solution_is_listed_with_its_component_type()
    {
        FakeOrgService org = Org(members: []);
        List<string> info = [];

        IReadOnlyList<SolutionMembershipAction> actions = Compute(org, info, Package());

        Assert.Empty(info);
        Assert.Contains(actions, a => a is { Kind: "package", Name: "zava_Plugins", ComponentType: PackageType } && a.ComponentId == _packageId);
        Assert.Contains(actions, a => a is { Kind: "assembly", Name: "Zava.Plugins", ComponentType: DataverseRegistration.PluginAssemblyComponentType } && a.ComponentId == _assemblyId);
        Assert.Contains(actions, a => a is { Kind: "step", Name: "Account step", ComponentType: DataverseRegistration.SdkMessageProcessingStepComponentType } && a.ComponentId == _stepId);
        Assert.Contains(actions, a => a is { Kind: "custom-api", Name: "zava_DoThing", ComponentType: CustomApiType } && a.ComponentId == _apiId);
        Assert.Contains(actions, a => a is { Kind: "custom-api-parameter", Name: "zava_DoThing.In", ComponentType: ParameterType } && a.ComponentId == _parameterId);
        Assert.Contains(actions, a => a is { Kind: "custom-api-response-property", Name: "Out", ComponentType: ResponseType } && a.ComponentId == _responseId);
        Assert.DoesNotContain(actions, a => a.ComponentId == _mainOperationStepId); // platform-managed, not ours
        Assert.Equal(6, actions.Count); // the assembly listed twice under different casing is read once
    }

    [Fact]
    public void Steps_and_custom_apis_are_read_for_the_managed_plugin_types_only()
    {
        FakeOrgService org = Org(members: []);

        Compute(org, []);

        foreach (QueryExpression query in org.Queries.Where(q => q.EntityName is "sdkmessageprocessingstep" or "customapi"))
        {
            ConditionExpression condition = Assert.Single(query.Criteria.Conditions);
            Assert.Equal([_managedTypeId], condition.Values.Cast<Guid>().ToArray()); // the classic type is excluded
        }
    }

    [Fact]
    public void Components_already_in_the_solution_are_not_listed()
    {
        FakeOrgService org = Org(members: [_assemblyId, _stepId, _apiId, _parameterId, _responseId]);

        IReadOnlyList<SolutionMembershipAction> actions = Compute(org, []);

        Assert.Empty(actions);
        QueryExpression membership = Assert.Single(org.Queries, q => q.EntityName == "solutioncomponent");
        LinkEntity solution = Assert.Single(membership.LinkEntities);
        Assert.Equal("solution", solution.LinkToEntityName);
        Assert.Equal(Solution, Assert.Single(solution.LinkCriteria.Conditions).Values.Single());
    }

    [Fact]
    public void A_table_component_type_that_cannot_be_resolved_is_skipped_and_reported()
    {
        FakeOrgService org = Org(members: [], resolveTableTypes: false);
        List<string> info = [];

        IReadOnlyList<SolutionMembershipAction> actions = Compute(org, info, Package());

        Assert.Equal(["assembly", "step"], actions.Select(a => a.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(4, info.Count);
        Assert.Contains(info, i => i.Contains("'customapi'") && i.Contains("could not be checked"));
        Assert.Contains(info, i => i.Contains("'pluginpackage'"));
    }

    [Fact]
    public void Nothing_found_queries_no_membership()
    {
        var org = new FakeOrgService(); // no assembly, no package

        IReadOnlyList<SolutionMembershipAction> actions = DataverseRegistration.ComputeSolutionMembershipActions(org, Solution, ["Zava.Plugins"], [], null, []);

        Assert.Empty(actions);
        Assert.DoesNotContain(org.Queries, q => q.EntityName == "solutioncomponent");
    }

    [Fact]
    public void AddComponentsToSolution_issues_one_request_per_component_without_required_components()
    {
        var org = new FakeOrgService();
        Guid id = Guid.NewGuid();

        DataverseRegistration.AddComponentsToSolution(org, Solution, [new SolutionMembershipAction("step", "Account step", 92, id)], verbose: false);

        AddSolutionComponentRequest request = Assert.IsType<AddSolutionComponentRequest>(Assert.Single(org.Executed));
        Assert.Equal(92, request.ComponentType);
        Assert.Equal(id, request.ComponentId);
        Assert.Equal(Solution, request.SolutionUniqueName);
        Assert.False(request.AddRequiredComponents);
    }
}
