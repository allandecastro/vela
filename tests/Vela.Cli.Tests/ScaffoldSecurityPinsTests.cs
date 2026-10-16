using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// The scaffold pins the patched System.Security.Cryptography.* builds over the Dataverse client's advisory transitive
// versions. The repository pins them too (Directory.Packages.props, which CI's vulnerability audit covers), so the two
// must stay equal: bumping one without the other ships a scaffold that restores a vulnerable package.
public class ScaffoldSecurityPinsTests
{
    [Theory]
    [InlineData("System.Security.Cryptography.Xml", ScaffoldTemplates.CryptographyXmlVersion)]
    [InlineData("System.Security.Cryptography.Pkcs", ScaffoldTemplates.CryptographyPkcsVersion)]
    public void The_scaffold_pins_the_same_patched_version_as_the_repository(string package, string scaffoldVersion)
    {
        string props = FindRepoFile("Directory.Packages.props");
        string? repoVersion = XDocument.Load(props).Descendants("PackageVersion")
            .SingleOrDefault(e => (string?)e.Attribute("Include") == package)?.Attribute("Version")?.Value;

        Assert.Equal(repoVersion, scaffoldVersion);
    }

    [Fact]
    public void The_client_projects_pin_both_packages()
    {
        ScaffoldOptions options = new(
            SolutionName: "ZavaSales", Prefix: "Zava", OutputDirectory: ".", IncludeConsole: true, IncludeIntegrations: false,
            IncludeTests: false, CoreVersion: "1.0.0", Force: false, Interactive: false, IncludeSamples: true);

        foreach (string csproj in new[] { ScaffoldTemplates.SharedCsproj(options), ScaffoldTemplates.ConsoleCsproj(options) })
        {
            Assert.Contains($"Include=\"System.Security.Cryptography.Xml\" Version=\"{ScaffoldTemplates.CryptographyXmlVersion}\"", csproj);
            Assert.Contains($"Include=\"System.Security.Cryptography.Pkcs\" Version=\"{ScaffoldTemplates.CryptographyPkcsVersion}\"", csproj);
        }
    }

    private static string FindRepoFile(string name)
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir, "Vela.sln")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{name} not found above {AppContext.BaseDirectory}");
    }
}
