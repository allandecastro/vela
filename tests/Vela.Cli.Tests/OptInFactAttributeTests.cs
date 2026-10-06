using System;
using Xunit;

namespace Vela.Cli.Tests;

public class OptInFactAttributeTests
{
    // Unique names: environment variables are process-wide and tests run in parallel.
    private const string Flag = "VELA_TEST_OPTIN_FLAG";
    private const string Value = "VELA_TEST_OPTIN_VALUE";

    [Fact]
    public void Unset_Requirements_Skip_And_Name_Each_Missing_Variable()
    {
        Environment.SetEnvironmentVariable(Flag, null);
        Environment.SetEnvironmentVariable(Value, null);

        var fact = new OptInFactAttribute(Flag + "=1", Value);

        Assert.Equal($"Opt-in test: set {Flag}=1, {Value} to run it.", fact.Skip);
    }

    [Fact]
    public void Met_Requirements_Run_The_Test()
    {
        try
        {
            Environment.SetEnvironmentVariable(Flag, "1");
            Environment.SetEnvironmentVariable(Value, "anything");

            Assert.Null(new OptInFactAttribute(Flag + "=1", Value).Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Flag, null);
            Environment.SetEnvironmentVariable(Value, null);
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" ", false)]
    public void Value_Requirement_Matches_Exactly_And_Name_Requirement_Rejects_Blank(string set, bool expected)
    {
        string name = Flag + "_" + expected + "_" + set.Trim();
        try
        {
            Environment.SetEnvironmentVariable(name, set);

            Assert.Equal(expected, OptInFactAttribute.IsMet(name + "=1"));
            Assert.Equal(!string.IsNullOrWhiteSpace(set), OptInFactAttribute.IsMet(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
