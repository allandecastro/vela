using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

// --entity is emitted as a type (EntityPluginBase<T>, BoundTo<T>, new T { ... }) and names the <Table>/ folder, so a table
// logical name resolves to the generated model's class through the model's EntityLogicalNameValue, the way
// --filtering-attributes maps a column logical name to its property. Redirects Console.Error, so it runs in the
// serialized ConsoleRedirect collection.
[Collection("ConsoleRedirect")]
public class AddCommandEntityResolutionTests
{
    private const string AccountModel =
        "namespace Acme.Shared.Models.Generated\n{\n    [EntityLogicalName(EntityLogicalNameValue)]\n    public partial class Account : Entity\n    {\n" +
        "        public const string EntityLogicalNameValue = \"account\";\n\n        [AttributeLogicalName(\"name\")]\n        public string? Name\n        {\n            get; set;\n        }\n    }\n}\n";

    // A custom table whose class is not its logical name re-cased.
    private const string ProjectModel =
        "namespace Acme.Shared.Models.Generated\n{\n    [EntityLogicalName(EntityLogicalNameValue)]\n    public partial class Project : Entity\n    {\n" +
        "        public const string EntityLogicalNameValue = \"acme_project\";\n    }\n}\n";

    // The logical name and the class name produce the same file, byte for byte, under the class-named folder.
    [Theory]
    [InlineData("plugin", "Account/Plugins/AccountPlugin.cs")]
    [InlineData("service", "Account/Services/AccountRules.cs")]
    [InlineData("customapi", "Account/CustomApis/Recalculate.cs")]
    public void Add_LogicalNameEntity_WritesTheSameFileAsTheClassName(string kind, string relativePath)
    {
        string[] Args(string entity, string project) => kind switch
        {
            "plugin" => ["plugin", "--entity", entity, "--message", "Delete", "--project", project],
            "service" => ["service", "AccountRules", "--entity", entity, "--project", project],
            _ => ["customapi", "Recalculate", "--entity", entity, "--bound", "--prefix", "acme", "--project", project],
        };

        string byClass = string.Empty;
        RunInSolution((plugins, pluginsDir, _, _, _) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(Args("Account", plugins)));
            byClass = File.ReadAllText(Path.Combine(pluginsDir, relativePath));
        });

        RunInSolution((plugins, pluginsDir, _, _, stderr) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(Args("account", plugins)));
            Assert.Equal(byClass, File.ReadAllText(Path.Combine(pluginsDir, relativePath)));
            Assert.Equal(["Account"], Array.ConvertAll(Directory.GetDirectories(pluginsDir), d => Path.GetFileName(d)));
            Assert.Contains("Note: --entity 'account' resolved to the early-bound class 'Account'", stderr());
        });

        Assert.Contains("Account", byClass);
        Assert.DoesNotContain("<account>", byClass);
    }

    [Fact]
    public void Add_Plugin_CustomTableLogicalName_UsesTheModelClass()
    {
        RunInSolution((plugins, pluginsDir, _, _, _) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "--entity", "acme_project", "--message", "Delete", "--project", plugins]));

            string content = File.ReadAllText(Path.Combine(pluginsDir, "Project", "Plugins", "ProjectPlugin.cs"));
            Assert.Contains("namespace Acme.Plugins.Project.Plugins", content);
            Assert.Contains("public sealed partial class ProjectPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Project>(unsecureConfig, secureConfig)", content);
            Assert.DoesNotContain("acme_project", content);
        });
    }

    [Fact]
    public void Add_Plugin_ClassName_IsKept_WithoutANote()
    {
        RunInSolution((plugins, pluginsDir, _, _, stderr) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountGuard", "--entity", "Account", "--message", "Delete", "--project", plugins]));

            Assert.Contains("EntityPluginBase<Account>", File.ReadAllText(Path.Combine(pluginsDir, "Account", "Plugins", "AccountGuard.cs")));
            Assert.DoesNotContain("--entity", stderr());
        });
    }

    [Fact]
    public void Add_Plugin_WrongCaseClassName_IsCorrected()
    {
        RunInSolution((plugins, pluginsDir, _, _, stderr) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountGuard", "--entity", "ACCOUNT", "--message", "Delete", "--project", plugins]));

            Assert.Contains("EntityPluginBase<Account>", File.ReadAllText(Path.Combine(pluginsDir, "Account", "Plugins", "AccountGuard.cs")));
            Assert.Contains("Note: --entity 'ACCOUNT' resolved to the early-bound class 'Account'", stderr());
        });
    }

    // add step locates <Table>/Plugins/<Name>.cs and checks EntityPluginBase<T>: both need the class.
    [Fact]
    public void Add_Step_LogicalNameEntity_FindsTheClassNamedPlugin()
    {
        RunInSolution((plugins, pluginsDir, _, _, _) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountGuard", "--entity", "Account", "--message", "Delete", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["step", "AccountGuard", "--entity", "account", "--message", "Update", "--filtering-attributes", "name", "--project", plugins]));

            Assert.Contains(".FilterOn(e => e.Name)", File.ReadAllText(Path.Combine(pluginsDir, "Account", "Plugins", "AccountGuard.cs")));
        });
    }

    [Fact]
    public void Add_Test_LogicalNameEntity_TypesTheTestWithTheClass()
    {
        RunInSolution((plugins, _, tests, testsDir, _) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountGuard", "--entity", "Account", "--message", "Delete", "--project", plugins]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["test", "AccountGuard", "--entity", "account", "--project", tests]));

            string content = File.ReadAllText(Path.Combine(testsDir, "Account", "Plugins", "AccountGuardTests.cs"));
            Assert.Contains("pipeline.Seed(new Account { Id = id });", content);
            Assert.DoesNotContain("new account", content);
        });
    }

    // No model matches: a lower-case value reads as a logical name nothing maps, and would emit a non-compiling type,
    // so it is refused (as --filtering-attributes refuses an unmappable logical name); a class-cased value is kept.
    [Fact]
    public void Add_Plugin_LogicalNameWithoutAModel_Fails_AndWritesNothing()
    {
        RunInSolution((plugins, pluginsDir, _, _, stderr) =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["plugin", "--entity", "contact", "--message", "Delete", "--project", plugins]));

            Assert.Contains("--entity 'contact': no early-bound model was found whose class or table logical name is 'contact'", stderr());
            Assert.Contains("Run 'vela models' first, or pass the class name.", stderr());
            Assert.False(Directory.Exists(Path.Combine(pluginsDir, "contact")));
            Assert.False(Directory.Exists(Path.Combine(pluginsDir, "Contact")));

            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "--entity", "Contact", "--message", "Delete", "--project", plugins]));
            Assert.Contains("EntityPluginBase<Contact>", File.ReadAllText(Path.Combine(pluginsDir, "Contact", "Plugins", "ContactPlugin.cs")));
        });
    }

    // A solution with a plugins project, a tests project and generated Account and Project models.
    private static void RunInSolution(Action<string, string, string, string, Func<string>> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-add-entity-" + Guid.NewGuid().ToString("N"));
        TextWriter original = Console.Error;
        StringWriter capture = new StringWriter();
        try
        {
            string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
            string testsDir = Path.Combine(root, "tests", "Acme.Plugins.Tests");
            string modelsDir = Path.Combine(root, "src", "Acme.Shared", "Models", "Generated");
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(testsDir);
            Directory.CreateDirectory(modelsDir);
            File.WriteAllText(Path.Combine(root, "Acme.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(modelsDir, "Account.cs"), AccountModel);
            File.WriteAllText(Path.Combine(modelsDir, "Project.cs"), ProjectModel);
            string plugins = Path.Combine(pluginsDir, "Acme.Plugins.csproj");
            File.WriteAllText(plugins, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");
            string tests = Path.Combine(testsDir, "Acme.Plugins.Tests.csproj");
            File.WriteAllText(tests,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins.Tests</RootNamespace></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"..\\..\\src\\Acme.Plugins\\Acme.Plugins.csproj\" /></ItemGroup></Project>");

            Console.SetError(capture);
            action(plugins, pluginsDir, tests, testsDir, () => capture.ToString());
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
