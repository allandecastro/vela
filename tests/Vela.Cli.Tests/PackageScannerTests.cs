using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

// PackageScanner discovers plugin/custom-api types by NAME and SHAPE (a base type whose simple name is exactly
// "EntityPluginBase"/"CustomApiBase", a (string,string) ctor, and a definition method), not by the real Core
// types. These stand-ins reproduce that shape so the reflection logic can be tested without a built .nupkg or a
// Core reference.
public class PackageScannerTests
{
    public abstract class EntityPluginBase
    {
    }

    public abstract class CustomApiBase
    {
    }

    // A decoy whose base-type name only STARTS WITH the marker: it must NOT be mistaken for a plugin (the old
    // prefix match wrongly accepted it, #305/#303).
    public abstract class EntityPluginBaseHelper
    {
    }

    public sealed class NotAPluginDespiteName : EntityPluginBaseHelper
    {
        public NotAPluginDespiteName(string unsecure, string secure)
        {
            _ = unsecure;
            _ = secure;
        }

        public object[] GetStepDefinitions() => [new { message = "Update" }];
    }

    public sealed class GoodStepPlugin : EntityPluginBase
    {
        public GoodStepPlugin(string unsecure, string secure)
        {
            _ = unsecure;
            _ = secure;
        }

        public object[] GetStepDefinitions() => [new { message = "Update", stage = 40 }];
    }

    public sealed class GoodCustomApi : CustomApiBase
    {
        public GoodCustomApi(string unsecure, string secure)
        {
            _ = unsecure;
            _ = secure;
        }

        public object GetCustomApiDefinition() => new { uniqueName = "zava_Api" };
    }

    public sealed class MissingCtorPlugin : EntityPluginBase
    {
        public object[] GetStepDefinitions() => [new { message = "Update" }];
    }

    public sealed class EmptyDefinitionsPlugin : EntityPluginBase
    {
        public EmptyDefinitionsPlugin(string a, string b)
        {
            _ = a;
            _ = b;
        }

        public object[] GetStepDefinitions() => [];
    }

    public sealed class ThrowingPlugin : EntityPluginBase
    {
        public ThrowingPlugin(string a, string b)
        {
            _ = a;
            _ = b;
        }

        public object[] GetStepDefinitions() => throw new InvalidOperationException("boom");
    }

    public sealed class NotAPlugin
    {
    }

    // ---- pure predicates ----

    [Theory]
    [InlineData("System.Text.Json", true)]
    [InlineData("Microsoft.Xrm.Sdk", true)]
    [InlineData("Newtonsoft.Json", true)]
    [InlineData("Vela.Plugins", true)]
    [InlineData("Zava.Plugins", false)]
    public void IsLikelyDependencyAssembly_ClassifiesFrameworkAssemblies(string name, bool expected)
    {
        Assert.Equal(expected, PackageScanner.IsLikelyDependencyAssembly(name));
    }

    [Theory]
    [InlineData("Zava.Plugins", true)]
    [InlineData("System.Text.Json", false)]
    [InlineData("Microsoft.Xrm.Sdk", false)]
    public void IsRegistrableAssembly_ExcludesSystemAndMicrosoft(string name, bool expected)
    {
        Assert.Equal(expected, PackageScanner.IsRegistrableAssembly(name));
    }

    // ---- type discovery ----

    [Fact]
    public void IsPluginType_MatchesConcreteSubclassByBaseName()
    {
        Assert.True(PackageScanner.IsPluginType(typeof(GoodStepPlugin), "EntityPluginBase"));
        Assert.True(PackageScanner.IsPluginType(typeof(GoodCustomApi), "CustomApiBase"));
    }

    [Fact]
    public void IsPluginType_RejectsAbstractAndUnrelatedTypes()
    {
        Assert.False(PackageScanner.IsPluginType(typeof(EntityPluginBase), "EntityPluginBase")); // abstract base itself
        Assert.False(PackageScanner.IsPluginType(typeof(NotAPlugin), "EntityPluginBase"));
        Assert.False(PackageScanner.IsPluginType(typeof(GoodStepPlugin), "CustomApiBase")); // wrong base
    }

