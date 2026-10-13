using System;
using System.IO;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Interactive <see cref="ScaffoldOptions.TryParse"/> paths: prompts are read from redirected stdin, so this must
/// run serially (it swaps the process-global <see cref="Console.In"/>). Grouped with the other console-redirecting
/// tests so it never races them.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ScaffoldOptionsInteractiveTests
{
    // Runs TryParse with the given stdin lines feeding the interactive prompts.
    private static bool Parse(string stdin, string[] args, out ScaffoldOptions options)
    {
        TextReader original = Console.In;
        Console.SetIn(new StringReader(stdin));
        try
        {
            return ScaffoldOptions.TryParse(args, out options, out _);
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Fact]
    public void Interactive_WebResources_Yes_Prompts_Namespace_And_Folder()
    {
        // Prompts in order: demos, integrations, tests, console, web-resources, then namespace + folder.
        bool ok = Parse("y\nn\nn\nn\ny\nadc\ndist\n",
            ["--name", "DemoSol", "--prefix", "Demo", "--output", ".", "--interactive"],
            out ScaffoldOptions options);

        Assert.True(ok);
        Assert.False(options.IncludeIntegrations);
        Assert.False(options.IncludeTests);
        Assert.False(options.IncludeConsole);
        Assert.True(options.IncludeWebResources);
        Assert.Equal("adc", options.WebResourceNamespace);
        Assert.Equal("dist", options.WebResourcesFolder);
    }

    [Fact]
    public void Interactive_WebResources_Yes_Empty_Answers_Use_Defaults()
    {
        // demos yes; web-resources yes, empty namespace -> default (prefix lower-cased), empty folder -> "dist".
        bool ok = Parse("y\nn\nn\nn\ny\n\n\n",
            ["--name", "DemoSol", "--prefix", "Demo", "--output", ".", "--interactive"],
            out ScaffoldOptions options);

        Assert.True(ok);
        Assert.True(options.IncludeWebResources);
        Assert.Equal(ScaffoldOptions.SuggestGlobalName("Demo"), options.WebResourceNamespace); // "demo"
        Assert.Equal("dist", options.WebResourcesFolder);
    }

    [Fact]
    public void Interactive_Prompts_For_Name_Prefix_And_Output()
    {
        // No name/prefix/output flags: prompt them (prefix + output empty -> defaults), then decline samples, tests
        // and web resources.
        bool ok = Parse("MySol\n\n\nn\nn\nn\n", ["--interactive"], out ScaffoldOptions options);

        Assert.True(ok);
        Assert.Equal("MySol", options.SolutionName);
        Assert.Equal(ScaffoldOptions.SuggestPrefix("MySol"), options.Prefix);
        Assert.Equal(".", options.OutputDirectory);
        Assert.False(options.IncludeWebResources);
        Assert.False(options.IncludeSamples);
        Assert.False(options.IncludeTests);
    }

    [Fact]
    public void Interactive_Samples_PromptsForAPublisherPrefix_WhenTheDerivedOneIsInvalid()   // review
    {
        // "ZavaSales" derives "zavasales" (9 characters). Prompts: samples(y), integrations(n), tests(n), console(n),
        // then the publisher prefix, then web-resources(n).
        bool ok = Parse("y\nn\nn\nn\nZS\nn\n",
            ["--name", "ZavaSales", "--prefix", "ZavaSales", "--output", ".", "--interactive"],
            out ScaffoldOptions options);

        Assert.True(ok);
        Assert.True(options.IncludeSamples);
        Assert.Equal("zs", options.PublisherPrefix);
    }

    [Fact]
    public void Interactive_DefaultAnswers_AreMinimal_WithTests()   // A-53
    {
        // Empty answers take the defaults: samples no (so no Integrations/ConsoleApp prompt), tests yes, web no.
        bool ok = Parse("\n\n\n",
            ["--name", "DemoSol", "--prefix", "Demo", "--output", ".", "--interactive"],
            out ScaffoldOptions options);

        Assert.True(ok);
        Assert.False(options.IncludeSamples);
        Assert.False(options.IncludeIntegrations);
        Assert.False(options.IncludeConsole);
        Assert.True(options.IncludeTests);
        Assert.False(options.IncludeWebResources);
    }

    [Fact]
    public void Interactive_Integrations_Yes_Prompts_ManagedIdentity_And_WebResources_Declined()
    {
        // demos yes; integrations yes -> managed-identity prompt; then web-resources declined.
        bool ok = Parse("y\ny\ny\ny\ny\nn\n",
            ["--name", "DemoSol", "--prefix", "Demo", "--output", ".", "--interactive"],
            out ScaffoldOptions options);

        Assert.True(ok);
        Assert.True(options.IncludeIntegrations);
        Assert.True(options.ManagedIdentity);
        Assert.False(options.IncludeWebResources);
    }

    [Fact]
    public void Interactive_IntegrationsFlag_WithoutSamples_KeepsTheProject_AndAsksForManagedIdentity()
    {
        // Prompts: demos(n), tests(default yes), managed-identity(y), web(n). No Integrations prompt: the flag asked for it.
        bool ok = Parse("n\n\ny\nn\n",
            ["--name", "DemoSol", "--prefix", "Demo", "--output", ".", "--interactive", "--integrations"],
            out ScaffoldOptions options);

        Assert.True(ok);
        Assert.False(options.IncludeSamples);
        Assert.True(options.IncludeIntegrations);
        Assert.True(options.ManagedIdentity);
        Assert.True(options.IncludeTests);
        Assert.False(options.IncludeWebResources);
    }

    [Fact]
    public void Interactive_Demos_Yes_StillHonors_ExplicitNoTests()
    {
        // demos=yes must not resurrect a project excluded on the command line: --no-tests stays off, and the
        // tests prompt is skipped. Prompts: demos(y), integrations(y), console(y), managed-identity(n), web(n).
        bool ok = Parse("y\ny\ny\nn\nn\n",
            ["--name", "DemoSol", "--prefix", "Demo", "--output", ".", "--interactive", "--no-tests"],
            out ScaffoldOptions options);

        Assert.True(ok);
        Assert.True(options.IncludeSamples);
        Assert.True(options.IncludeIntegrations);
        Assert.True(options.IncludeConsole);
        Assert.False(options.IncludeTests);
    }
}
