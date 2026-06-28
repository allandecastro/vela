using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void TryReadOption_ReturnsValue_AfterMatchingFlag()
    {
        string[] args = ["plan", "--url", "https://zava.crm.dynamics.com", "--strict"];

        Assert.Equal("https://zava.crm.dynamics.com", CommandLineOptions.TryReadOption(args, "--url"));
    }

    [Fact]
    public void TryReadOption_IsCaseInsensitive_OnFlagName()
    {
        string[] args = ["--Tenant-Id", "abc"];

        Assert.Equal("abc", CommandLineOptions.TryReadOption(args, "--tenant-id"));
    }

    [Fact]
    public void TryReadOption_ReturnsNull_WhenFlagMissing()
    {
        string[] args = ["--url", "x"];

        Assert.Null(CommandLineOptions.TryReadOption(args, "--client-id"));
    }

    [Fact]
    public void TryReadOption_ReturnsNull_WhenFlagIsLastArgWithoutValue()
    {
        string[] args = ["--url"];

        Assert.Null(CommandLineOptions.TryReadOption(args, "--url"));
    }

    [Fact]
    public void ReadMultiOption_CollectsAllOccurrences()
    {
        string[] args = ["--package", "a.nupkg", "--package", "b.nupkg", "--strict"];

        List<string> values = CommandLineOptions.ReadMultiOption(args, "--package");

        Assert.Equal(["a.nupkg", "b.nupkg"], values);
    }

    [Fact]
    public void ReadMultiOption_ReturnsEmpty_WhenAbsent()
    {
        string[] args = ["--strict"];

        Assert.Empty(CommandLineOptions.ReadMultiOption(args, "--package"));
    }

    [Fact]
    public void TryReadFirstPositional_ReturnsFirstNonFlagArgument()
    {
        // --url consumes its value; the first standalone token is the positional package path.
        string[] args = ["--url", "https://x", "package.nupkg"];

        Assert.Equal("package.nupkg", CommandLineOptions.TryReadFirstPositional(args));
    }

    [Fact]
    public void TryReadFirstPositional_ReturnsNull_WhenOnlyFlagsAndValues()
    {
        string[] args = ["--url", "https://x", "--strict"];

        Assert.Null(CommandLineOptions.TryReadFirstPositional(args));
    }

    [Fact]
    public void BuildClientSecretConnectionString_FormatsAllFields()
    {
        string cs = CommandLineOptions.BuildClientSecretConnectionString(
            "https://zava.crm.dynamics.com", "tenant", "client", "secret");

        Assert.Contains("AuthType=ClientSecret", cs);
        Assert.Contains("Url=https://zava.crm.dynamics.com", cs);
        Assert.Contains("TenantId=tenant", cs);
        Assert.Contains("ClientId=client", cs);
        Assert.Contains("ClientSecret=secret", cs);
        Assert.Contains("RequireNewInstance=true", cs);
    }
}
