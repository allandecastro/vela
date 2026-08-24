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
