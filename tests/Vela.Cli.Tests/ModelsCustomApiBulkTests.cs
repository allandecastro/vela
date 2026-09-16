using System;
using System.IO;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

public sealed class ModelsCustomApiBulkTests
{
    private static Entity ApiRow(Guid id, string uniqueName, int bindingType, string? boundEntity, int allowedStep, bool isPrivate)
    {
        var e = new Entity("customapi", id)
        {
            ["uniquename"] = uniqueName,
            ["bindingtype"] = new OptionSetValue(bindingType),
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(allowedStep),
            ["isprivate"] = isPrivate,
        };
        if (boundEntity != null)
        {
            e["boundentitylogicalname"] = boundEntity;
        }

        return e;
    }

    private static Entity ReqParamRow(Guid apiId, string name, int type, bool isOptional) => new Entity("customapirequestparameter", Guid.NewGuid())
    {
        ["name"] = name,
        ["type"] = new OptionSetValue(type),
        ["isoptional"] = isOptional,
        ["customapiid"] = new EntityReference("customapi", apiId),
    };

    private static Entity RespPropRow(Guid apiId, string name, int type) => new Entity("customapiresponseproperty", Guid.NewGuid())
    {
        ["name"] = name,
        ["type"] = new OptionSetValue(type),
        ["customapiid"] = new EntityReference("customapi", apiId),
    };

    // #211: bare --custom-apis read every API in a fixed number of queries (one per table), not one-plus-two per
    // API, so it no longer times out on large environments.
    [Fact]
    public void ReadAllCustomApis_reads_in_bulk_and_joins_by_customapiid()
    {
        Guid alpha = Guid.NewGuid();
        Guid beta = Guid.NewGuid();
        var svc = new FakeOrgService();
        svc.RouteRetrieveMultiple(entity => entity switch
        {
            "customapi" => FakeOrgService.Collection(
                ApiRow(alpha, "zava_Alpha", bindingType: 1, boundEntity: "account", allowedStep: 0, isPrivate: false),
                ApiRow(beta, "zava_Beta", bindingType: 0, boundEntity: null, allowedStep: 2, isPrivate: true)),
            "customapirequestparameter" => FakeOrgService.Collection(
                ReqParamRow(alpha, "AsOfDate", type: 2, isOptional: true),
                ReqParamRow(alpha, "AccountId", type: 8, isOptional: false)),
            "customapiresponseproperty" => FakeOrgService.Collection(
                RespPropRow(alpha, "Total", type: 0)),
            _ => new EntityCollection(),
        });

        var result = ModelsCommand.ReadAllCustomApis(svc);

        // Exactly three queries, one per table, regardless of how many APIs exist (no N+1).
        Assert.Equal(3, svc.Queries.Count);
        Assert.Equal(
            new[] { "customapi", "customapirequestparameter", "customapiresponseproperty" },
            svc.Queries.Select(q => q.EntityName).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Ordered by unique name; parameters joined by customapiid and ordered by name.
        Assert.Equal(new[] { "zava_Alpha", "zava_Beta" }, result.Select(m => m.UniqueName).ToArray());
        var a = result[0];
        Assert.True(a.IsBoundToEntity);
        Assert.Equal("account", a.BoundEntityLogicalName);
        Assert.Equal(new[] { "AccountId", "AsOfDate" }, a.RequestParameters.Select(p => p.Name).ToArray());
        Assert.True(a.RequestParameters.Single(p => p.Name == "AsOfDate").IsOptional);
        Assert.Equal(new[] { "Total" }, a.ResponseProperties.Select(p => p.Name).ToArray());

        var b = result[1];
        Assert.False(b.IsBoundToEntity);
        Assert.True(b.IsPrivate);
        Assert.Empty(b.RequestParameters);
        Assert.Empty(b.ResponseProperties);
    }

    // #210: with no --out, C# models default into the inferred project's Models/Generated folder (matching the
    // inferred namespace), not the project root. An explicit --out or --namespace is respected (falls back).
    [Fact]
    public void ResolveCSharpOutputDir_defaults_to_project_Models_Generated()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string project = Path.Combine(dir, "Acme.Shared.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Shared</RootNamespace></PropertyGroup></Project>");

            string resolved = ModelsCommand.ResolveCSharpOutputDir(["--project", project], "FALLBACK");
            Assert.Equal(Path.Combine(dir, "Models", "Generated"), resolved);

            // Explicit --out or --namespace: keep the caller's fallback.
            Assert.Equal("FALLBACK", ModelsCommand.ResolveCSharpOutputDir(["--project", project, "--out", "x"], "FALLBACK"));
            Assert.Equal("FALLBACK", ModelsCommand.ResolveCSharpOutputDir(["--project", project, "--namespace", "N"], "FALLBACK"));

            // No project resolvable: fall back.
            string emptyDir = Path.Combine(Path.GetTempPath(), "vela-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);
            try
            {
                Assert.Equal("FALLBACK", ModelsCommand.ResolveCSharpOutputDir(["--project", emptyDir], "FALLBACK"));
            }
            finally
            {
                Directory.Delete(emptyDir, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
