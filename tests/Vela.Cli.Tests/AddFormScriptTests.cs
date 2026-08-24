using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>Integration tests for <c>vela add form-script &lt;entity&gt;</c> against a scaffolded web-resources project.</summary>
public class AddFormScriptTests
{
    [Fact]
    public void Add_FormScript_Creates_Files_And_Registers_The_Entry()
    {
        RunInWebResourcesProject(dir =>
        {
            int exit = AddCommand.Run(["form-script", "contact", "--project", dir]);
            Assert.Equal(ExitSuccess, exit);

            string script = Path.Combine(dir, "src", "contact", "Forms", "main.ts");
            string test = Path.Combine(dir, "src", "contact", "Forms", "main.test.ts");
            Assert.True(File.Exists(script));
            Assert.True(File.Exists(test));

            Assert.Contains("export function onLoad(", File.ReadAllText(script));
            Assert.Contains("export function onSave(", File.ReadAllText(script));
            Assert.Contains("import { onLoad } from './main';", File.ReadAllText(test));

            string entries = File.ReadAllText(Path.Combine(dir, "web-resources.entries.ts"));
            Assert.Contains("'adc_/js/contact/Forms/main': 'src/contact/Forms/main.ts',", entries);
        });
    }

    [Fact]
    public void Add_FormScript_Lowercases_The_Entity()
    {
        RunInWebResourcesProject(dir =>
        {
            int exit = AddCommand.Run(["form-script", "Contact", "--project", dir]);
            Assert.Equal(ExitSuccess, exit);

            Assert.True(File.Exists(Path.Combine(dir, "src", "contact", "Forms", "main.ts")));
            Assert.Contains("'adc_/js/contact/Forms/main'", File.ReadAllText(Path.Combine(dir, "web-resources.entries.ts")));
        });
    }

    [Fact]
    public void Add_FormScript_Is_Idempotent()
    {
        RunInWebResourcesProject(dir =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["form-script", "contact", "--project", dir]));
            string afterFirst = File.ReadAllText(Path.Combine(dir, "web-resources.entries.ts"));

            // A second run touches nothing new and still succeeds.
            Assert.Equal(ExitSuccess, AddCommand.Run(["form-script", "contact", "--project", dir]));
            string afterSecond = File.ReadAllText(Path.Combine(dir, "web-resources.entries.ts"));

            Assert.Equal(afterFirst, afterSecond);
            // The entry appears exactly once.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(afterSecond, "adc_/js/contact/Forms/main").Count);
        });
    }

    [Fact]
    public void Add_FormScript_InvalidEntity_Fails()
    {
        RunInWebResourcesProject(dir =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["form-script", "9bad", "--project", dir]));
        });
    }

    [Fact]
    public void Add_FormScript_MissingEntity_Fails()
    {
        RunInWebResourcesProject(dir =>
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["form-script", "--project", dir]));
        });
    }

    [Fact]
    public void Add_FormScript_NotAWebResourcesProject_Fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-wr-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["form-script", "contact", "--project", dir]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Scaffolds a minimal web-resources project (tsup.config.ts + web-resources.entries.ts) into a temp dir.
    private static void RunInWebResourcesProject(Action<string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-wr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tsup.config.ts"), ScaffoldTemplates.WebResourcesTsupConfig("adc.sales", "dist"));
            File.WriteAllText(Path.Combine(dir, "web-resources.entries.ts"), ScaffoldTemplates.WebResourcesEntries("adc.sales"));
            action(dir);
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
