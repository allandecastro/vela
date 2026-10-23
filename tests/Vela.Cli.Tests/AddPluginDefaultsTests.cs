using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

// `vela add plugin` defaults: the name (<Entity>Plugin), the handler (Handle{Message}, as for `add step`), --handler,
// and the `vela add step` hint when the plugin file already exists. Redirects Console.Error, so it runs in the
// serialized ConsoleRedirect collection.
[Collection("ConsoleRedirect")]
public class AddPluginDefaultsTests
{
    [Fact]
    public void Add_Plugin_WithoutName_IsNamedAfterTheEntity()
    {
        RunInProject((project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "--entity", "Account", "--message", "Delete", "--project", project]));

            string file = Path.Combine(dir, "Account", "Plugins", "AccountPlugin.cs");
            Assert.True(File.Exists(file));
            Assert.Contains("public sealed partial class AccountPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>", File.ReadAllText(file));
        });
    }

    [Fact]
    public void Add_Plugin_WithoutNameOrEntity_Fails_AndWritesNothing()
    {
        RunInProject((project, dir) =>
        {
            (int exit, string error) = CaptureError(() => AddCommand.Run(["plugin", "--message", "Delete", "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains("--entity", error);
            Assert.Empty(Directory.GetDirectories(dir));
        });
    }

    [Theory]
    [InlineData("service")]
    [InlineData("customapi")]
    [InlineData("step")]
    public void Add_OtherKinds_StillRequireAName(string kind)
    {
        RunInProject((project, dir) =>
        {
            (int exit, string error) = CaptureError(() => AddCommand.Run([kind, "--entity", "Account", "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains($"Missing name. Usage: vela add {kind} <Name>", error);
        });
    }

    [Theory]
    [InlineData("Delete", "HandleDelete")]
    [InlineData("Update", "HandleUpdate")]
    [InlineData("Assign", "HandleAssign")]
    public void Add_Plugin_DefaultHandler_IsHandleMessage_LikeAddStep(string message, string handler)
    {
        RunInProject((project, dir) =>
        {
            string[] filter = AddCommand.RequiresFilter(message) ? ["--filtering-attributes", "Name"] : [];
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", message, .. filter, "--project", project]));

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains($"                .Handle({handler});", content);
            Assert.Contains($"private void {handler}({handler}Context ctx)", content);
            Assert.DoesNotContain("OnExecute", content);
        });
    }

    [Fact]
    public void Add_Plugin_Handler_NamesTheMethodAndItsContext()
    {
        RunInProject((project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", "Update", "--filtering-attributes", "Name", "--handler", "OnNameChanged", "--project", project]));

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs"));
            Assert.Contains("                .Handle(OnNameChanged);", content);
            Assert.Contains("private void OnNameChanged(OnNameChangedContext ctx)", content);
            Assert.DoesNotContain("HandleUpdate", content);
        });
    }

    [Theory]
    [InlineData("1Bad")]
    [InlineData("On-Update")]
    [InlineData("Handle Update")]
    public void Add_Plugin_InvalidHandler_Fails_AndWritesNothing(string handler)
    {
        RunInProject((project, dir) =>
        {
            (int exit, string error) = CaptureError(() => AddCommand.Run(["plugin", "AccountStep", "--entity", "Account", "--message", "Delete", "--handler", handler, "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains($"Invalid --handler '{handler}'", error);
            Assert.False(File.Exists(Path.Combine(dir, "Account", "Plugins", "AccountStep.cs")));
        });
    }

    // add plugin never edits an existing class: it names the `vela add step` command that adds the step instead, built
    // from the options given, and that command does add it.
    [Fact]
    public void Add_Plugin_ExistingFile_NamesTheAddStepCommand_AndLeavesTheFile()
    {
        RunInProject((project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "--entity", "Account", "--message", "Delete", "--project", project]));
            string file = Path.Combine(dir, "Account", "Plugins", "AccountPlugin.cs");
            string before = File.ReadAllText(file);

            (int exit, string error) = CaptureError(() => AddCommand.Run(["plugin", "--entity", "Account", "--message", "Update", "--stage", "PreOperation", "--filtering-attributes", "Name,Telephone1", "--handler", "OnNameChanged", "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains($"File already exists: {file}", error);
            string expected = $"vela add step AccountPlugin --entity Account --message Update --handler OnNameChanged --stage PreOperation --filtering-attributes Name,Telephone1 --project {project}";
            Assert.Contains(expected, error);
            Assert.Contains("--force", error);
            Assert.Equal(before, File.ReadAllText(file));

            // The hinted command adds the step to the existing class.
            string[] hinted = expected.Substring("vela add ".Length).Split(' ');
            Assert.Equal(ExitSuccess, AddCommand.Run(hinted));
            string after = File.ReadAllText(file);
            Assert.Contains("RegisterStep().OnUpdate()", after);
            Assert.Contains("private void OnNameChanged(OnNameChangedContext ctx)", after);
            Assert.Contains("private void HandleDelete(HandleDeleteContext ctx)", after);
        });
    }

    [Fact]
    public void Add_Plugin_ExistingFile_HintCarriesOnlyTheOptionsGiven()
    {
        RunInProject((project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "AccountGuard", "--entity", "Account", "--message", "Delete", "--project", project]));

            (int exit, string error) = CaptureError(() => AddCommand.Run(["plugin", "AccountGuard", "--entity", "Account", "--message", "Assign", "--project", project]));

            Assert.Equal(ExitInvalidArguments, exit);
            Assert.Contains($"vela add step AccountGuard --entity Account --message Assign --project {project}\n", error.Replace("\r\n", "\n"));
            Assert.DoesNotContain("--handler", error);
            Assert.DoesNotContain("--stage", error);
            Assert.DoesNotContain("--filtering-attributes", error);
        });
    }

    [Fact]
    public void Add_Plugin_ExistingFile_WithForce_Overwrites()
    {
        RunInProject((project, dir) =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "--entity", "Account", "--message", "Delete", "--project", project]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["plugin", "--entity", "Account", "--message", "Assign", "--project", project, "--force"]));

            string content = File.ReadAllText(Path.Combine(dir, "Account", "Plugins", "AccountPlugin.cs"));
            Assert.Contains("RegisterStep().OnAssign()", content);
            Assert.DoesNotContain("OnDelete()", content);
        });
    }

    [Fact]
    public void AddStepCommandFor_QuotesAValueWithASpace()
        => Assert.Equal(
            "vela add step AccountPlugin --entity Account --message Delete --project \"/my projects/Acme.Plugins.csproj\"",
            AddCommand.AddStepCommandFor(["plugin", "--project", "/my projects/Acme.Plugins.csproj"], "AccountPlugin", "Account", "Delete", []));

    private static (int Exit, string Error) CaptureError(Func<int> run)
    {
        TextWriter original = Console.Error;
        StringWriter capture = new();
        Console.SetError(capture);
        try
        {
            return (run(), capture.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static void RunInProject(Action<string, string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string project = Path.Combine(dir, "Acme.Plugins.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");
            action(project, dir);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
