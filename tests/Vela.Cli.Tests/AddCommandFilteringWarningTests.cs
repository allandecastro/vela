using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

// Redirects Console.Error, so it runs in the serialized ConsoleRedirect collection.
[Collection("ConsoleRedirect")]
public class AddCommandFilteringWarningTests
{
    // A valid C# identifier passed to --filtering-attributes may still not be a real early-bound property
    // (e.g. a wrong-case 'name' vs the generated 'Name'), which compiles against the base Entity but not the
    // property, so the mistake only surfaces as a later build error. When the model is available, `add plugin`
    // warns and suggests the correct casing instead of silently emitting non-compiling code.
    [Fact]
    public void Add_Plugin_WrongCaseFilteringAttribute_WarnsWithSuggestion()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TextWriter original = Console.Error;
        StringWriter capture = new StringWriter();
        try
        {
            File.WriteAllText(Path.Combine(root, "Demo.sln"), string.Empty);
            string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
            Directory.CreateDirectory(pluginsDir);
            string project = Path.Combine(pluginsDir, "Acme.Plugins.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");

            string modelsDir = Path.Combine(root, "src", "Acme.Shared", "Models", "Generated");
            Directory.CreateDirectory(modelsDir);
            File.WriteAllText(Path.Combine(modelsDir, "Account.cs"),
                "public sealed class Account : Entity\n{\n    [AttributeLogicalName(\"name\")]\n    public string? Name\n    {\n        get; set;\n    }\n}\n");

            Console.SetError(capture);
            int exit = AddCommand.Run(["plugin", "AccountWarn", "--entity", "Account", "--message", "Update", "--filtering-attributes", "name", "--project", project]);
            Console.SetError(original);

            // When the model is available, a wrong-case value is corrected to the real property so the emitted
            // FilterOn compiles; a note explains the correction.
            Assert.Equal(ExitSuccess, exit);
            string note = capture.ToString();
            Assert.Contains("filtering attribute 'name'", note);
            Assert.Contains("corrected to 'Name'", note);

            string content = File.ReadAllText(Path.Combine(pluginsDir, "Account", "Plugins", "AccountWarn.cs"));
            Assert.Contains(".FilterOn(e => e.Name)", content);
        }
        finally
        {
            Console.SetError(original);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    // A column logical name maps to its property through the model's [AttributeLogicalName], even when the property is
    // not just the logical name re-cased (the generated FilterOn would not compile otherwise).
    [Fact]
    public void Add_Plugin_LogicalNameFilteringAttribute_MapsToItsProperty()
    {
        RunWithModel("Demo.sln",
            "public sealed class Account : Entity\n{\n    [AttributeLogicalName(\"telephone1\")]\n    [MaxLength(50)]\n    public string? MainPhone\n    {\n        get; set;\n    }\n}\n",
            (project, pluginsDir, stderr) =>
            {
                Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountPhone", "--entity", "Account", "--message", "Update", "--filtering-attributes", "telephone1", "--project", project]));
                Assert.Contains("corrected to 'MainPhone'", stderr());
                Assert.Contains(".FilterOn(e => e.MainPhone)", File.ReadAllText(Path.Combine(pluginsDir, "Account", "Plugins", "AccountPhone.cs")));
            });
    }

    // The solution root that bounds the model search may be a .slnx.
    [Fact]
    public void Add_Plugin_FindsTheModelUnderAnSlnxRoot()
    {
        RunWithModel("Demo.slnx",
            "public sealed class Account : Entity\n{\n    [AttributeLogicalName(\"name\")]\n    public string? Name\n    {\n        get; set;\n    }\n}\n",
            (project, pluginsDir, _) =>
            {
                Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountSlnx", "--entity", "Account", "--message", "Update", "--filtering-attributes", "name", "--project", project]));
                Assert.Contains(".FilterOn(e => e.Name)", File.ReadAllText(Path.Combine(pluginsDir, "Account", "Plugins", "AccountSlnx.cs")));
            });
    }

    // Without a model, an all-lower-case value is a logical name nothing can map: emitting e => e.name would not compile,
    // so add plugin and add step refuse it and say how to fix it. A property-cased value is still accepted.
    [Theory]
    [InlineData("plugin")]
    [InlineData("step")]
    public void Add_LogicalNameFilteringAttribute_WithoutAModel_Fails(string kind)
    {
        RunWithModel("Demo.sln", model: null, (project, pluginsDir, stderr) =>
        {
            if (kind == "step")
            {
                Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountRouter", "--entity", "Account", "--message", "Delete", "--project", project]));
            }

            string name = kind == "step" ? "AccountRouter" : "AccountNoModel";
            string file = Path.Combine(pluginsDir, "Account", "Plugins", $"{name}.cs");
            string before = File.Exists(file) ? File.ReadAllText(file) : string.Empty;

            Assert.Equal(ExitInvalidArguments, AddCommand.Run([kind, name, "--entity", "Account", "--message", "Update", "--filtering-attributes", "name", "--project", project]));
            Assert.Contains("no early-bound model for Account", stderr());
            Assert.Contains("vela models", stderr());
            Assert.Equal(before, File.Exists(file) ? File.ReadAllText(file) : string.Empty);

            Assert.Equal(ExitSuccess, AddCommand.Run([kind, name, "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--project", project, .. kind == "plugin" ? new[] { "--force" } : []]));
            Assert.Contains(".FilterOn(e => e.Name)", File.ReadAllText(file));
        });
    }

    // A solution (solutionFile at its root) with a plugins project and, unless null, a generated Account model.
    private static void RunWithModel(string solutionFile, string? model, Action<string, string, Func<string>> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TextWriter original = Console.Error;
        StringWriter capture = new StringWriter();
        try
        {
            File.WriteAllText(Path.Combine(root, solutionFile), string.Empty);
            string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
            Directory.CreateDirectory(pluginsDir);
            string project = Path.Combine(pluginsDir, "Acme.Plugins.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");
            if (model is not null)
            {
                string modelsDir = Path.Combine(root, "src", "Acme.Shared", "Models", "Generated");
                Directory.CreateDirectory(modelsDir);
                File.WriteAllText(Path.Combine(modelsDir, "Account.cs"), model);
            }

            Console.SetError(capture);
            action(project, pluginsDir, () => capture.ToString());
        }
        finally
        {
            Console.SetError(original);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    // When the attribute matches a real property, no warning is emitted.
    [Fact]
    public void Add_Plugin_CorrectFilteringAttribute_NoWarning()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TextWriter original = Console.Error;
        StringWriter capture = new StringWriter();
        try
        {
            File.WriteAllText(Path.Combine(root, "Demo.sln"), string.Empty);
            string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
            Directory.CreateDirectory(pluginsDir);
            string project = Path.Combine(pluginsDir, "Acme.Plugins.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");

            string modelsDir = Path.Combine(root, "src", "Acme.Shared", "Models", "Generated");
            Directory.CreateDirectory(modelsDir);
            File.WriteAllText(Path.Combine(modelsDir, "Account.cs"),
                "public sealed class Account : Entity\n{\n    [AttributeLogicalName(\"name\")]\n    public string? Name\n    {\n        get; set;\n    }\n}\n");

            Console.SetError(capture);
            int exit = AddCommand.Run(["plugin", "AccountOk", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--project", project]);
            Console.SetError(original);

            Assert.Equal(ExitSuccess, exit);
            Assert.DoesNotContain("may not compile", capture.ToString());
        }
        finally
        {
            Console.SetError(original);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
