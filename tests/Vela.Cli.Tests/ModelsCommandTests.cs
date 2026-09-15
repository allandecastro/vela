using System;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

public class ModelsCommandTests
{
    [Fact]
    public void Run_RejectsUnknownOption_BeforeConnecting()
    {
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--soluton", "Zava"]));
    }

    [Fact]
    public void Run_Help_PrintsUsage_AndSucceeds()
    {
        Assert.Equal(ExitSuccess, ModelsCommand.Run(["--help"]));
    }

    [Fact]
    public void ReadRootRole_returns_null_for_a_missing_role_instead_of_throwing()
    {
        // E5: a missing role must not abort the whole run — ReadRootRole returns null so the caller can skip it,
        // warn, and still generate the roles that resolved.
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => new EntityCollection() };
        Assert.Null(ModelsCommand.ReadRootRole(svc, "Nonexistent Role"));
    }

    [Fact]
    public void ReadRootRole_returns_the_root_role_when_found()
    {
        var id = Guid.NewGuid();
        Entity role = new Entity("role", id);
        role["name"] = "Salesperson";
        role["parentrootroleid"] = new EntityReference("role", id); // its own root
        var svc = new TestSupport.FakeOrgService { OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(role) };

        Entity? found = ModelsCommand.ReadRootRole(svc, "Salesperson");
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    [Fact]
    public void ReadCustomApiParameters_uses_the_name_not_the_dotted_uniquename()
    {
        // Dataverse stores a Custom API parameter's `name` unqualified ("ContactId") and its `uniquename`
        // dotted ("zava_ContactValidate.ContactId"). The generated C# property + the runtime Parameters/Results
        // key must come from `name`; the dotted uniquename would emit an explicit-interface member (CS0538).
        Entity param = new Entity("customapirequestparameter", Guid.NewGuid());
        param["name"] = "ContactId";
        param["uniquename"] = "zava_ContactValidate.ContactId";
        param["type"] = new OptionSetValue(12); // Guid
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(param),
        };

        var result = ModelsCommand.ReadCustomApiParameters(svc, "customapirequestparameter", Guid.NewGuid());

        CustomApiParameter one = Assert.Single(result);
        Assert.Equal("ContactId", one.Name);
        Assert.Equal(12, one.FieldType);
        // The read must request the `name` column and must NOT request `uniquename`.
        QueryExpression q = Assert.Single(svc.Queries);
        Assert.Contains("name", q.ColumnSet.Columns);
        Assert.DoesNotContain("uniquename", q.ColumnSet.Columns);
    }

    [Fact]
    public void ReadCustomApiParameters_reads_isoptional_for_request_parameters()
    {
        Entity required = new Entity("customapirequestparameter", Guid.NewGuid());
        required["name"] = "AccountId";
        required["type"] = new OptionSetValue(12);
        required["isoptional"] = false;
        Entity optional = new Entity("customapirequestparameter", Guid.NewGuid());
        optional["name"] = "AsOfDate";
        optional["type"] = new OptionSetValue(1);
        optional["isoptional"] = true;
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(required, optional),
        };

        var result = ModelsCommand.ReadCustomApiParameters(svc, "customapirequestparameter", Guid.NewGuid());

        Assert.False(result.Single(p => p.Name == "AccountId").IsOptional);
        Assert.True(result.Single(p => p.Name == "AsOfDate").IsOptional);
        Assert.Contains("isoptional", Assert.Single(svc.Queries).ColumnSet.Columns);
    }

    [Fact]
    public void ReadCustomApiParameters_never_requests_isoptional_for_response_properties()
    {
        Entity prop = new Entity("customapiresponseproperty", Guid.NewGuid());
        prop["name"] = "Total";
        prop["type"] = new OptionSetValue(2);
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = _ => TestSupport.FakeOrgService.Collection(prop),
        };

        var result = ModelsCommand.ReadCustomApiParameters(svc, "customapiresponseproperty", Guid.NewGuid());

        // Response properties have no isoptional column, so it is neither requested nor set.
        Assert.False(Assert.Single(result).IsOptional);
        Assert.DoesNotContain("isoptional", Assert.Single(svc.Queries).ColumnSet.Columns);
    }

    [Fact]
    public void Models_Help_Succeeds()
    {
        Assert.Equal(ExitSuccess, ModelsCommand.Run(["--help"]));
    }

    private static string TempDir()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vela-models-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ResolveNamespace_InfersFromSolutionRoot_PreferringSharedProject()
    {
        // A solution root has no top-level .csproj (they live under src/*), so the namespace must be derived
        // from the sibling .sln - preferring the .Shared project - instead of failing "Could not infer".
        string root = TempDir();
        try
        {
            string plugins = System.IO.Path.Combine(root, "src", "Acme.Plugins");
            string shared = System.IO.Path.Combine(root, "src", "Acme.Shared");
            System.IO.Directory.CreateDirectory(plugins);
            System.IO.Directory.CreateDirectory(shared);
            System.IO.File.WriteAllText(System.IO.Path.Combine(plugins, "Acme.Plugins.csproj"),
                "<Project><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");
            System.IO.File.WriteAllText(System.IO.Path.Combine(shared, "Acme.Shared.csproj"),
                "<Project><PropertyGroup><RootNamespace>Acme.Shared</RootNamespace></PropertyGroup></Project>");
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Acme.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Acme.Plugins\", \"src\\Acme.Plugins\\Acme.Plugins.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Acme.Shared\", \"src\\Acme.Shared\\Acme.Shared.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\nEndProject\n");

            string? ns = ModelsCommand.ResolveNamespace(new[] { "--project", root });

            Assert.Equal("Acme.Shared.Models.Generated", ns);
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveNamespace_DoesNotTreat_p_AsProjectAlias()
    {
        // -p is the --package short alias (as in plan/apply), not --project, so it must NOT drive namespace
        // inference. A solution root that --project would resolve returns null when passed via -p.
        string root = TempDir();
        try
        {
            string plugins = System.IO.Path.Combine(root, "src", "Acme.Plugins");
            System.IO.Directory.CreateDirectory(plugins);
            System.IO.File.WriteAllText(System.IO.Path.Combine(plugins, "Acme.Plugins.csproj"),
                "<Project><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Acme.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Acme.Plugins\", \"src\\Acme.Plugins\\Acme.Plugins.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n");

            Assert.Null(ModelsCommand.ResolveNamespace(new[] { "-p", root }));
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveNamespace_ReturnsNull_WhenNoProjectOrSolution()
    {
        string root = TempDir();
        try
        {
            Assert.Null(ModelsCommand.ResolveNamespace(new[] { "--project", root }));
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteModels_Writes_All_Files_And_Returns_Success()
    {
        string dir = TempDir();
        try
        {
            int exit = ModelsCommand.WriteModels(
                new[] { new GeneratedModel("a.ts", "// a"), new GeneratedModel("b.ts", "// b") },
                dir, overwrite: false, json: false, summarySuffix: "");

            Assert.Equal(ExitSuccess, exit);
            Assert.Equal("// a", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "a.ts")));
            Assert.Equal("// b", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "b.ts")));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteModels_Refuses_Existing_File_Without_Overwrite()
    {
        string dir = TempDir();
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "a.ts"), "old");

            int exit = ModelsCommand.WriteModels(
                new[] { new GeneratedModel("a.ts", "// new") }, dir, overwrite: false, json: false, summarySuffix: "");

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Equal("old", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "a.ts"))); // untouched
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteModels_Overwrite_Replaces_Existing_File()
    {
        string dir = TempDir();
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "a.ts"), "old");

            int exit = ModelsCommand.WriteModels(
                new[] { new GeneratedModel("a.ts", "// new") }, dir, overwrite: true, json: false, summarySuffix: "");

            Assert.Equal(ExitSuccess, exit);
            Assert.Equal("// new", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "a.ts")));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteModels_Fails_On_Duplicate_File_Names()
    {
        string dir = TempDir();
        try
        {
            int exit = ModelsCommand.WriteModels(
                new[] { new GeneratedModel("dup.ts", "x"), new GeneratedModel("dup.ts", "y") },
                dir, overwrite: true, json: false, summarySuffix: "");

            Assert.Equal(ExitInvalidArguments, exit);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildGenerationReport_Lists_Files_In_Order()
    {
        string json = ModelsCommand.BuildGenerationReport(
            "/tmp/out",
            new[]
            {
                new GeneratedModel("account.main.form.ts", "// a"),
                new GeneratedModel("option-sets.ts", "// b"),
            });

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("/tmp/out", doc.RootElement.GetProperty("outDir").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(
            new[] { "account.main.form.ts", "option-sets.ts" },
            doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Models_Forms_WithNonTsLang_Fails_BeforeConnecting()
    {
        // --forms only emits TypeScript today; --lang csharp with --forms is refused up front.
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--forms", "--lang", "csharp", "--entities", "account"]));
    }

    [Fact]
    public void Models_Forms_WithoutTableScope_Fails_BeforeConnecting()
    {
        // A Custom API scope satisfies the generic scope gate but not --forms, which needs a table scope.
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--forms", "--lang", "ts", "--custom-apis", "zava_DoThing"]));
    }

    [Fact]
    public void Models_LangTs_WithoutForms_Fails_BeforeConnecting()
    {
        // --lang only applies to --forms; using it on the C# path is an error, not a silent ignore.
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--entities", "account", "--lang", "ts"]));
    }

    [Fact]
    public void Models_WithoutScope_Fails_BeforeConnecting()
    {
        // No --solution / --entities → refuses up front (never touches the connection).
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--connection-string", "irrelevant"]));
    }

    [Fact]
    public void Models_CustomApisCountAsScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // --custom-apis is a valid scope; the run only fails here because the namespace can't be inferred.
        int exit = ModelsCommand.Run(["--custom-apis", "zava_DoThing", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void Models_MessagesCountAsScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // --messages is a valid scope; the run only fails here because the namespace can't be inferred.
        int exit = ModelsCommand.Run(["--messages", "QualifyLead", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void Models_WithScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // Scope is valid, but the namespace can't be inferred (bad --project) → fails before connecting.
        int exit = ModelsCommand.Run(["--entities", "account", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void Models_RolesCountAsScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // --roles is a valid scope; the run only fails here because the namespace can't be inferred.
        int exit = ModelsCommand.Run(["--roles", "Salesperson", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }

    [Fact]
    public void Models_SolutionRoles_WithoutSolution_Fails_BeforeConnecting()
    {
        // --solution-roles has nothing to enumerate without --solution → refused up front.
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(["--solution-roles", "--connection-string", "irrelevant"]));
    }

    [Fact]
    public void Models_SolutionRoles_WithSolution_IsAScope_ButUnresolvableNamespace_Fails_BeforeConnecting()
    {
        // --solution-roles + --solution is a valid scope; it only fails because the namespace can't be inferred.
        int exit = ModelsCommand.Run(["--solution-roles", "--solution", "MySolution", "--project", "/no/such/project.csproj"]);
        Assert.Equal(ExitInvalidArguments, exit);
    }
}
