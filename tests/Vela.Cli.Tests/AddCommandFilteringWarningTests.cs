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

            // The file is still generated (the value is emitted as given); only a warning is surfaced.
            Assert.Equal(ExitSuccess, exit);
            string warning = capture.ToString();
            Assert.Contains("filtering attribute 'name'", warning);
            Assert.Contains("Did you mean 'Name'", warning);

            string content = File.ReadAllText(Path.Combine(pluginsDir, "Account", "Plugins", "AccountWarn.cs"));
            Assert.Contains(".FilteringAttributes(e => e.name)", content);
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
