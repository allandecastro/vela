using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// A-63: the help is generated from the command table (CliCommands), which is also the allowlist of the commands that
/// read it; these pin the table to the parsers that keep their own sets (init, add) and to the dispatch in Program.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class CliCommandsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string CaptureOut(Action action)
    {
        TextWriter original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return captured.ToString();
    }

    private static (int Exit, string Error) CaptureError(Func<int> action)
    {
        TextWriter original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            return (action(), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("inspect")]
    [InlineData("init")]
    [InlineData("new")]
    [InlineData("add")]
    [InlineData("models")]
    [InlineData("logs")]
    [InlineData("trace")]
    [InlineData("solution")]
    [InlineData("sign")]
    [InlineData("logout")]
    [InlineData("update")]
    [InlineData("menu")]
    [InlineData("version")]
    [InlineData("help")]
    public void Every_dispatched_command_is_in_the_table(string name) => Assert.NotNull(CliCommands.Find(name));

    [Fact]
    public void Commands_and_option_spellings_are_unique_and_described()
    {
        Assert.Equal(CliCommands.All.Count, CliCommands.All.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (CliCommand command in CliCommands.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Summary));
            Assert.NotEmpty(command.Usage);
            string[] spellings = command.Options.SelectMany(o => o.Spellings).ToArray();
            Assert.True(spellings.Length == spellings.Distinct(StringComparer.OrdinalIgnoreCase).Count(), $"duplicate option in '{command.Name}'");
            Assert.All(command.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
            Assert.All(command.Options.Where(o => o.Kind != CliOptionKind.Flag), o => Assert.False(string.IsNullOrWhiteSpace(o.ValueName)));
        }
    }

    [Fact]
    public void The_add_table_matches_the_add_parser()
    {
        Assert.Equal(Sorted(AddCommand.ValueOptions), Sorted(CliCommands.ValueOptions("add")));
        Assert.Equal(Sorted(AddCommand.FlagOptions), Sorted(CliCommands.FlagOptions("add")));
    }

    [Fact]
    public void The_init_table_matches_the_init_parser()
    {
        Assert.Equal(Sorted(PrivateSet(typeof(ScaffoldOptions), "ValueOptions")), Sorted(CliCommands.ValueOptions("init")));
        Assert.Equal(Sorted(PrivateSet(typeof(ScaffoldOptions), "FlagOptions")), Sorted(CliCommands.FlagOptions("init")));
    }

    [Fact]
    public void The_connection_options_are_the_ones_every_connected_command_accepts()
    {
        Assert.Equal(
            Sorted(["--connection-string", "--url", "--tenant-id", "--client-id", "--client-secret", "--auth-client-id", "--authority", "--timeout"]),
            Sorted(CommandLineOptions.ConnectionValueOptions));
        Assert.Equal(Sorted(["--client-secret-stdin", "--fresh-login", "--interactive", "-i"]), Sorted(CommandLineOptions.ConnectionFlagOptions));
        foreach (string command in new[] { "plan", "apply", "inspect", "models", "logs", "solution" })
        {
            Assert.Superset(new HashSet<string>(CommandLineOptions.ConnectionValueOptions, StringComparer.OrdinalIgnoreCase), CliCommands.ValueOptions(command));
        }
    }

    [Fact]
    public void Models_bare_value_options_are_the_optional_ones()
        => Assert.Equal(Sorted(["--custom-apis", "--messages"]), Sorted(CliCommands.OptionalValueOptions("models")));

    [Theory]
    [InlineData("plan", "--auto-approve")]
    [InlineData("plan", "--yes")]
    [InlineData("plan", "--batch-size")]
    [InlineData("apply", "--detailed-exitcode")]
    [InlineData("inspect", "--strict")]
    [InlineData("inspect", "--auto-approve")]
    public void Each_of_plan_apply_inspect_rejects_the_options_of_the_others(string command, string option)
    {
        (int exit, string error) = CaptureError(() =>
            CommandLineOptions.TryParseSharedOptions(command, [option, "1"], out _, out int code) ? 0 : code);

        Assert.Equal(CliExitCodes.ExitInvalidArguments, exit);
        Assert.Contains($"Unknown option '{option}'", error);
    }

    [Fact]
    public void Update_rejects_an_unknown_option_instead_of_running()
    {
        (int exit, string error) = CaptureError(() => UpdateCommand.Run(["--bogus"]));

        Assert.Equal(CliExitCodes.ExitInvalidArguments, exit);
        Assert.Contains("Unknown option '--bogus'", error);
    }

    [Fact]
    public void Each_command_help_lists_every_option_and_its_usage()
    {
        foreach (CliCommand command in CliCommands.All)
        {
            string help = CaptureOut(() => CliCommands.PrintCommandHelp(command.Name));

            Assert.StartsWith($"vela {command.Name}: {command.Summary}", help);
            Assert.All(command.Usage, usage => Assert.Contains(usage, help));
            Assert.All(command.Options.SelectMany(o => o.Spellings), spelling => Assert.Contains(spelling, help));
            Assert.All(command.OptionGroups, group => Assert.Contains(group.Group + ":", help));
        }
    }

    [Theory]
    [InlineData("--strict", "Fail (exit 4)")]
    [InlineData("--package, -p <file.nupkg>", "A built plugin package (repeatable).")]
    [InlineData("--detailed-exitcode", "Exit 8")]
    public void Plan_help_aligns_each_option_with_its_description(string signature, string description)
    {
        string help = CaptureOut(() => CliCommands.PrintCommandHelp("plan"));
        string line = Assert.Single(help.Split('\n'), l => l.TrimStart().StartsWith(signature + " ", StringComparison.Ordinal));
        Assert.Contains(description, line);
    }

    [Fact]
    public void Solution_show_examples_feed_the_publisher_prefix_to_a_publisher_prefix_option()
    {
        // `solution show` prints the publisher prefix: `init --prefix` is the namespace prefix, so an init example
        // must take it through --publisher-prefix (add customapi/test --prefix is the publisher prefix).
        string[] examples = [.. CliCommands.All.SelectMany(c => c.Examples ?? []).Where(e => e.Contains("solution show", StringComparison.Ordinal))];

        Assert.Contains(examples, e => e.StartsWith("vela init ", StringComparison.Ordinal));
        foreach (string example in examples)
        {
            if (example.StartsWith("vela init ", StringComparison.Ordinal))
            {
                Assert.Contains("--publisher-prefix \"$(vela solution show", example);
                Assert.DoesNotContain("--prefix \"$(vela solution show", example);
            }
            else
            {
                Assert.StartsWith("vela add customapi ", example);
            }
        }
    }

    [Fact]
    public void Help_shows_aliases_notes_and_examples()
    {
        string logs = CaptureOut(() => CliCommands.PrintCommandHelp("trace"));

        Assert.StartsWith("vela logs:", logs);
        Assert.Contains("Aliases: trace", logs);
        Assert.Contains("Examples:", logs);
        Assert.Contains("vela logs --errors --since 1h -i", logs);
        Assert.Contains("Notes:", CaptureOut(() => CliCommands.PrintCommandHelp("update")));
        Assert.DoesNotContain("Aliases:", CaptureOut(() => CliCommands.PrintCommandHelp("logout")));
    }

    [Fact]
    public void The_overview_lists_every_command_and_the_exit_codes()
    {
        string overview = CaptureOut(CliCommands.PrintOverview);

        Assert.All(CliCommands.All, c => Assert.Contains(c.Name, overview));
        Assert.Contains("solution", overview);
        Assert.Contains("8 changes pending", overview);
        Assert.Contains("7 partial apply", overview);
    }

    [Fact]
    public void Help_json_describes_every_command_option_and_exit_code()
    {
        int exit = 0;
        string output = CaptureOut(() => exit = CliCommands.RunHelp(["--json"], JsonOptions));

        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        JsonElement root = JsonDocument.Parse(output).RootElement;
        Assert.Equal(MachineOutput.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(CliCommands.All.Count, root.GetProperty("commands").GetArrayLength());
        JsonElement plan = root.GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "plan");
        JsonElement package = plan.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "--package");
        Assert.Equal("value", package.GetProperty("kind").GetString());
        Assert.Equal("file.nupkg", package.GetProperty("value").GetString());
        Assert.Equal("-p", Assert.Single(package.GetProperty("aliases").EnumerateArray()).GetString());
        JsonElement models = root.GetProperty("commands").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "models");
        Assert.Contains(models.GetProperty("options").EnumerateArray(), o => o.GetProperty("kind").GetString() == "optional-value");
        Assert.Contains(plan.GetProperty("options").EnumerateArray(), o => o.GetProperty("kind").GetString() == "flag");
        Assert.Equal("changes pending (plan --detailed-exitcode)", root.GetProperty("exitCodes").GetProperty("8").GetString());
        Assert.Contains("<file.nupkg>", output); // readable, not <-escaped
    }

    [Fact]
    public void Help_for_a_command_or_the_overview()
    {
        int exit = -1;
        Assert.StartsWith("vela models:", CaptureOut(() => exit = CliCommands.RunHelp(["models"], JsonOptions)));
        Assert.Equal(CliExitCodes.ExitSuccess, exit);
        Assert.Contains("Commands:", CaptureOut(() => exit = CliCommands.RunHelp([], JsonOptions)));
        Assert.Equal(CliExitCodes.ExitSuccess, exit);
    }

    [Fact]
    public void Help_for_an_unknown_command_is_an_invalid_argument()
    {
        (int exit, string error) = CaptureError(() => CliCommands.RunHelp(["nope"], JsonOptions));

        Assert.Equal(CliExitCodes.ExitInvalidArguments, exit);
        Assert.Contains("Unknown command 'nope'", error);
    }

    [Fact]
    public void Looking_up_an_unknown_command_table_throws()
        => Assert.Throws<ArgumentException>(() => CliCommands.ValueOptions("nope"));

    [Theory]
    [InlineData(0, "--force")]
    [InlineData(1, "--out, -o <dir>")]
    [InlineData(2, "--out, -o [dir]")]
    public void Signature_shows_how_the_option_takes_its_value(int kindValue, string expected)
    {
        var kind = (CliOptionKind)kindValue;
        CliOption option = kind == CliOptionKind.Flag
            ? new CliOption("--force", kind, "d")
            : new CliOption("--out", kind, "d", "dir", ["-o"]);

        Assert.Equal(expected, option.Signature);
    }

    [Fact]
    public void A_value_option_without_a_value_name_shows_a_placeholder()
        => Assert.Equal("--x <value>", new CliOption("--x", CliOptionKind.Value, "d").Signature);

    private static string[] Sorted(IEnumerable<string> values) =>
        values.Select(v => v.ToLowerInvariant()).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    private static HashSet<string> PrivateSet(Type type, string field) =>
        (HashSet<string>)type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
}
