using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>Integration tests for <c>vela add ribbon-script &lt;entity&gt;</c> against a scaffolded web-resources project.</summary>
public class AddRibbonCommandTests
{
    [Fact]
    public void Add_RibbonScript_Creates_Files_And_Registers_The_Entry()
    {
        RunInWebResourcesProject(dir =>
        {
            int exit = AddCommand.Run(["ribbon-script", "contact", "--project", dir]);
            Assert.Equal(ExitSuccess, exit);

            string commands = Path.Combine(dir, "src", "contact", "Ribbon", "commands.ts");
            string test = Path.Combine(dir, "src", "contact", "Ribbon", "commands.test.ts");
            Assert.True(File.Exists(commands));
            Assert.True(File.Exists(test));

            string src = File.ReadAllText(commands);
            Assert.Contains("import { formCommand, formRule, bootstrapCommands } from 'vela-client/ribbon';", src);
            // The model const name is discovered from the generated barrel (ContactInformation, not ContactMain).
            Assert.Contains("import { ContactInformation } from '@generated';", src);
            Assert.Contains("import type { ContactCmd } from '@generated';", src);
            Assert.Contains("export const commands = bootstrapCommands(", src);
            // The deploy namespace is <Prefix>.Ribbon.<entity>, derived from the project folder name.
            Assert.Contains("namespace: 'Demo.Ribbon.contact'", src);

            string testSrc = File.ReadAllText(test);
            Assert.Contains("fakeFormCommand", testSrc);
            Assert.Contains("import { ContactInformation } from '@generated';", testSrc);

            string entries = File.ReadAllText(Path.Combine(dir, "web-resources.entries.ts"));
            Assert.Contains("'adc_/js/contact/Ribbon/commands': 'src/contact/Ribbon/commands.ts',", entries);
        });
    }

    [Fact]
    public void Add_RibbonScript_Existing_File_Fails_Without_Force()
    {
        RunInWebResourcesProject(dir =>
        {
            Assert.Equal(ExitSuccess, AddCommand.Run(["ribbon-script", "contact", "--project", dir]));
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["ribbon-script", "contact", "--project", dir]));
            Assert.Equal(ExitSuccess, AddCommand.Run(["ribbon-script", "contact", "--project", dir, "--force"]));
        });
    }

    [Fact]
    public void Add_RibbonScript_InvalidEntity_Fails()
    {
        RunInWebResourcesProject(dir =>
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["ribbon-script", "9bad", "--project", dir])));
    }

    [Fact]
    public void Add_RibbonScript_Without_Generated_Main_Form_Fails()
    {
        // A table with no <Entity>Cmd in the barrel (e.g. tables-only generation) cannot be wired to a form command.
        RunInWebResourcesProject(dir =>
            Assert.Equal(ExitInvalidArguments, AddCommand.Run(["ribbon-script", "order", "--project", dir])));
    }

    // Scaffolds a minimal web-resources project inside a Demo.WebResources folder (so the ribbon namespace prefix
    // resolves to "Demo"), with tsup.config.ts + web-resources.entries.ts + a generated barrel exposing contact's
    // Main form model (ContactInformation) and its <Entity>Cmd alias.
    private static void RunInWebResourcesProject(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-wr-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "Demo.WebResources");
        string generated = Path.Combine(dir, "src", "generated");
        Directory.CreateDirectory(generated);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tsup.config.ts"), ScaffoldTemplates.WebResourcesTsupConfig("adc.sales", "dist"));
            File.WriteAllText(Path.Combine(dir, "web-resources.entries.ts"), ScaffoldTemplates.WebResourcesEntries("adc.sales"));
            File.WriteAllText(Path.Combine(generated, "index.ts"),
                "export { ContactInformation } from './forms/contact/information.form';\n" +
                "export type { ContactCmd } from './forms/contact/information.form';\n");
            action(dir);
        }
        finally
        {
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
