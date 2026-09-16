using System;
using System.IO;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

namespace Vela.Cli.Tests;

/// <summary>
/// The client-side scopes (<c>--forms</c> / <c>--tables</c>) emit TypeScript only, so <c>--lang</c> is optional and
/// defaults to <c>ts</c>. These assert on the failure message (captured from stderr) to tell the language gate apart
/// from the later connection gate. They swap the process-global <see cref="Console.Error"/>, so they run serially.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ModelsLangOptionalTests
{
    private static string RunCapturingStderr(string[] args, out int exitCode)
    {
        TextWriter original = Console.Error;
        StringWriter captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            exitCode = ModelsCommand.Run(args);
        }
        finally
        {
            Console.SetError(original);
        }

        return captured.ToString();
    }

    // Regression: --solution-roles used to be mis-registered as a value option (in ValueOptions but read via
    // HasFlag and not in OptionalValueOptions), so it errored "requires a value" in every form. It is a flag
    // combined with --solution, so alone it must reach the "requires --solution" gate, not the tokenizer.
    [Fact]
    public void SolutionRoles_is_a_flag_not_a_value_option()
    {
        string err = RunCapturingStderr(["--solution-roles"], out int exit);
        Assert.Equal(ExitInvalidArguments, exit);
        Assert.Contains("requires --solution", err);
        Assert.DoesNotContain("requires a value", err);
    }

    [Theory]
    [InlineData("--forms")]
    [InlineData("--tables")]
    public void Lang_Is_Optional_And_Defaults_To_Ts(string scope)
    {
        // No --lang: the command clears the language gate (TypeScript by default) and only stops later, at the
        // connection gate - so the error is about the missing connection, not about --lang.
        string stderr = RunCapturingStderr([scope, "--entities", "account"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("Missing Dataverse auth", stderr);
        Assert.DoesNotContain("--lang", stderr);
    }

    [Theory]
    [InlineData("--forms")]
    [InlineData("--tables")]
    public void Explicit_Non_Ts_Lang_Is_Rejected(string scope)
    {
        string stderr = RunCapturingStderr([scope, "--lang", "csharp", "--entities", "account"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("only supports TypeScript output", stderr);
    }

    [Fact]
    public void Forms_And_Tables_Can_Be_Combined()
    {
        // --forms and --tables together are accepted (one run, one barrel): the command clears the scope/language
        // gates and only stops later at the connection gate, not with an "either/or" rejection.
        string stderr = RunCapturingStderr(["--forms", "--tables", "--entities", "account"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("Missing Dataverse auth", stderr);
        Assert.DoesNotContain("not both", stderr);
    }

    [Fact]
    public void Messages_BareFlag_CountsAsScope_ReachesConnectionGate()
    {
        // A bare --messages (no list) = ALL mode. It must clear the scope gate (not "Specify a scope") and, with a
        // namespace given, reach the connection gate. --namespace after --messages also proves the bare flag does
        // not swallow the following option as its value.
        string stderr = RunCapturingStderr(["--messages", "--namespace", "Acme.Models"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("Missing Dataverse auth", stderr);
        Assert.DoesNotContain("Specify a scope", stderr);
    }

    [Fact]
    public void Messages_Targeted_CountsAsScope_ReachesConnectionGate()
    {
        // A targeted --messages list is a scope too; it clears the scope gate and reaches the connection gate.
        string stderr = RunCapturingStderr(["--messages", "QualifyLead,AddToQueue", "--namespace", "Acme.Models"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("Missing Dataverse auth", stderr);
        Assert.DoesNotContain("Specify a scope", stderr);
    }

    [Fact]
    public void CustomApis_BareFlag_CountsAsScope_ReachesConnectionGate()
    {
        // A bare --custom-apis (no list) = ALL mode (every Custom API). It must clear the scope gate and, with a
        // namespace given, reach the connection gate; --namespace after it proves the bare flag does not swallow it.
        string stderr = RunCapturingStderr(["--custom-apis", "--namespace", "Acme.Models"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("Missing Dataverse auth", stderr);
        Assert.DoesNotContain("Specify a scope", stderr);
    }

    [Fact]
    public void CustomApis_Targeted_CountsAsScope_ReachesConnectionGate()
    {
        string stderr = RunCapturingStderr(["--custom-apis", "zava_DoThing", "--namespace", "Acme.Models"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("Missing Dataverse auth", stderr);
        Assert.DoesNotContain("Specify a scope", stderr);
    }

    [Fact]
    public void NoScope_Fails_WithSpecifyScope()
    {
        // Sanity check the other side of the gate: with no scope at all, the scope error is what surfaces.
        string stderr = RunCapturingStderr(["--namespace", "Acme.Models"], out int exitCode);

        Assert.Equal(ExitInvalidArguments, exitCode);
        Assert.Contains("Specify a scope", stderr);
    }
}
