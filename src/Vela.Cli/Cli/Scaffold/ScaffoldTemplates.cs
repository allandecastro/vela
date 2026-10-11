using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Vela.Cli;

/// <summary>
/// Pure file-content generators for <c>vela init</c> and <c>vela add</c>. Every method returns the text of a
/// single generated file and performs no I/O, which keeps templates independently unit-testable.
/// Raw interpolated string literals (<c>$$"""..."""</c>) are used so the emitted C# braces stay
/// literal and only <c>{{token}}</c> placeholders interpolate.
/// </summary>
/// <remarks>
/// The class is split by concern: this file holds the shared constants and the solution-wide root files;
/// <c>Scaffold/ScaffoldTemplates.Projects.cs</c> the project files; <c>.Readme.cs</c> the generated READMEs;
/// <c>.Add.cs</c> the single-file <c>vela add</c> templates; <c>.Samples*.cs</c> / <c>.SampleTests.cs</c> the
/// opt-in <c>vela init --samples</c> demo code; <c>.WebResources.cs</c> the TypeScript web-resources project.
/// </remarks>
internal static partial class ScaffoldTemplates
{
    // Version of the Power Platform plugin MSBuild SDK package. Bump as new releases ship; this only
    // affects newly generated solutions, not this repository.
    public const string PluginSdkVersion = "1.41.1";

    // Cross-platform Dataverse client used by the Shared (netstandard2.0) and ConsoleApp projects.
    public const string DataverseClientVersion = "1.2.10";

    // The Dataverse client pulls System.Security.Cryptography.Xml in transitively at a version with known
    // advisories (NU1903); pin the patched build in the projects that reference the client (Shared, ConsoleApp).
    public const string CryptographyXmlVersion = "8.0.4";

    // .NET Framework Dataverse SDK reference assemblies for plugin (net462) compilation.
    public const string CrmCoreAssembliesVersion = "9.0.2.59";

    // Test stack for the generated *.Plugins.Tests project. These (and Vela.Plugins / Vela.Testing) resolve
    // from nuget.org, the default package source; the scaffold emits no nuget.config.
    public const string TestSdkVersion = "17.12.0";
    public const string XunitVersion = "2.9.2";
    public const string XunitRunnerVersion = "2.8.2";

    private const string CSharpProjectTypeGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";

    /// <summary>The Visual Studio JavaScript/TypeScript project system (.esproj) type id.</summary>
    public const string JavaScriptProjectTypeGuid = "54A90642-561A-4BB1-A94E-469ADEE60C69";

    /// <summary>A project to be listed in the generated solution file. The type id defaults to C#.</summary>
    public sealed record ProjectEntry(string Name, string RelativePath, Guid ProjectGuid, string ProjectTypeGuid = CSharpProjectTypeGuid);

    // The Custom API unique-name publisher prefix is derived from the namespace prefix (lower-cased,
    // dots removed) so it is a valid Dataverse customization prefix.
    public static string ApiPrefix(string prefix) =>
        prefix.Replace(".", string.Empty).ToLowerInvariant();

    // ---- Root files -------------------------------------------------------

    public static string Gitignore() =>
$$"""
## .NET / Visual Studio
bin/
obj/
*.user
*.suo
.vs/
artifacts/

## Signing - never commit a code-signing certificate
*.pfx
""";

    public static string DirectoryBuildProps() =>
$$"""
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
    <!-- Early-bound models and plugin code follow the classic non-nullable Dataverse conventions. -->
    <Nullable>disable</Nullable>
    <!-- These projects pin package versions inline. Disable central package management explicitly so the solution
         builds even if it is created inside a repository that has its own Directory.Packages.props. -->
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
""";

    // A solution-root Directory.Packages.props. MSBuild imports the NEAREST Directory.Packages.props walking up the
    // folder tree, and the mere presence of one with ManagePackageVersionsCentrally=true switches on Central Package
    // Management. Emitting this file makes the scaffold's own the nearest one, shadowing any parent inherited from an
    // enclosing repository and keeping CPM off - so the inline PackageReference versions stay valid (no NU1008). To
    // adopt CPM, set this to true and move each version to a <PackageVersion> item here. (#315)
    public static string DirectoryPackagesProps() =>
"""
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
""";

    public static string Solution(IReadOnlyList<ProjectEntry> projects)
    {
        StringBuilder sb = new();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        foreach (ProjectEntry project in projects)
        {
            sb.AppendLine($"Project(\"{{{project.ProjectTypeGuid}}}\") = \"{project.Name}\", \"{project.RelativePath}\", \"{{{project.ProjectGuid.ToString().ToUpperInvariant()}}}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (ProjectEntry project in projects)
        {
            string g = "{" + project.ProjectGuid.ToString().ToUpperInvariant() + "}";
            sb.AppendLine($"\t\t{g}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{g}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{g}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{g}.Release|Any CPU.Build.0 = Release|Any CPU");
        }

        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        sb.AppendLine("\t\tHideSolutionNode = FALSE");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");
        return sb.ToString();
    }

    // Deterministic GUID from a name (MD5-derived) so re-running init produces a stable solution file.
    public static Guid StableGuid(string name)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash);
    }
}