    [Fact]
    public void IsPluginType_RejectsATypeWhoseBaseNameMerelyStartsWithTheMarker()   // #305/#303
    {
        // The base is "EntityPluginBaseHelper" - a prefix of the marker, not an exact match. It must not be treated
        // as a plugin (which the scanner would then instantiate).
        Assert.False(PackageScanner.IsPluginType(typeof(NotAPluginDespiteName), "EntityPluginBase"));
    }

    [Fact]
    public void GetLoadableTypes_ReturnsTypesFromAssembly()
    {
        var types = PackageScanner.GetLoadableTypes(typeof(GoodStepPlugin).Assembly).ToList();

        Assert.Contains(typeof(GoodStepPlugin), types);
    }

    // ---- definition extraction (happy + error paths) ----

    [Fact]
    public void TryGetDefinitions_ReturnsDefinitions_ForWellFormedPlugin()
    {
        bool ok = PackageScanner.TryGetDefinitions(typeof(GoodStepPlugin), "GetStepDefinitions", out object[]? defs, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(defs!);
    }

    [Fact]
    public void TryGetDefinitions_Fails_WhenConstructorMissing()
    {
        bool ok = PackageScanner.TryGetDefinitions(typeof(MissingCtorPlugin), "GetStepDefinitions", out _, out string? error);

        Assert.False(ok);
        Assert.Contains("constructor", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetDefinitions_Fails_WhenMethodMissing()
    {
        bool ok = PackageScanner.TryGetDefinitions(typeof(GoodStepPlugin), "NoSuchMethod", out _, out string? error);

        Assert.False(ok);
        Assert.Contains("NoSuchMethod", error);
    }

    [Fact]
    public void TryGetDefinitions_Fails_WhenDefinitionsEmpty()
    {
        bool ok = PackageScanner.TryGetDefinitions(typeof(EmptyDefinitionsPlugin), "GetStepDefinitions", out _, out string? error);

        Assert.False(ok);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetDefinitions_CapturesException_FromDefinitionMethod()
    {
        bool ok = PackageScanner.TryGetDefinitions(typeof(ThrowingPlugin), "GetStepDefinitions", out _, out string? error);

        Assert.False(ok);
        Assert.Contains("InvalidOperationException", error);
        Assert.Contains("boom", error);
    }

    [Fact]
    public void TryGetDefinition_ReturnsDefinition_ForWellFormedCustomApi()
    {
        bool ok = PackageScanner.TryGetDefinition(typeof(GoodCustomApi), "GetCustomApiDefinition", out object? def, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(def);
    }

    // ---- nuspec metadata ----

    [Fact]
    public void ReadPackageMetadata_ReadsIdAndVersion_FromNuspec()
    {
        using ZipArchive archive = BuildArchiveWithNuspec("Zava.Plugins.Package", "2.3.4");

        (string? id, string? version) = PackageScanner.ReadPackageMetadata(archive);

        Assert.Equal("Zava.Plugins.Package", id);
        Assert.Equal("2.3.4", version);
    }

    [Fact]
    public void ReadPackageMetadata_ReturnsNulls_WhenNoNuspec()
    {
        using var ms = new MemoryStream();
        using (new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        ms.Position = 0;
        using ZipArchive archive = new ZipArchive(ms, ZipArchiveMode.Read);

        (string? id, string? version) = PackageScanner.ReadPackageMetadata(archive);

        Assert.Null(id);
        Assert.Null(version);
    }

    private static ZipArchive BuildArchiveWithNuspec(string id, string version)
    {
        string nuspec = $@"<?xml version=""1.0""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>{id}</id>
    <version>{version}</version>
  </metadata>
</package>";

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry($"{id}.nuspec");
            using Stream s = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(nuspec);
            s.Write(bytes, 0, bytes.Length);
        }

        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }
}
