using System;
using System.IO;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// Redirects Console.Error, so it runs in the serialized ConsoleRedirect collection.
[Collection("ConsoleRedirect")]
public class AddCommandSetStateNoteTests
{
    // SetStateRequest is deprecated in favour of an Update of statecode: `vela add --message SetState` still renders
    // On<SetStateRequest>(), and notes that an Update step filtered on statecode is the one to prefer.
    [Theory]
    [InlineData("SetState")]
    [InlineData("setstate")]
    public void A_SetState_trigger_notes_that_the_request_is_deprecated(string message)
    {
        string note = CaptureError(() =>
            Assert.Equal("On<Microsoft.Crm.Sdk.Messages.SetStateRequest>()", AddCommand.RegistrationCall(null, message)));

        Assert.Equal(AddCommand.SetStateDeprecatedNote + Environment.NewLine, note);
        Assert.Contains("--message Update --filtering-attributes StateCode", note);
    }

    [Fact]
    public void Add_plugin_on_the_SetStateDynamicEntity_sdkmessage_notes_it_too()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-add-" + Guid.NewGuid().ToString("N"));
        string pluginsDir = Path.Combine(root, "src", "Acme.Plugins");
        Directory.CreateDirectory(pluginsDir);
        try
        {
            File.WriteAllText(Path.Combine(root, "Demo.sln"), string.Empty);
            string project = Path.Combine(pluginsDir, "Acme.Plugins.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Acme.Plugins</RootNamespace></PropertyGroup></Project>");

            string note = CaptureError(() => Assert.Equal(0,
                AddCommand.Run(["plugin", "AccountState", "--entity", "Account", "--message", "SetStateDynamicEntity", "--project", project])));

            Assert.Contains(AddCommand.SetStateDeprecatedNote, note);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "Publish")]   // another SDK request type
    [InlineData("Update", "Update")] // a typed trigger
    public void Other_triggers_get_no_SetState_note(string? known, string message)
    {
        string note = CaptureError(() => AddCommand.RegistrationCall(known, message));

        Assert.DoesNotContain("SetStateRequest is deprecated", note);
    }

    private static string CaptureError(Action action)
    {
        TextWriter original = Console.Error;
        StringWriter capture = new StringWriter();
        try
        {
            Console.SetError(capture);
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return capture.ToString();
    }
}
