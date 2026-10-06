using System.Collections.Generic;
using Spectre.Console;

namespace Vela.Cli;

/// <summary>
/// Spectre.Console-based interactive menu shown when a developer runs <c>vela</c> with no arguments in a
/// terminal (or runs <c>vela menu</c>). It gathers inputs and returns a synthesized argument array that
/// is dispatched through the normal command switch, so there is a single execution path.
/// </summary>
internal static class InteractiveMenu
{
    private const string Init = "Scaffold a new solution (init)";
    private const string Add = "Add to a project (add)";
    private const string Models = "Generate models (models)";
    private const string Plan = "Preview changes (plan)";
    private const string Apply = "Apply changes (apply)";
    private const string Inspect = "Inspect environment (inspect)";
    private const string Logout = "Sign out (logout)";
    private const string Exit = "Exit";

    private static readonly System.Text.RegularExpressions.Regex Identifier =
        new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Runs the menu and returns the argument array for the chosen command, or <c>null</c> if the user
    /// chose to exit.
    /// </summary>
    public static string[]? Prompt()
    {
        WriteBanner();

        string choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold #6366f1]vela[/] — what would you like to do?")
                .AddChoices(Init, Add, Models, Plan, Apply, Inspect, Logout, Exit));

        return choice switch
        {
            Init => PromptInit(),
            Add => ["add", .. PromptAdd()],
            Models => PromptModels(),
            Plan => ["plan", "--interactive"],
            Apply => ComposeApplyArgs(AnsiConsole.Confirm("Execute changes against Dataverse now? (No = dry-run preview)", false)),
            Inspect => ["inspect", "--interactive"],
            Logout => ["logout"],
            _ => null,
        };
    }

    /// <summary>Prompts for a <c>vela models</c> scope and returns the argument array (interactive connection).</summary>
    public static string[] PromptModels()
    {
        string scope = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Generate models for...")
                .AddChoices("A solution", "Specific tables"));

        if (scope == "A solution")
        {
            string solution = AnsiConsole.Prompt(new TextPrompt<string>("Solution unique name:"));
            return ComposeModelsArgs(bySolution: true, solution);
        }

        string entities = AnsiConsole.Prompt(
            new TextPrompt<string>("Tables (comma-separated logical names, e.g. account,contact):"));
        return ComposeModelsArgs(bySolution: false, entities);
    }

    /// <summary>Builds the <c>models</c> argument array from a scope choice. Pure, for testability.</summary>
    public static string[] ComposeModelsArgs(bool bySolution, string value) =>
        bySolution
            ? ["models", "--solution", value, "--interactive"]
            : ["models", "--entities", value, "--interactive"];

    /// <summary>
    /// Prompts for a <c>vela add</c> operation and returns the subcommand args (without the leading
    /// <c>add</c>), ready to feed back through <see cref="AddCommand"/>.
    /// </summary>
    public static string[] PromptAdd()
    {
        string kind = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to add?")
                .AddChoices("plugin", "service", "customapi", "test", "step"));

        string name = AnsiConsole.Prompt(
            new TextPrompt<string>(kind == "test" || kind == "step" ? "Class under test/edit:" : "Name:")
                .Validate(v => Identifier.IsMatch(v)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Use a valid C# identifier.[/]")));

        string? entity = null;
        string? message = null;
        string? handler = null;
        string? testKind = null;
        string? filteringAttributes = null;
        bool function = false;

        switch (kind)
        {
            case "plugin":
                entity = PromptEntity();
                message = PromptMessage();
                filteringAttributes = PromptFilteringAttributes(message);
                break;
            case "service":
                entity = PromptEntity();
                break;
            case "step":
                entity = PromptEntity();
                message = PromptMessage();
                filteringAttributes = PromptFilteringAttributes(message);
                break;
            case "customapi":
                if (AnsiConsole.Confirm("Bind it to a table?", false))
                {
                    entity = PromptEntity();
                }

                function = AnsiConsole.Confirm("Function (returns data)? [grey](No = Action)[/]", false);
                break;
            case "test":
                testKind = AnsiConsole.Prompt(
                    new SelectionPrompt<string>().Title("A test for which kind?").AddChoices("plugin", "customapi"));
                if (testKind == "plugin" || AnsiConsole.Confirm("Is the Custom API bound to a table?", false))
                {
                    entity = PromptEntity();
                }

                break;
        }

        return ComposeAddArgs(kind, name, entity, message, handler, testKind, function, filteringAttributes);
    }

    private static string PromptEntity() =>
        AnsiConsole.Prompt(
            new TextPrompt<string>("Entity model type (e.g. Account):")
                .Validate(v => Identifier.IsMatch(v)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Use the early-bound class name, e.g. Account.[/]")));

    private static string PromptMessage() =>
        AnsiConsole.Prompt(new TextPrompt<string>("Message:").DefaultValue("Create"));

    // Create/Update steps must FilterOn, so the wizard asks for the column(s) instead of producing a failing command.
    private static string? PromptFilteringAttributes(string message) =>
        AddCommand.RequiresFilter(message)
            ? AnsiConsole.Prompt(new TextPrompt<string>("Filtering attributes (early-bound property names, comma-separated, e.g. Name):"))
            : null;

    /// <summary>Builds the <c>add</c> subcommand argument array (no leading <c>add</c>). Pure, for testability.</summary>
    public static string[] ComposeAddArgs(string kind, string name, string? entity, string? message, string? handler, string? testKind, bool function, string? filteringAttributes = null)
    {
        List<string> args = [kind, name];
        if (!string.IsNullOrWhiteSpace(testKind))
        {
            args.Add("--kind");
            args.Add(testKind!);
        }

        if (!string.IsNullOrWhiteSpace(entity))
        {
            args.Add("--entity");
            args.Add(entity!);
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("--message");
            args.Add(message!);
        }

        if (!string.IsNullOrWhiteSpace(filteringAttributes))
        {
            args.Add("--filtering-attributes");
            args.Add(filteringAttributes!);
        }

        if (!string.IsNullOrWhiteSpace(handler))
        {
            args.Add("--handler");
            args.Add(handler!);
        }

        if (function)
        {
            args.Add("--function");
        }

        return [.. args];
    }

    private static string[] PromptInit()
    {
        string name = AnsiConsole.Prompt(
            new TextPrompt<string>("Solution name (example: ZavaSales):")
                .Validate(v => ScaffoldOptions.IsValidSolutionName(v)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Use letters, digits, '.', '-' or '_'.[/]")));

        string prefix = AnsiConsole.Prompt(
            new TextPrompt<string>("Namespace prefix:")
                .DefaultValue(ScaffoldOptions.SuggestPrefix(name))
                .Validate(v => ScaffoldOptions.IsValidPrefix(v)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Use a dotted identifier such as 'Zava' or 'Zava.CRM'.[/]")));

        string output = AnsiConsole.Prompt(
            new TextPrompt<string>("Output directory:").DefaultValue("."));

        bool integrations = AnsiConsole.Confirm("Generate an Integrations project?", true);
        bool tests = AnsiConsole.Confirm("Generate a unit-test project (Vela.Testing)?", true);
        bool console = AnsiConsole.Confirm("Generate a ConsoleApp project?", true);

        return ComposeInitArgs(name, prefix, output, integrations, tests, console);
    }

    /// <summary>Builds the <c>init</c> argument array from collected inputs. Pure, for testability.</summary>
    public static string[] ComposeInitArgs(string name, string prefix, string output, bool integrations, bool tests, bool console)
    {
        List<string> args = ["init", "--name", name, "--prefix", prefix, "--output", output];
        if (!integrations)
        {
            args.Add("--no-integrations");
        }

        if (!tests)
        {
            args.Add("--no-tests");
        }

        if (!console)
        {
            args.Add("--no-console");
        }

        return [.. args];
    }

    /// <summary>Builds the argument array for the apply choice. Pure, for testability. The menu already asked
    /// for confirmation, so "execute" applies non-interactively (--auto-approve); "No" previews via plan.</summary>
    public static string[] ComposeApplyArgs(bool execute) =>
        execute ? ["apply", "--interactive", "--auto-approve"] : ["plan", "--interactive"];

    private static void WriteBanner()
    {
        // Brand indigo (#6366F1) for the wordmark; cyan (#22D3EE) version tag echoes the brand gradient.
        AnsiConsole.Write(new FigletText(ToolInfo.ToolName).LeftJustified().Color(new Color(99, 102, 241)));
        AnsiConsole.MarkupLine($"[grey]{ToolInfo.ProductName}[/]  [#22d3ee]v{ToolInfo.Version}[/]  [grey]by {ToolInfo.Author}[/]");
        AnsiConsole.WriteLine();
    }
}
