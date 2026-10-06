using System;
using System.Linq;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for opt-in tests (heavy builds, toolchains, live Dataverse) that is reported as
/// <b>skipped</b>, with the variables to set, when its environment gate is not met. Returning early from the test body
/// instead would report it as passed and hide that it never ran.
/// Each requirement is either <c>NAME</c> (the variable must be set and non-blank) or <c>NAME=value</c> (it must
/// equal <c>value</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(params string[] requirements)
    {
        string[] missing = requirements.Where(r => !IsMet(r)).ToArray();
        if (missing.Length > 0)
        {
            Skip = "Opt-in test: set " + string.Join(", ", missing) + " to run it.";
        }
    }

    internal static bool IsMet(string requirement)
    {
        int eq = requirement.IndexOf('=');
        string name = eq < 0 ? requirement : requirement.Substring(0, eq);
        string? value = Environment.GetEnvironmentVariable(name);
        return eq < 0 ? !string.IsNullOrWhiteSpace(value) : value == requirement.Substring(eq + 1);
    }
}
